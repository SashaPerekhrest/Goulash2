using System.Globalization;
using System.Text.Json;

namespace Goulash.Application;

/// <summary>Applies the user's substantive discovery conditions to source-validated provider facts.</summary>
public static class SupplierDiscoveryMatcher
{
    public static bool Matches(SupplierDiscoveryCandidate candidate, SupplierDiscoveryFilters filters)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(filters);

        if (!MatchesText(filters.City, candidate.Facts.Where(fact => fact.FieldKey is "city" or "service_region")))
            return false;
        if (!MatchesText(filters.Region, candidate.Facts.Where(fact => fact.FieldKey is "region" or "service_region")))
            return false;
        if (!MatchesText(filters.Category, candidate.Facts.Where(fact => fact.FieldKey is "category" or "product_category")))
            return false;
        var productFacts = candidate.Facts.Where(fact => fact.FieldKey is "product_name" or "product_category").ToArray();
        if (!MatchesText(filters.Product, productFacts)) return false;
        var matchingProductKeys = string.IsNullOrWhiteSpace(filters.Product)
            ? null
            : productFacts.Where(fact => FlattenStrings(fact.NormalizedValue)
                    .Any(value => TextValuesMatch(value, SupplierIdentity.NormalizeName(filters.Product))))
                .Select(fact => fact.ItemKey).ToHashSet(StringComparer.Ordinal);
        if (filters.Price is not null && (filters.Price.Min.HasValue || filters.Price.Max.HasValue) &&
            !MatchesPrice(filters.Price, filters.IncludeApproximatePrices,
                matchingProductKeys is null
                    ? candidate.Facts
                    : candidate.Facts.Where(fact => fact.FieldKey is "price" or "product_price" &&
                        matchingProductKeys.Contains(fact.ItemKey))))
            return false;
        if (filters.MaxDeliveryDays.HasValue && !MatchesDelivery(filters.MaxDeliveryDays.Value, candidate.Facts))
            return false;
        if ((filters.MinMinimumOrder is not null || filters.MaxMinimumOrder is not null) &&
            !MatchesMinimumOrder(filters.MinMinimumOrder, filters.MaxMinimumOrder, candidate.Facts))
            return false;

        return true;
    }

    private static bool MatchesText(string? expected, IEnumerable<SupplierObservedFact> facts)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        var normalizedExpected = SupplierIdentity.NormalizeName(expected);
        if (normalizedExpected.Length == 0) return false;

        return facts.Any(fact => FlattenStrings(fact.NormalizedValue)
            .Any(value => TextValuesMatch(value, normalizedExpected)));
    }

    private static bool TextValuesMatch(string actual, string normalizedExpected)
    {
        var normalizedActual = SupplierIdentity.NormalizeName(actual);
        return normalizedActual.Length > 0 &&
            (normalizedActual == normalizedExpected ||
             normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal) ||
             normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal));
    }

    private static bool MatchesPrice(SupplierPriceRange filter, bool includeApproximate,
        IEnumerable<SupplierObservedFact> facts)
    {
        foreach (var fact in facts.Where(fact => fact.FieldKey is "price" or "product_price"))
        {
            if (!TryReadPrice(fact.NormalizedValue, out var minimum, out var maximum, out var currency, out var unit, out var approximate))
                continue;
            if (approximate && !includeApproximate) continue;
            if (!string.Equals(currency, filter.Currency, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(unit, filter.Unit, StringComparison.OrdinalIgnoreCase)) continue;

            // Compare intersections only. Missing/ambiguous endpoints were rejected by TryReadPrice.
            if (filter.Min.HasValue && maximum < filter.Min.Value) continue;
            if (filter.Max.HasValue && minimum > filter.Max.Value) continue;
            return true;
        }

        return false;
    }

    private static bool TryReadPrice(JsonElement value, out decimal minimum, out decimal maximum,
        out string currency, out string unit, out bool approximate)
    {
        minimum = maximum = default;
        currency = unit = string.Empty;
        approximate = false;
        if (value.ValueKind != JsonValueKind.Object ||
            !TryGet(value, "currency", out var currencyValue) || !TryString(currencyValue, out currency) ||
            !TryGet(value, "unit", out var unitValue) || !TryString(unitValue, out unit)) return false;

        if (!TryGet(value, "isApproximate", out var approximateValue) ||
            approximateValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        approximate = approximateValue.GetBoolean();

        currency = SupplierFactNormalizer.NormalizeCurrency(currency).ToUpperInvariant();
        unit = SupplierFactNormalizer.NormalizeUnit(unit);
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter) || string.IsNullOrWhiteSpace(unit)) return false;

        var hasMinimumValue = TryGet(value, "amountMin", out var minimumValue);
        var hasMaximumValue = TryGet(value, "amountMax", out var maximumValue);
        if (!hasMinimumValue && !hasMaximumValue && TryGet(value, "amount", out var amountValue) && TryDecimal(amountValue, out var amount))
        {
            minimum = maximum = amount;
        }
        else if (hasMinimumValue && hasMaximumValue && TryDecimal(minimumValue, out minimum) &&
                 TryDecimal(maximumValue, out maximum))
        {
            // Both ends are required for a range: an unknown endpoint cannot satisfy a numeric filter.
        }
        else return false;

        return minimum >= 0 && maximum >= minimum;
    }

    private static bool MatchesDelivery(int maximumDays, IEnumerable<SupplierObservedFact> facts)
    {
        foreach (var fact in facts.Where(fact => fact.FieldKey == "delivery_days"))
        {
            if (TryDecimal(fact.NormalizedValue, out var days) && days >= 0 && days <= maximumDays)
                return true;
        }
        return false;
    }

    private static bool MatchesMinimumOrder(SupplierMinimumOrderBound? minimumFilter,
        SupplierMinimumOrderBound? maximumFilter, IEnumerable<SupplierObservedFact> facts)
    {
        foreach (var fact in facts.Where(fact => fact.FieldKey == "minimum_order"))
        {
            if (fact.NormalizedValue.ValueKind != JsonValueKind.Object ||
                !TryGet(fact.NormalizedValue, "amount", out var amountValue) || !TryDecimal(amountValue, out var amount) || amount < 0 ||
                !TryGet(fact.NormalizedValue, "unit", out var unitValue) || !TryString(unitValue, out var unit)) continue;

            if (minimumFilter is not null &&
                (!string.Equals(unit, minimumFilter.Unit, StringComparison.OrdinalIgnoreCase) || amount < minimumFilter.Amount)) continue;
            if (maximumFilter is not null &&
                (!string.Equals(unit, maximumFilter.Unit, StringComparison.OrdinalIgnoreCase) || amount > maximumFilter.Amount)) continue;
            return true;
        }
        return false;
    }

    public static bool TryDecimal(JsonElement value, out decimal number)
    {
        number = default;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetDecimal(out number);
        // Normalized numeric strings use an invariant decimal point. Accepting group separators
        // would reinterpret an ambiguous source value such as "1,000" as a known amount.
        return value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    }

    private static bool TryString(JsonElement value, out string text)
    {
        text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool TryGet(JsonElement value, string name, out JsonElement result)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                result = property.Value;
                return true;
            }
        }
        result = default;
        return false;
    }

    private static IEnumerable<string> FlattenStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (!string.IsNullOrWhiteSpace(element.GetString())) yield return element.GetString()!;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var nested in FlattenStrings(item)) yield return nested;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    foreach (var nested in FlattenStrings(property.Value)) yield return nested;
                break;
        }
    }
}
