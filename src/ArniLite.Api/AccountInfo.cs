using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using ArniLite.Core;

namespace ArniLite.Api
{
    public sealed class AccountInfo
    {
        private string _source = string.Empty;
        public string Email { get; private set; } = string.Empty;
        public string Plan { get; private set; } = string.Empty;
        public string AccountType { get; private set; } = string.Empty;
        public string Summary => string.Join(" · ", LimitLines);
        public IList<string> LimitLines { get; } = new List<string>();
        public string ResetInfo { get; private set; } = string.Empty;
        public bool HasBudget { get; private set; }
        public AccountInfo Relocalize() => Parse(_source);

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
            return info;
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

        private static bool TryDate(JToken? value, out DateTimeOffset date)
        {
            if (value is JValue scalar && scalar.Value is DateTimeOffset offset) { date = offset; return true; }
            if (value is JValue dateValue && dateValue.Value is DateTime time) { date = new DateTimeOffset(time); return true; }
            return DateTimeOffset.TryParse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date);
        }
    }
}
