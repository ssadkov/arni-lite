using System;
using System.Globalization;

namespace ArniLite.Api
{
    public sealed class CatalogModel
    {
        // OpenRouter refused these free endpoints for Arni (2026-11-11).
        public static bool IsAvailableForArni(string id) =>
            !string.Equals(id, "thinkingmachines/inkling:free", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(id, "thinkingmachines/inkling-2-small:free", StringComparison.OrdinalIgnoreCase);

        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public bool Free { get; set; }
        public string PriceLabel { get; set; } = string.Empty;

        public string Display => Provider + " · " + Name + " · " + PriceLabel;

        public override string ToString()
        {
            return Display;
        }

        public static string FormatPrice(bool free, string promptPerToken, string completionPerToken)
        {
            if (free || (IsZero(promptPerToken) && IsZero(completionPerToken) && promptPerToken != null && completionPerToken != null))
            {
                return "бесплатно";
            }

            if (string.IsNullOrWhiteSpace(promptPerToken) || string.IsNullOrWhiteSpace(completionPerToken))
            {
                return "цена не указана";
            }

            return PerMillion(promptPerToken) + " / " + PerMillion(completionPerToken) + " за 1M";
        }

        public static string ProviderLabel(string provider)
        {
            if (string.Equals(provider, "bedrock", StringComparison.OrdinalIgnoreCase))
            {
                return "Bedrock";
            }

            if (string.Equals(provider, "openrouter", StringComparison.OrdinalIgnoreCase))
            {
                return "OpenRouter";
            }

            return string.IsNullOrWhiteSpace(provider) ? "Модель" : provider;
        }

        private static string PerMillion(string perToken)
        {
            if (!decimal.TryParse(perToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return "цена не указана";
            }

            var perMillion = value * 1000000m;
            return "$" + perMillion.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static bool IsZero(string perToken)
        {
            return decimal.TryParse(perToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value == 0m;
        }
    }
}
