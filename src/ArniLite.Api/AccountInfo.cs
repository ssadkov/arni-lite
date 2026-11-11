using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace ArniLite.Api
{
    public sealed class AccountInfo
    {
        public string Email { get; private set; } = string.Empty;
        public string Plan { get; private set; } = string.Empty;
        public string AccountType { get; private set; } = string.Empty;
        public string Summary => string.Join(" · ", LimitLines);
        public IList<string> LimitLines { get; } = new List<string>();
        public string ResetInfo { get; private set; } = string.Empty;
        public bool HasBudget { get; private set; }

        public static AccountInfo Parse(string body)
        {
            var root = JObject.Parse(body);
            var user = root["user"] as JObject;
            if (user == null) throw new FormatException("Сервер вернул некорректные данные аккаунта.");
            var budget = user["budget"] as JObject;
            var free = user["freeSteps"] as JObject;
            var rates = user["billingRates"] as JObject ?? root["billingRates"] as JObject;
            var info = new AccountInfo
            {
                Email = Text(user["email"]),
                Plan = Text(user["plan"]),
                HasBudget = budget != null,
            };
            info.AccountType = budget != null ? "Доступ по бюджету"
                : info.Plan == "FREE" ? "Бесплатный аккаунт" : "Тариф: " + (info.Plan.Length > 0 ? info.Plan : "не указан");
            if (budget != null)
            {
                var balance = Number(budget["balanceMicros"]);
                if (balance.HasValue) info.LimitLines.Add("Баланс: " + Money(balance.Value, rates));
                AddWindow(info.LimitLines, "5 часов", budget["fiveHour"] as JObject, rates);
                AddWindow(info.LimitLines, "7 дней", budget["weekly"] as JObject, rates);
                if ((bool?)budget["expired"] == true) info.LimitLines.Add("Срок платного доступа истёк");
                else if ((bool?)budget["enabled"] == false) info.LimitLines.Add("Платный доступ приостановлен");
                else if (TryDate(budget["validUntil"], out var until)) info.ResetInfo = "Платный доступ до " + until.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            }

            var remaining = Number(free?["remaining"]);
            var limit = Number(free?["limit"]);
            if (remaining.HasValue && limit.HasValue)
            {
                info.LimitLines.Add("Бесплатные шаги: " + remaining.Value + " из " + limit.Value);
                if (TryDate(free?["resetsAt"], out var resets))
                {
                    if (info.ResetInfo.Length > 0) info.ResetInfo += "\n";
                    info.ResetInfo += "Бесплатные шаги обновятся " + resets.ToLocalTime().ToString("dd.MM в HH:mm", CultureInfo.InvariantCulture);
                }
            }
            if (info.LimitLines.Count == 0) info.LimitLines.Add("Данные о лимитах не предоставлены");
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
            if (limit.HasValue && used.HasValue) parts.Add(Math.Max(0, limit.Value - used.Value) + " из " + limit.Value + " запросов");
            if (parts.Count > 0) lines.Add(title + ": осталось " + string.Join(" · ", parts));
        }

        private static string Money(long micros, JObject? rates)
        {
            var usd = micros / 1000000m;
            var rate = rates?["RUB"];
            if (rate != null && rate.Type != JTokenType.Null && decimal.TryParse(rate.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var rub) && rub > 0)
            {
                return "≈ " + (usd * rub).ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " ₽";
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
