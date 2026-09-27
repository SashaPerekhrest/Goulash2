using System.Globalization;
using System.Text.Json;
using Goulash.Application;
using Goulash.Domain;

namespace Goulash.Api.Providers;

public sealed record SupplierDetailsResponse(
    Guid Id,
    SourcedValue<string> Name,
    SourcedValue<string> Description,
    SourcedValue<string> Address,
    SourcedValue<string> City,
    SourcedValue<string> Region,
    IReadOnlyList<SourcedValue<string>> ServiceRegions,
    SupplierContactsDetails Contacts,
    IReadOnlyList<SupplierProductDetails> Products,
    SupplierDeliveryDetails Delivery,
    SourcedValue<SupplierMinimumOrderValue> MinimumOrder,
    IReadOnlyList<SourcedValue<string>> Certificates,
    IReadOnlyList<SourcedValue<string>> Images,
    IReadOnlyList<FactEvidence> Sources,
    bool IsFavorite,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastDiscoveredAt);

public sealed record SupplierContactsDetails(
    IReadOnlyList<SourcedValue<string>> Phones,
    IReadOnlyList<SourcedValue<string>> Emails,
    SourcedValue<string> Website);

public sealed record SupplierProductDetails(
    SourcedValue<string> Name,
    SourcedValue<string> Category,
    IReadOnlyList<SupplierPriceDetails> Prices);

public sealed record SupplierPriceDetails(
    decimal AmountMin,
    decimal AmountMax,
    string Currency,
    string Unit,
    bool IsApproximate,
    SourcedValue<string> Evidence);

public sealed record SupplierDeliveryDetails(SourcedValue<string> Terms, SourcedValue<decimal?> MaxDays);
public sealed record SupplierMinimumOrderValue(string Amount, string Unit);

