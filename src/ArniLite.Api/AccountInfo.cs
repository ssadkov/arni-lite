using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using ArniLite.Core;

namespace ArniLite.Api
{
    public sealed class AccountInfo
    {
        private readonly HashSet<string> _bonusModels = new HashSet<string>(StringComparer.Ordinal);
        private string _source = string.Empty;
        public string Email { get; private set; } = string.Empty;
        public string Plan { get; private set; } = string.Empty;
        public string AccountType { get; private set; } = string.Empty;
        public string Summary => string.Join(" · ", LimitLines);
        public IList<string> LimitLines { get; } = new List<string>();
        public string ResetInfo { get; private set; } = string.Empty;
        public bool HasBudget { get; private set; }
        public string ChipText { get; private set; } = string.Empty;
        public string BalanceText { get; private set; } = string.Empty;
        public string BonusSummary { get; private set; } = string.Empty;
        public IList<string> BonusLimits { get; } = new List<string>();
        public bool HasBonus => BonusSummary.Length > 0;
        public IList<string> PanelLines { get; } = new List<string>();
        public IList<AccountWindowRow> Windows { get; } = new List<AccountWindowRow>();
        public AccountInfo Relocalize() => Parse(_source);
        public bool HasBonusFor(string modelId) => _bonusModels.Contains(modelId);

        public static AccountInfo Parse(string body)
        {
            var root = JObject.Parse(body);
            var user = root["user"] as JObject;
            if (user == null) throw new FormatException(StringCatalog.Get("InvalidAccount"));
            var budget = user["budget"] as JObject;
            var free = user["freeSteps"] as JObject;
            var rates = user["billingRates"] as JObject ?? root["billingRates"] as JObject;
            var info = new AccountInfo
            {
                _source = body,
                Email = Text(user["email"]),
                Plan = Text(user["plan"]),
                HasBudget = budget != null,
            };
            info.AccountType = budget != null ? StringCatalog.Get("BudgetAccess")
                : info.Plan == "FREE" ? StringCatalog.Get("FreeAccount") : StringCatalog.Get("PlanFormat", info.Plan.Length > 0 ? info.Plan : StringCatalog.Get("NotSpecified"));
            if (budget != null)
            {
                var balance = Number(budget["balanceMicros"]);
                if (balance.HasValue) info.LimitLines.Add(StringCatalog.Get("BalanceFormat", Money(balance.Value, rates)));
                AddWindow(info.LimitLines, StringCatalog.Get("FiveHours"), budget["fiveHour"] as JObject, rates);
                AddWindow(info.LimitLines, StringCatalog.Get("SevenDays"), budget["weekly"] as JObject, rates);
                if ((bool?)budget["expired"] == true) info.LimitLines.Add(StringCatalog.Get("PaidExpired"));
                else if ((bool?)budget["enabled"] == false) info.LimitLines.Add(StringCatalog.Get("PaidPaused"));
                else if (TryDate(budget["validUntil"], out var until)) info.ResetInfo = StringCatalog.Get("PaidUntil", until.ToLocalTime().ToString("d", StringCatalog.Culture));
                if (budget["bonus"] is JObject bonus)
                    info.FillBonus(bonus, (bool?)budget["enabled"] == true, rates);
            }

            var remaining = Number(free?["remaining"]);
            var limit = Number(free?["limit"]);
            if (remaining.HasValue && limit.HasValue)
            {
                info.LimitLines.Add(StringCatalog.Get("FreeStepsFormat", remaining.Value, limit.Value));
                if (TryDate(free?["resetsAt"], out var resets))
                {
                    if (info.ResetInfo.Length > 0) info.ResetInfo += "\n";
                    info.ResetInfo += StringCatalog.Get("FreeResetFormat", resets.ToLocalTime().ToString("g", StringCatalog.Culture));
                }
            }
            if (info.LimitLines.Count == 0) info.LimitLines.Add(StringCatalog.Get("NoLimits"));
            FillPanel(info, budget, free, rates);
            return info;
        }

        private void FillBonus(JObject bonus, bool enabled, JObject? rates)
        {
            var balance = Number(bonus["balanceMicros"]);
            var granted = Number(bonus["grantedMicros"]);
            if (!balance.HasValue || !granted.HasValue || !TryDate(bonus["expiresAt"], out var expiresAt)) return;
            if ((bool?)bonus["expired"] == true || expiresAt <= DateTimeOffset.UtcNow)
            {
                BonusSummary = StringCatalog.Get("DeepSeekBonusExpired");
                return;
            }
            if (!enabled)
            {
                BonusSummary = StringCatalog.Get("DeepSeekBonusPaused");
                return;
            }

            BonusSummary = StringCatalog.Get("DeepSeekBonusBalance", Money(balance.Value, rates), Money(granted.Value, rates),
                expiresAt.ToLocalTime().ToString("g", StringCatalog.Culture));
            AddBonusLimit("DeepSeekBonusFiveHours", bonus["fiveHour"] as JObject, balance.Value, rates);
            AddBonusLimit("DeepSeekBonusWeek", bonus["weekly"] as JObject, balance.Value, rates);
            if (balance.Value > 0 && bonus["models"] is JArray models)
            {
                foreach (var model in models)
                {
                    var id = Text(model);
                    if (id.Length > 0) _bonusModels.Add(id);
                }
            }
        }

