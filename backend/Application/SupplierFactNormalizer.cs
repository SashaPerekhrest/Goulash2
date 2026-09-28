using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Goulash.Application;

/// <summary>Normalizes values used for catalog filtering while retaining the original provider profile value.</summary>
public static partial class SupplierFactNormalizer
{
    public static bool TryDecimal(JsonElement value, out decimal number)
    {
        number = default;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDecimal(out number);
        return value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    }

    public static JsonElement Normalize(string fieldKey, JsonElement original)
    {
        if (original.ValueKind == JsonValueKind.Object)
        {
            var node = JsonNode.Parse(original.GetRawText());
            NormalizeObject(node);
            return JsonSerializer.SerializeToElement(node);
        }

        if (original.ValueKind != JsonValueKind.String) return original.Clone();
        var value = CollapseWhitespace(original.GetString() ?? string.Empty);
        return fieldKey.ToLowerInvariant() switch
        {
            "city" or "region" or "category" or "service_region" or "product_category" =>
                JsonSerializer.SerializeToElement(value.ToLowerInvariant()),
            "phone" => JsonSerializer.SerializeToElement(NormalizePhone(value)),
            "email" => JsonSerializer.SerializeToElement(value.ToLowerInvariant()),
            "website" or "image" => SupplierDiscoverySourcePolicy.TryNormalizeHttpUrl(value, out var url)
                ? JsonSerializer.SerializeToElement(url.AbsoluteUri)
                : JsonSerializer.SerializeToElement(value),
            "delivery_days" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days >= 0
                ? JsonSerializer.SerializeToElement(days)
                : JsonSerializer.SerializeToElement(value),
            _ => JsonSerializer.SerializeToElement(value)
        };
    }

    private static void NormalizeObject(JsonNode? node)
    {
        if (node is not JsonObject obj) return;
        foreach (var key in obj.Select(pair => pair.Key).ToArray())
        {
            var value = obj[key];
            if (value is JsonObject nestedObject)
            {
                NormalizeObject(nestedObject);
                continue;
            }

            if (value is JsonArray array)
            {
                foreach (var item in array) NormalizeObject(item);
                continue;
            }

            if (value is JsonValue numberValue &&
                (key.Equals("amount", StringComparison.OrdinalIgnoreCase) ||
                 key.Equals("amountMin", StringComparison.OrdinalIgnoreCase) ||
                 key.Equals("amountMax", StringComparison.OrdinalIgnoreCase)) &&
                numberValue.TryGetValue<decimal>(out var numericAmount))
            {
                obj[key] = numericAmount.ToString("G29", CultureInfo.InvariantCulture);
                continue;
            }

            if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text) || text is null) continue;
            var normalized = CollapseWhitespace(text);
            if (key.Equals("amount", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("amountMin", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("amountMax", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseAmount(normalized, out var amount))
                    obj[key] = amount.ToString("G29", CultureInfo.InvariantCulture);
                else
                    obj[key] = normalized;
            }
            else if (key.Equals("currency", StringComparison.OrdinalIgnoreCase))
            {
                obj[key] = NormalizeCurrency(normalized);
            }
            else if (key.Equals("unit", StringComparison.OrdinalIgnoreCase))
            {
                obj[key] = NormalizeUnit(normalized);
            }
            else
            {
                obj[key] = normalized;
            }
        }
    }

    private static string NormalizePhone(string value)
    {
        var digits = NonDigitRegex().Replace(value, string.Empty);
        return digits.Length is >= 6 and <= 15
            ? (value.TrimStart().StartsWith('+') ? "+" : string.Empty) + digits
            : value;
    }

    public static string NormalizeCurrency(string value)
    {
        var compact = value.Trim().ToLowerInvariant();
        return compact switch
        {
            "руб" or "руб." or "рубль" or "рубля" or "рублей" or "₽" => "RUB",
            "$" or "доллар" or "доллара" or "долларов" => "USD",
            "€" or "евро" => "EUR",
            "£" or "фунт" or "фунта" or "фунтов" => "GBP",
            _ when compact.Length == 3 && compact.All(char.IsAsciiLetter) => compact.ToUpperInvariant(),
            _ => value
        };
    }

    public static string NormalizeUnit(string value)
    {
        var compact = value.Trim().ToLowerInvariant().Replace(".", string.Empty, StringComparison.Ordinal);
        return compact switch
        {
            "кг" or "килограмм" or "килограмма" or "килограммов" or "kg" => "kg",
            "г" or "гр" or "грамм" or "грамма" or "граммов" or "g" => "g",
            "л" or "литр" or "литра" or "литров" or "l" => "l",
            "мл" or "миллилитр" or "миллилитра" or "миллилитров" or "ml" => "ml",
            "шт" or "штука" or "штуки" or "штук" or "piece" or "pcs" => "piece",
            "уп" or "упаковка" or "упаковки" or "pack" => "pack",
            "короб" or "коробка" or "коробки" or "ящик" or "box" => "box",
            _ => compact
        };
    }

    private static bool TryParseAmount(string raw, out decimal amount)
    {
        var compact = WhitespaceRegex().Replace(raw, string.Empty);
        var comma = compact.LastIndexOf(',');
        var dot = compact.LastIndexOf('.');
        if (comma >= 0 && dot >= 0)
        {
            var decimalSeparator = Math.Max(comma, dot);
            var groupingSeparator = compact[decimalSeparator] == ',' ? '.' : ',';
            compact = compact.Replace(groupingSeparator.ToString(), string.Empty, StringComparison.Ordinal)
                .Replace(compact[decimalSeparator], '.');
        }
        else if (comma >= 0)
        {
            if (compact.Length - comma - 1 == 3)
            {
                amount = default;
                return false;
            }
            compact = compact.Replace(',', '.');
        }
        else if (dot >= 0 && compact.Length - dot - 1 == 3 && !compact.StartsWith("0.", StringComparison.Ordinal))
        {
            amount = default;
            return false;
        }

        return decimal.TryParse(compact, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private static string CollapseWhitespace(string value) =>
        WhitespaceRegex().Replace(value.Normalize(NormalizationForm.FormKC), " ").Trim();

    [GeneratedRegex("[^0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonDigitRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