internal static class SupplierCatalogMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool Matches(Supplier supplier, SupplierCatalogFilters filters)
    {
        var currentFacts = supplier.Facts.Where(fact => fact.IsCurrent).ToArray();
        if (filters.FavoriteOnly && !supplier.IsFavorite) return false;
        if (!MatchesAllContent(filters.Query, supplier))
            return false;
        if (!MatchesFactTerm(filters.City, currentFacts.Where(fact => fact.FieldKey is "city" or "service_region"), supplier.City))
            return false;
        if (!MatchesFactTerm(filters.Region, currentFacts.Where(fact => fact.FieldKey is "region" or "service_region"), supplier.Region))
            return false;
        if (!MatchesFactTerm(filters.Category, currentFacts.Where(fact => fact.FieldKey is "category" or "product_category")))
            return false;
        var productFacts = currentFacts.Where(fact => fact.FieldKey is "product_name" or "product_category").ToArray();
        if (!MatchesFactTerm(filters.Product, productFacts))
            return false;

        var matchingProductKeys = string.IsNullOrWhiteSpace(filters.Product)
            ? null
            : productFacts.Where(fact => FactMatchesTerm(filters.Product, fact))
                .Select(fact => fact.ItemKey).ToHashSet(StringComparer.Ordinal);
        if ((filters.PriceMinimum.HasValue || filters.PriceMaximum.HasValue) &&
            !currentFacts.Where(fact => fact.FieldKey is "price" or "product_price")
                .Where(fact => matchingProductKeys is null || matchingProductKeys.Contains(fact.ItemKey))
                .Any(fact => MatchesPrice(fact, filters)))
            return false;

        if (filters.MaximumDeliveryDays.HasValue &&
            !currentFacts.Where(fact => fact.FieldKey == "delivery_days")
                .Any(fact => TryReadDecimal(fact.NormalizedValueJson, out var days) &&
                    days >= 0 && days <= filters.MaximumDeliveryDays.Value))
            return false;

        if ((filters.MinimumOrderMinimum.HasValue || filters.MinimumOrderMaximum.HasValue) &&
            !currentFacts.Where(fact => fact.FieldKey == "minimum_order")
                .Any(fact => MatchesMinimumOrder(fact, filters)))
            return false;

        return true;
    }

    private static bool MatchesAllContent(string? query, Supplier supplier)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var values = new List<string?>
        {
            supplier.Name, supplier.NormalizedName, supplier.OfficialSiteUrl, supplier.OfficialDomain,
            supplier.City, supplier.Region, supplier.Note
        };
        values.AddRange(supplier.Products.Select(product => product.Name));
        foreach (var fact in supplier.Facts)
        {
            values.Add(fact.FieldKey);
            values.Add(fact.ItemKey);
            values.AddRange(FlattenStrings(fact.ValueJson));
            values.AddRange(FlattenStrings(fact.NormalizedValueJson));
            foreach (var evidence in fact.FactSources.Select(link => link.Source))
            {
                values.Add(evidence.Title);
                values.Add(evidence.Excerpt);
                values.Add(evidence.Url);
                values.Add(evidence.Host);
            }
        }
        foreach (var source in supplier.Sources)
        {
            values.Add(source.Title);
            values.Add(source.Excerpt);
            values.Add(source.Url);
            values.Add(source.Host);
        }
        return MatchesTerm(query, values.OfType<string>());
    }

    public static SupplierCatalogCard ToCard(Supplier supplier)
    {
        var currentFacts = supplier.Facts.Where(fact => fact.IsCurrent).ToArray();
        var price = supplier.Products.SelectMany(product => product.Prices)
            .Where(item => item.Fact.IsCurrent)
            .OrderBy(item => item.Fact.Status == VerificationStatus.Official ? 0 : 1)
            .ThenBy(item => item.IsApproximate ? 1 : 0)
            .ThenBy(item => item.AmountMin)
            .ThenBy(item => item.Fact.ObservedAt)
            .FirstOrDefault();
        var pricePreview = price is null ? null : FormatPrice(price.AmountMin, price.AmountMax, price.Currency, price.Unit);
        var delivery = currentFacts.Where(fact => fact.FieldKey == "delivery_days")
            .Select(fact => (Fact: fact, Days: TryReadDecimal(fact.NormalizedValueJson, out var value) && value >= 0
                ? (decimal?)value : null))
            .Where(item => item.Days.HasValue)
            .OrderBy(item => item.Days)
            .FirstOrDefault();
        var productFacts = currentFacts.Where(fact => fact.FieldKey == "product_name")
            .Where(fact => !string.IsNullOrWhiteSpace(ReadString(fact.ValueJson))).ToArray();
        var products = productFacts
            .Select(fact => ReadString(fact.ValueJson))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var websiteFact = CurrentStringFact(currentFacts, "website");
        var contactFact = CurrentStringFact(currentFacts, "phone") ?? CurrentStringFact(currentFacts, "email");
        var cityFact = CurrentStringFact(currentFacts, "city");
        var website = supplier.OfficialSiteUrl ?? (websiteFact is null ? null : ReadString(websiteFact.ValueJson));
        var contact = contactFact is null ? null : ReadString(contactFact.ValueJson);
        var city = cityFact is null ? supplier.City : ReadString(cityFact.ValueJson);
        var hasUnconfirmedData = currentFacts.Any(fact => fact.FieldKey == "name" &&
                fact.Status == VerificationStatus.External) ||
            productFacts.Any(fact => fact.Status == VerificationStatus.External) ||
            new[] { cityFact, websiteFact, contactFact, price?.Fact, delivery.Fact }
                .Any(fact => fact?.Status == VerificationStatus.External);

        return new SupplierCatalogCard(supplier.Id, supplier.Name, city, products,
            pricePreview, price?.IsApproximate ?? false,
            delivery.Days.HasValue ? $"до {delivery.Days.Value.ToString("0.####", CultureInfo.InvariantCulture)} дней" : null,
            website, contact, supplier.IsFavorite, hasUnconfirmedData, supplier.LastDiscoveredAt);
    }

    public static SupplierDetailsResponse ToDetails(Supplier supplier)
    {
        var facts = supplier.Facts.ToArray();
        var name = BuildSourcedValue(facts.Where(fact => fact.FieldKey == "name"), ReadStringValue);
        var description = BuildSourcedValue(facts.Where(fact => fact.FieldKey == "description"), ReadStringValue);
        var address = BuildSourcedValue(facts.Where(fact => fact.FieldKey == "address"), ReadStringValue);
        var city = BuildSourcedValue(facts.Where(fact => fact.FieldKey == "city"), ReadStringValue);
        var region = BuildSourcedValue(facts.Where(fact => fact.FieldKey == "region"), ReadStringValue);

        var phoneFacts = facts.Where(fact => fact.FieldKey == "phone").ToArray();
        var emailFacts = facts.Where(fact => fact.FieldKey == "email").ToArray();
        var website = BuildSourcedValue(facts.Where(fact => fact.FieldKey == "website"), ReadStringValue);
        var products = supplier.Products.OrderBy(product => product.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(product => product.ItemKey, StringComparer.Ordinal)
            .Select(product => ToProduct(product, facts))
            .ToArray();

        var delivery = new SupplierDeliveryDetails(
            BuildSourcedValue(facts.Where(fact => fact.FieldKey == "delivery_terms"), ReadStringValue),
            BuildSourcedValue(facts.Where(fact => fact.FieldKey == "delivery_days"), ReadNullableDecimal));
        var minimumOrder = BuildSourcedValue(facts.Where(fact => fact.FieldKey == "minimum_order"), ReadMinimumOrder);
        var serviceRegions = BuildCollection(facts, "service_region");
        var certificates = BuildCollection(facts, "certificate");
        var images = BuildCollection(facts, "image");
        var sources = supplier.Sources.GroupBy(source => source.Url, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(source => source.RetrievedAt).First())
            .OrderBy(source => source.Url, StringComparer.Ordinal)
            .Select(ToEvidence)
            .ToArray();

        return new SupplierDetailsResponse(supplier.Id, name, description, address, city, region, serviceRegions,
            new SupplierContactsDetails(BuildCollection(phoneFacts), BuildCollection(emailFacts), website), products,
            delivery, minimumOrder, certificates, images, sources, supplier.IsFavorite, supplier.Note,
            supplier.CreatedAt, supplier.UpdatedAt, supplier.LastDiscoveredAt);
    }

    private static SupplierProductDetails ToProduct(SupplierProduct product, IReadOnlyList<SupplierFact> facts)
    {
        var nameFacts = facts.Where(fact => fact.ItemKey == product.ItemKey && fact.FieldKey == "product_name");
        var categoryFacts = facts.Where(fact => fact.ItemKey == product.ItemKey && fact.FieldKey == "product_category").ToArray();
        if (!categoryFacts.Any(fact => fact.IsCurrent))
            categoryFacts = facts.Where(fact => fact.FieldKey == "category" &&
                fact.ItemKey == product.ItemKey).ToArray();

        var prices = product.Prices.Where(price => price.Fact.IsCurrent)
            .OrderBy(price => price.Fact.Status == VerificationStatus.Official ? 0 : 1)
            .ThenBy(price => price.AmountMin)
            .ThenBy(price => price.Fact.ObservedAt)
            .Select(price => new SupplierPriceDetails(price.AmountMin, price.AmountMax, price.Currency,
                price.Unit, price.IsApproximate, BuildPriceEvidence(price.Fact,
                    facts.Where(fact => fact.FieldKey == price.Fact.FieldKey && fact.ItemKey == price.Fact.ItemKey))))
            .ToArray();

        return new SupplierProductDetails(BuildSourcedValue(nameFacts, ReadStringValue),
            BuildSourcedValue(categoryFacts, ReadStringValue), prices);
    }

    private static IReadOnlyList<SourcedValue<string>> BuildCollection(IEnumerable<SupplierFact> facts, string fieldKey) =>
        BuildCollection(facts.Where(fact => fact.FieldKey == fieldKey));

    private static IReadOnlyList<SourcedValue<string>> BuildCollection(IEnumerable<SupplierFact> facts)
    {
        var allFacts = facts.ToArray();
        return allFacts.Where(fact => fact.IsCurrent)
            .GroupBy(fact => fact.ItemKey, StringComparer.Ordinal)
            .Select(group => BuildSourcedValue(allFacts.Where(fact => fact.ItemKey == group.Key), ReadStringValue))
            .Where(value => value.Status != FactStatus.Missing)
            .GroupBy(value => value.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(value => value.Status == FactStatus.Official ? 0 : 1)
                .ThenByDescending(value => value.ObservedAt).First())
            .ToArray();
    }

    private static SourcedValue<T> BuildSourcedValue<T>(IEnumerable<SupplierFact> candidates, Func<string, T?> readValue)
    {
        var allFacts = candidates.ToArray();
        var current = allFacts.Where(fact => fact.IsCurrent)
            .OrderBy(fact => fact.Status == VerificationStatus.Official ? 0 : 1)
            .ThenByDescending(fact => fact.ObservedAt)
            .ToArray();
        SupplierFact? selected = null;
        T? selectedValue = default;
        foreach (var fact in current)
        {
            var value = readValue(fact.ValueJson);
            if (value is null) continue;
            selected = fact;
            selectedValue = value;
            break;
        }
        if (selected is null || selectedValue is null)
            return SourcedValue<T>.Missing;

        var serializedSelected = JsonSerializer.Serialize(selectedValue, JsonOptions);
        var alternatives = new List<FactAlternative<T>>();
        foreach (var fact in allFacts.Where(fact => fact.Id != selected.Id)
                     .OrderBy(fact => fact.Status == VerificationStatus.Official ? 0 : 1)
                     .ThenByDescending(fact => fact.ObservedAt))
        {
            var value = readValue(fact.ValueJson);
            if (value is null || JsonSerializer.Serialize(value, JsonOptions) == serializedSelected) continue;
            alternatives.Add(new FactAlternative<T>(value, ToStatus(fact.Status), ToEvidenceList(fact), fact.ObservedAt));
        }

        return new SourcedValue<T>(selectedValue, ToStatus(selected.Status), ToEvidenceList(selected),
            selected.ObservedAt, alternatives);
    }

    private static SourcedValue<string> BuildPriceEvidence(SupplierFact fact, IEnumerable<SupplierFact> candidates)
    {
        var value = ReadPriceEvidenceText(fact);
        if (string.IsNullOrWhiteSpace(value)) return SourcedValue<string>.Missing;
        var alternatives = candidates.Where(candidate => candidate.Id != fact.Id)
            .OrderBy(candidate => candidate.Status == VerificationStatus.Official ? 0 : 1)
            .ThenByDescending(candidate => candidate.ObservedAt)
            .Select(candidate => (Fact: candidate, Value: ReadPriceEvidenceText(candidate)))
            .Where(item => !string.IsNullOrWhiteSpace(item.Value) && item.Value != value)
            .DistinctBy(item => (item.Value, item.Fact.Status))
            .Select(item => new FactAlternative<string>(item.Value!, ToStatus(item.Fact.Status),
                ToEvidenceList(item.Fact), item.Fact.ObservedAt))
            .ToArray();
        return new SourcedValue<string>(value, ToStatus(fact.Status), ToEvidenceList(fact), fact.ObservedAt,
            alternatives);
    }

    private static string? ReadPriceEvidenceText(SupplierFact fact) => ReadPriceOriginalText(fact.ValueJson) ??
        fact.FactSources.OrderByDescending(link => link.Source.RetrievedAt)
            .Select(link => link.Source.Excerpt).FirstOrDefault();

    private static string? ReadPriceOriginalText(string valueJson)
    {
        using var document = JsonDocument.Parse(valueJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!property.Name.Equals("originalText", StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
        }
        return null;
    }

    private static SupplierMinimumOrderValue? ReadMinimumOrder(string valueJson)
    {
        using var document = JsonDocument.Parse(valueJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
        string? amount = null;
        string? unit = null;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("amount", StringComparison.OrdinalIgnoreCase))
                amount = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number when property.Value.TryGetDecimal(out var number) => number.ToString("G29", CultureInfo.InvariantCulture),
                    _ => null
                };
            else if (property.Name.Equals("unit", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                unit = property.Value.GetString();
        }
        if (string.IsNullOrWhiteSpace(amount) || string.IsNullOrWhiteSpace(unit)) return null;
        if (decimal.TryParse(amount, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed))
            amount = parsed.ToString("G29", CultureInfo.InvariantCulture);
        return new SupplierMinimumOrderValue(amount, unit);
    }

    private static decimal? ReadNullableDecimal(string valueJson) =>
        TryReadDecimal(valueJson, out var number) ? number : null;

    private static string? ReadStringValue(string valueJson) => ReadString(valueJson);

    private static string? ReadString(string valueJson)
    {
        using var document = JsonDocument.Parse(valueJson);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
    }

    private static bool MatchesPrice(SupplierFact fact, SupplierCatalogFilters filters)
    {
        if (!TryReadPrice(fact.NormalizedValueJson, out var minimum, out var maximum,
                out var currency, out var unit, out var approximate)) return false;
        if (approximate && !filters.IncludeApproximatePrices) return false;
        if (!string.Equals(currency, filters.Currency, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(unit, filters.Unit, StringComparison.OrdinalIgnoreCase)) return false;
        if (filters.PriceMinimum.HasValue && maximum < filters.PriceMinimum.Value) return false;
        return !filters.PriceMaximum.HasValue || minimum <= filters.PriceMaximum.Value;
    }

    private static bool MatchesMinimumOrder(SupplierFact fact, SupplierCatalogFilters filters)
    {
        using var document = JsonDocument.Parse(fact.NormalizedValueJson);
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object || !TryGet(value, "amount", out var amountValue) ||
            !SupplierDiscoveryMatcher.TryDecimal(amountValue, out var amount) || amount < 0 ||
            !TryGet(value, "unit", out var unitValue) || unitValue.ValueKind != JsonValueKind.String ||
            !string.Equals(SupplierFactNormalizer.NormalizeUnit(unitValue.GetString() ?? string.Empty),
                filters.MinimumOrderUnit, StringComparison.OrdinalIgnoreCase)) return false;
        if (filters.MinimumOrderMinimum.HasValue && amount < filters.MinimumOrderMinimum.Value) return false;
        return !filters.MinimumOrderMaximum.HasValue || amount <= filters.MinimumOrderMaximum.Value;
    }

    private static bool TryReadPrice(string json, out decimal minimum, out decimal maximum,
        out string currency, out string unit, out bool approximate)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        minimum = maximum = default;
        currency = unit = string.Empty;
        approximate = false;
        if (value.ValueKind != JsonValueKind.Object || !TryGet(value, "currency", out var currencyValue) ||
            currencyValue.ValueKind != JsonValueKind.String || !TryGet(value, "unit", out var unitValue) ||
            unitValue.ValueKind != JsonValueKind.String || !TryGet(value, "isApproximate", out var approximateValue) ||
            approximateValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        currency = SupplierFactNormalizer.NormalizeCurrency(currencyValue.GetString() ?? string.Empty).ToUpperInvariant();
        unit = SupplierFactNormalizer.NormalizeUnit(unitValue.GetString() ?? string.Empty);
        approximate = approximateValue.GetBoolean();
        var hasMin = TryGet(value, "amountMin", out var minValue);
        var hasMax = TryGet(value, "amountMax", out var maxValue);
        if (hasMin && hasMax && SupplierDiscoveryMatcher.TryDecimal(minValue, out minimum) &&
            SupplierDiscoveryMatcher.TryDecimal(maxValue, out maximum))
            return minimum >= 0 && maximum >= minimum;
        if (!hasMin && !hasMax && TryGet(value, "amount", out var amountValue) &&
            SupplierDiscoveryMatcher.TryDecimal(amountValue, out var amount) && amount >= 0)
        {
            minimum = maximum = amount;
            return true;
        }
        return false;
    }

    private static bool TryReadDecimal(string valueJson, out decimal number)
    {
        using var document = JsonDocument.Parse(valueJson);
        return SupplierDiscoveryMatcher.TryDecimal(document.RootElement, out number);
    }

    private static bool MatchesFactTerm(string? expected, IEnumerable<SupplierFact> facts, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        var factValues = facts.ToArray();
        var values = factValues.SelectMany(fact => FlattenStrings(fact.NormalizedValueJson));
        if (fallback is not null) values = values.Append(fallback);
        return MatchesTerm(expected, values);
    }

    private static bool FactMatchesTerm(string? expected, SupplierFact fact) =>
        MatchesTerm(expected, FlattenStrings(fact.NormalizedValueJson));

    private static bool MatchesTerm(string? expected, IEnumerable<string> values)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        var normalizedExpected = SupplierIdentity.NormalizeName(expected);
        if (normalizedExpected.Length == 0) return false;
        return values.Any(value =>
        {
            var normalizedValue = SupplierIdentity.NormalizeName(value);
            return normalizedValue.Length > 0 &&
                (normalizedValue.Contains(normalizedExpected, StringComparison.Ordinal) ||
                 normalizedExpected.Contains(normalizedValue, StringComparison.Ordinal));
        });
    }

    private static IEnumerable<string> FlattenStrings(string json)
    {
        using var document = JsonDocument.Parse(json);
        return EnumerateStrings(document.RootElement).ToArray();

        static IEnumerable<string> EnumerateStrings(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    if (!string.IsNullOrWhiteSpace(element.GetString())) yield return element.GetString()!;
                    break;
                case JsonValueKind.Number:
                    yield return element.GetRawText();
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        foreach (var nested in EnumerateStrings(item)) yield return nested;
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                        foreach (var nested in EnumerateStrings(property.Value)) yield return nested;
                    break;
            }
        }
    }

    private static SupplierFact? CurrentStringFact(IEnumerable<SupplierFact> facts, string fieldKey) =>
        facts.Where(fact => fact.FieldKey == fieldKey && fact.IsCurrent)
            .OrderBy(fact => fact.Status == VerificationStatus.Official ? 0 : 1)
            .ThenByDescending(fact => fact.ObservedAt)
            .FirstOrDefault(fact => !string.IsNullOrWhiteSpace(ReadString(fact.ValueJson)));

    private static string FormatPrice(decimal minimum, decimal maximum, string currency, string unit)
    {
        var amount = minimum == maximum ? minimum.ToString("0.####", CultureInfo.InvariantCulture)
            : $"от {minimum.ToString("0.####", CultureInfo.InvariantCulture)} до {maximum.ToString("0.####", CultureInfo.InvariantCulture)}";
        var displayUnit = unit.ToLowerInvariant() switch
        {
            "kg" => "кг", "g" => "г", "l" => "л", "ml" => "мл", "piece" => "шт.",
            "pack" => "уп.", "box" => "кор.", _ => unit
        };
        return $"{amount} {currency}/{displayUnit}";
    }

    private static IReadOnlyList<FactEvidence> ToEvidenceList(SupplierFact fact) =>
        fact.FactSources.Select(link => ToEvidence(link.Source))
            .DistinctBy(evidence => (evidence.Url, evidence.Title, evidence.Excerpt, evidence.Type, evidence.RetrievedAt))
            .OrderByDescending(evidence => evidence.RetrievedAt)
            .ToArray();

    private static FactEvidence ToEvidence(SupplierSource source) =>
        new(source.Url, source.Title, source.Excerpt, source.Type, source.RetrievedAt);

    private static FactStatus ToStatus(VerificationStatus status) => status == VerificationStatus.Official
        ? FactStatus.Official : FactStatus.External;

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
}