        private void AddBonusLimit(string resource, JObject? window, long balance, JObject? rates)
        {
            var remaining = Number(window?["remainingMicros"]);
            if (remaining.HasValue)
                BonusLimits.Add(StringCatalog.Get(resource, Money(Math.Min(balance, remaining.Value), rates)));
        }

        private static void FillPanel(AccountInfo info, JObject? budget, JObject? free, JObject? rates)
        {
            var total = 0L;
            var available = 0L;
            var limitKind = 0;
            var active = false;
            if (budget != null)
            {
                var balance = Math.Max(0, Number(budget["balanceMicros"]) ?? 0);
                active = (bool?)budget["enabled"] == true && (bool?)budget["expired"] != true;
                total = active ? balance : 0;
                available = total;
                if (active)
                {
                    ConsiderWindow(budget["fiveHour"] as JObject, 1, ref available, ref limitKind);
                    ConsiderWindow(budget["weekly"] as JObject, 2, ref available, ref limitKind);
                }
            }

            info.BalanceText = StringCatalog.Get("AccountBalance", Money(total, rates));
            if (budget != null && TryDate(budget["validUntil"], out var until))
            {
                info.PanelLines.Add(until.UtcDateTime.Year >= 2099
                    ? StringCatalog.Get("AccountOpenEnded")
                    : StringCatalog.Get("AccountValidUntil", until.ToLocalTime().ToString("d MMMM yyyy", StringCatalog.Culture)));
            }
            if (active)
            {
                info.PanelLines.Add(limitKind == 0
                    ? StringCatalog.Get("AccountAvailableBalance", Money(available, rates))
                    : StringCatalog.Get("AccountAvailableLimited", Money(available, rates), limitKind == 1 ? StringCatalog.Get("FiveHours") : StringCatalog.Get("AccountWindowWeek")));
            }
            var plan = info.Plan == "FREE" || info.Plan.Length == 0 ? StringCatalog.Get("AccountFreePlan") : info.Plan;
            info.PanelLines.Add(StringCatalog.Get("AccountPlan", plan));
            var stepsRemaining = Number(free?["remaining"]);
            var stepsLimit = Number(free?["limit"]);
            if (stepsRemaining.HasValue && stepsLimit.HasValue)
            {
                info.PanelLines.Add(StringCatalog.Get("AccountSteps", stepsRemaining.Value, stepsLimit.Value));
            }
            if (budget != null)
            {
                var state = (bool?)budget["expired"] == true ? StringCatalog.Get("AccountAccessExpired")
                    : (bool?)budget["enabled"] == true ? StringCatalog.Get("AccountAccessActive") : StringCatalog.Get("AccountAccessPaused");
                info.PanelLines.Add(StringCatalog.Get("AccountPaidAccess", state));
                AddWindowRow(info, StringCatalog.Get("FiveHours"), budget["fiveHour"] as JObject, rates, total);
                AddWindowRow(info, StringCatalog.Get("AccountWindowWeek"), budget["weekly"] as JObject, rates, total);
            }

            var suffix = limitKind == 0 ? string.Empty : StringCatalog.Get("AccountAvailableNow", Money(available, rates), limitKind == 1 ? StringCatalog.Get("LimitFiveHour") : StringCatalog.Get("LimitWeek"));
            var who = info.Email.Length > 0 ? info.Email : StringCatalog.Get("AccountArni");
            info.ChipText = StringCatalog.Get("AccountChip", Money(total, rates), suffix, who);
        }

        private static void ConsiderWindow(JObject? window, int kind, ref long available, ref int limitKind)
        {
            if (window == null) return;
            var remaining = WindowRemaining(window);
            if (remaining < available)
            {
                available = remaining;
                limitKind = kind;
            }
        }

        private static long WindowRemaining(JObject? window)
        {
            if (window == null) return long.MaxValue;
            var requests = Number(window["requests"]) ?? 0;
            var requestLimit = Number(window["requestLimit"]) ?? 0;
            if (requestLimit > 0 && requests >= requestLimit) return 0;
            var limit = Number(window["limitMicros"]);
            var used = Number(window["usedMicros"]);
            if (limit.HasValue && used.HasValue) return Math.Max(0, limit.Value - used.Value);
            return Number(window["remainingMicros"]) ?? long.MaxValue;
        }

        private static void AddWindowRow(AccountInfo info, string title, JObject? window, JObject? rates, long total)
        {
            if (window == null) return;
            var used = Number(window["usedMicros"]);
            var limit = Number(window["limitMicros"]);
            var requests = Number(window["requests"]) ?? 0;
            var requestLimit = Number(window["requestLimit"]) ?? 0;
            var reset = TryDate(window["resetsAt"], out var resets)
                ? StringCatalog.Get("AccountWindowReset", resets.ToLocalTime().ToString("ddd HH:mm", StringCatalog.Culture))
                : StringCatalog.Get("AccountWindowNotStarted");
            string text;
            double share = 0;
            var showBar = false;
            if (used.HasValue && limit.HasValue)
            {
                var remaining = requestLimit > 0 && requests >= requestLimit ? 0 : Math.Max(0, limit.Value - used.Value);
                var moneyShare = limit.Value > 0 ? (double)used.Value / limit.Value : 1;
                var requestShare = requestLimit > 0 ? (double)requests / requestLimit : 1;
                share = Math.Min(1, Math.Max(0, Math.Max(moneyShare, requestShare)));
                showBar = true;
                var spent = StringCatalog.Get("AccountWindowSpent", title, Money(used.Value, rates), Money(limit.Value, rates));
                var requestsText = StringCatalog.Get("AccountWindowRequests", requests, requestLimit);
                text = remaining < total
                    ? spent + " · " + StringCatalog.Get("AccountWindowLeft", Money(remaining, rates)) + " · " + requestsText + " · " + reset
                    : StringCatalog.Get("AccountWindowCeiling", title, Money(used.Value, rates), Money(limit.Value, rates)) + " · " + requestsText + " · " + reset;
            }
            else
            {
                var parts = new List<string>();
                var remaining = Number(window["remainingMicros"]);
                if (remaining.HasValue) parts.Add(Money(remaining.Value, rates));
                if (requestLimit > 0) parts.Add(StringCatalog.Get("RequestsRemaining", Math.Max(0, requestLimit - requests), requestLimit));
                if (parts.Count == 0) return;
                text = StringCatalog.Get("WindowRemaining", title, string.Join(" · ", parts)) + " · " + reset;
                if (requestLimit > 0)
                {
                    share = Math.Min(1, (double)requests / requestLimit);
                    showBar = true;
                }
            }
            info.Windows.Add(new AccountWindowRow
            {
                Text = text,
                BarPixels = showBar ? share * 320 : 0,
                ShowBar = showBar,
                Level = share >= 1 ? "exhausted" : share >= 0.8 ? "warning" : "ok",
            });
        }

        private static void AddWindow(IList<string> lines, string title, JObject? window, JObject? rates)
        {
            if (window == null) return;
            var remaining = Number(window["remainingMicros"]);
            var limit = Number(window["requestLimit"]);
            var used = Number(window["requests"]);
            var parts = new List<string>();
            if (remaining.HasValue) parts.Add(Money(remaining.Value, rates));
            if (limit.HasValue && used.HasValue) parts.Add(StringCatalog.Get("RequestsRemaining", Math.Max(0, limit.Value - used.Value), limit.Value));
            if (parts.Count > 0) lines.Add(StringCatalog.Get("WindowRemaining", title, string.Join(" · ", parts)));
        }

        private static string Money(long micros, JObject? rates)
        {
            var usd = micros / 1000000m;
            var rate = rates?["RUB"];
            if (rate != null && rate.Type != JTokenType.Null && decimal.TryParse(rate.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rub) && rub > 0)
            {
                return "≈ " + (usd * rub).ToString("N2", StringCatalog.Culture) + " ₽";
            }
            return "$" + usd.ToString("0.00##", CultureInfo.InvariantCulture);
        }

        private static long? Number(JToken? value) => value != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.String)
            && long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : (long?)null;

        private static string Text(JToken? value) => value?.Type == JTokenType.String ? value.ToString() : string.Empty;

        public sealed class AccountWindowRow
        {
            public string Text { get; set; } = string.Empty;
            public double BarPixels { get; set; }
            public bool ShowBar { get; set; }
            public string Level { get; set; } = "ok";
        }

        private static bool TryDate(JToken? value, out DateTimeOffset date)
        {
            if (value is JValue scalar && scalar.Value is DateTimeOffset offset) { date = offset; return true; }
            if (value is JValue dateValue && dateValue.Value is DateTime time) { date = new DateTimeOffset(time); return true; }
            return DateTimeOffset.TryParse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
        }
    }
}
