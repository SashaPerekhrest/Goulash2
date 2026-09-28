using System.Globalization;
using System.Text.Json;
using Goulash.Application;
using Goulash.Domain;
using Goulash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Goulash.Api.Providers;

public sealed record SupplierDiscoveryCard(
    Guid Id,
    string Name,
    string? City,
    IReadOnlyList<string> Products,
    string? PricePreview,
    bool PriceIsApproximate,
    string? DeliveryPreview,
    string? WebsiteUrl,
    string? ContactPreview,
    bool IsFavorite,
    bool HasUnconfirmedData,
    DateTimeOffset? LastDiscoveredAt);

public sealed record SupplierDiscoveryResponse(
    Guid DiscoveryId,
    IReadOnlyList<SupplierDiscoveryCard> Items,
    int AcceptedCount,
    int FailedProfileCount,
    int UpdatedExistingCount,
    string Outcome = "complete",
    int SourcePageCount = 0);

/// <summary>Persists structured discoveries with their source links and response cards atomically.</summary>
public sealed class DiscoveryPersistence(SupplierDataTransaction transaction)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<SupplierDiscoveryResponse> SaveAsync(DiscoveryRun run,
        IReadOnlyList<SupplierDiscoveryCandidate> candidates, int failedProfileCount,
        CancellationToken cancellationToken, string outcome = "complete", int sourcePageCount = 0) =>
        transaction.ExecuteAsync(async (context, identityLock, token) =>
    {
        if (context.Entry(run).State == EntityState.Detached)
            context.DiscoveryRuns.Add(run);

        // Acquire every identity lock in a stable order before any lookup or insert.
        var lockKeys = candidates.SelectMany(candidate => GetLockKeys(candidate)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        foreach (var key in lockKeys)
            await identityLock.AcquireKeyAsync(key, token);

        var acceptedIds = new List<Guid>(candidates.Count);
        var createdIds = new HashSet<Guid>();
        var updatedIds = new HashSet<Guid>();

        foreach (var candidate in candidates)
        {
            var match = await FindMatchAsync(context, candidate, token);
            var supplier = match.Supplier;
            var existedBeforeThisRun = supplier is not null && !createdIds.Contains(supplier.Id);
            if (supplier is null)
            {
                var normalizedName = SupplierIdentity.NormalizeName(candidate.Name);
                supplier = new Supplier(candidate.Name, normalizedName, DateTimeOffset.UtcNow);
                context.Suppliers.Add(supplier);
                createdIds.Add(supplier.Id);
            }

            await LoadSupplierGraphAsync(context, supplier, token);
            await UpsertCandidateAsync(context, supplier, candidate, token);
            if (existedBeforeThisRun) updatedIds.Add(supplier.Id);
            if (!acceptedIds.Contains(supplier.Id)) acceptedIds.Add(supplier.Id);
        }

        var finalOutcome = outcome;
        await context.SaveChangesAsync(token);

        var savedSuppliers = await context.Suppliers.AsNoTracking().AsSplitQuery()
            .Where(supplier => acceptedIds.Contains(supplier.Id))
            .Include(supplier => supplier.Facts)
                .ThenInclude(fact => fact.FactSources)
                    .ThenInclude(link => link.Source)
            .ToListAsync(token);
        var cards = savedSuppliers.OrderBy(supplier => acceptedIds.IndexOf(supplier.Id)).Select(ToCard).ToArray();
        var response = new SupplierDiscoveryResponse(run.Id, cards, cards.Length, failedProfileCount, updatedIds.Count,
            finalOutcome, sourcePageCount);
        run.Complete(cards.Length, failedProfileCount, DateTimeOffset.UtcNow, finalOutcome,
            JsonSerializer.Serialize(response, JsonOptions));
        await context.SaveChangesAsync(token);
        return response;
    }, cancellationToken);

    private static IEnumerable<string> GetLockKeys(SupplierDiscoveryCandidate candidate)
    {
        var domain = ResolveSupplierDomain(candidate.WebsiteUrl);
        if (domain is not null) yield return $"supplier-domain:{domain}";
        yield return SupplierIdentity.CreateLockKey(SupplierIdentity.NormalizeName(candidate.Name), null);
    }

    private static async Task<(Supplier? Supplier, bool IsAmbiguous)> FindMatchAsync(ApplicationDbContext context,
        SupplierDiscoveryCandidate candidate, CancellationToken cancellationToken)
    {
        var name = SupplierIdentity.NormalizeName(candidate.Name);
        var domain = ResolveSupplierDomain(candidate.WebsiteUrl);

        if (!string.IsNullOrWhiteSpace(domain))
        {
            var byDomain = context.Suppliers.Local.FirstOrDefault(item => item.OfficialDomain == domain)
                ?? await context.Suppliers.Where(item => item.OfficialDomain == domain)
                    .OrderByDescending(item => item.LastDiscoveredAt).FirstOrDefaultAsync(cancellationToken);
            if (byDomain is not null)
                return (byDomain, false);
        }

        var query = context.Suppliers.Where(item => item.NormalizedName == name);
        var sameName = await query.OrderByDescending(item => item.LastDiscoveredAt)
            .FirstOrDefaultAsync(cancellationToken);
        sameName ??= context.Suppliers.Local.FirstOrDefault(item =>
            context.Entry(item).State == EntityState.Added && item.NormalizedName == name);
        return (sameName, false);
    }

    private static async Task LoadSupplierGraphAsync(ApplicationDbContext context, Supplier supplier, CancellationToken cancellationToken)
    {
        if (context.Entry(supplier).State == EntityState.Added) return;
        await context.Entry(supplier).Collection(item => item.Facts).Query()
            .Include(fact => fact.FactSources).ThenInclude(link => link.Source)
            .LoadAsync(cancellationToken);
        await context.Entry(supplier).Collection(item => item.Sources).LoadAsync(cancellationToken);
        await context.Entry(supplier).Collection(item => item.Products).Query()
            .Include(product => product.Prices).ThenInclude(price => price.Fact)
            .LoadAsync(cancellationToken);
    }

    private static async Task UpsertCandidateAsync(ApplicationDbContext context, Supplier supplier,
        SupplierDiscoveryCandidate candidate, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var observations = new List<(string Field, string Item, JsonElement Value, JsonElement NormalizedValue)>();
        var nameValue = JsonSerializer.SerializeToElement(candidate.Name);
        observations.Add(("name", "name", nameValue, nameValue));
        if (!string.IsNullOrWhiteSpace(candidate.WebsiteUrl) &&
            SupplierDiscoverySourcePolicy.TryNormalizeHttpUrl(candidate.WebsiteUrl, out var website))
        {
            var websiteValue = JsonSerializer.SerializeToElement(candidate.WebsiteUrl);
            var normalizedWebsite = JsonSerializer.SerializeToElement(website.AbsoluteUri);
            observations.Add(("website", "website", websiteValue, normalizedWebsite));
        }
        observations.AddRange(candidate.Facts.Select(fact => (fact.FieldKey.ToLowerInvariant(), fact.ItemKey,
            fact.Value, fact.NormalizedValue)));

        foreach (var source in candidate.Sources)
        {
            if (SupplierDiscoverySourcePolicy.TryCreateSource(source.Url.AbsoluteUri, source.Title, source.Excerpt,
                    source.RetrievedAt, out var safeSource))
                GetOrCreateSource(context, supplier, safeSource!, SourceType.External);
        }

        foreach (var observation in observations)
        {
            await StoreFactAsync(context, supplier, observation.Field, observation.Item,
                observation.Value, observation.NormalizedValue, VerificationStatus.AiGenerated, now, cancellationToken);
        }

        var currentName = supplier.Facts.SingleOrDefault(fact => fact.FieldKey == "name" && fact.ItemKey == "name" && fact.IsCurrent);
        if (currentName is not null)
        {
            var name = JsonSerializer.Deserialize<string>(currentName.ValueJson);
            if (!string.IsNullOrWhiteSpace(name))
            {
                supplier.SetCurrentNameFact(currentName, name);
                var normalizedName = SupplierIdentity.NormalizeName(name);
                if (normalizedName.Length <= 300) supplier.SetNormalizedName(normalizedName);
            }
        }

        var city = CurrentString(supplier.Facts, "city");
        var region = CurrentString(supplier.Facts, "region");
        var officialDomain = ResolveSupplierDomain(candidate.WebsiteUrl) ?? supplier.OfficialDomain;
        var officialSiteUrl = candidate.WebsiteUrl ?? supplier.OfficialSiteUrl;
        supplier.SetOfficialIdentity(officialSiteUrl, officialDomain,
            city is { Length: <= 160 } ? city : supplier.City,
            region is { Length: <= 160 } ? region : supplier.Region);
        supplier.MarkDiscovered(now);

        await RefreshProductProjectionsAsync(context, supplier, cancellationToken);
    }

    private static async Task StoreFactAsync(ApplicationDbContext context, Supplier supplier, string fieldKey,
        string itemKey, JsonElement value, JsonElement normalizedValue, VerificationStatus status,
        DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var canonicalValue = CanonicalJson(value);
        var matching = supplier.Facts.Where(fact => fact.FieldKey == fieldKey && fact.ItemKey == itemKey).ToArray();
        var current = matching.SingleOrDefault(fact => fact.IsCurrent);
        var reusable = matching.FirstOrDefault(fact => fact.Status == status && CanonicalJson(fact.ValueJson) == canonicalValue);

        if (reusable is not null)
        {
            reusable.RefreshObservedAt(observedAt);
            reusable.RefreshNormalizedValue(normalizedValue);
            if (reusable != current && ShouldReplaceCurrent(current, status))
            {
                if (current is not null)
                {
                    current.SelectAsAlternative();
                    await context.SaveChangesAsync(cancellationToken);
                }
                reusable.SelectAsCurrent();
            }
            return;
        }

        var isCurrent = ShouldReplaceCurrent(current, status);
        if (isCurrent && current is not null)
        {
            current.SelectAsAlternative();
            await context.SaveChangesAsync(cancellationToken);
        }

        var fact = new SupplierFact(supplier.Id, fieldKey, itemKey, value, status, observedAt, isCurrent, normalizedValue);
        supplier.Facts.Add(fact);
        context.SupplierFacts.Add(fact);
    }

    private static bool ShouldReplaceCurrent(SupplierFact? current, VerificationStatus incomingStatus) =>
        current is null || incomingStatus == VerificationStatus.AiGenerated ||
        current.Status == VerificationStatus.External && incomingStatus == VerificationStatus.Official ||
        current.Status == incomingStatus;

    private static SupplierSource GetOrCreateSource(ApplicationDbContext context, Supplier supplier,
        SupplierDiscoverySource evidence, SourceType type)
    {
        var url = evidence.Url.AbsoluteUri;
        var excerpt = evidence.Excerpt.Trim();
        var existing = supplier.Sources.FirstOrDefault(source => source.Url == url &&
            source.Excerpt == excerpt && source.Type == type);
        if (existing is not null)
        {
            existing.RefreshObservation(evidence.Title, evidence.RetrievedAt);
            return existing;
        }

        var source = new SupplierSource(supplier.Id, evidence.Url, evidence.Title, evidence.Excerpt, type, evidence.RetrievedAt);
        supplier.Sources.Add(source);
        context.SupplierSources.Add(source);
        return source;
    }

    private static async Task RefreshProductProjectionsAsync(ApplicationDbContext context, Supplier supplier,
        CancellationToken cancellationToken)
    {
        var currentFacts = supplier.Facts.Where(fact => fact.IsCurrent).ToArray();
        var productNames = currentFacts.Where(fact => fact.FieldKey == "product_name")
            .ToDictionary(fact => fact.ItemKey, fact => ReadString(fact.ValueJson), StringComparer.Ordinal);
        var productCategories = currentFacts.Where(fact => fact.FieldKey == "product_category")
            .ToDictionary(fact => fact.ItemKey, fact => ReadString(fact.ValueJson), StringComparer.Ordinal);
        var currentPrices = currentFacts.Where(fact => fact.FieldKey is "price" or "product_price").ToArray();
        var activeProductKeys = productNames.Keys.Concat(productCategories.Keys).Concat(currentPrices.Select(fact => fact.ItemKey))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var stale in supplier.Products.Where(product => !activeProductKeys.Contains(product.ItemKey)).ToArray())
        {
            context.SupplierProducts.Remove(stale);
            supplier.Products.Remove(stale);
        }

        foreach (var itemKey in activeProductKeys)
        {
            var name = productNames.TryGetValue(itemKey, out var productName) && !string.IsNullOrWhiteSpace(productName)
                ? productName
                : itemKey;
            var category = productCategories.GetValueOrDefault(itemKey);
            if (name.Length > 300) name = itemKey;
            if (category?.Length > 160) category = null;
            var product = supplier.Products.FirstOrDefault(item => item.ItemKey == itemKey);
            if (product is null)
            {
                product = new SupplierProduct(supplier.Id, itemKey, name,
                    SupplierIdentity.NormalizeName(name), category);
                supplier.Products.Add(product);
                context.SupplierProducts.Add(product);
            }
            else
            {
                product.UpdateDetails(name, SupplierIdentity.NormalizeName(name), category);
            }
        }

        var currentPriceFactIds = currentPrices.Select(fact => fact.Id).ToHashSet();
        foreach (var product in supplier.Products)
        {
            foreach (var stalePrice in product.Prices.Where(price => !currentPriceFactIds.Contains(price.FactId)).ToArray())
            {
                context.SupplierPrices.Remove(stalePrice);
                product.Prices.Remove(stalePrice);
            }
        }

        var projectedFactIds = supplier.Products.SelectMany(product => product.Prices).Select(price => price.FactId).ToHashSet();
        foreach (var fact in currentPrices.Where(fact => !projectedFactIds.Contains(fact.Id)))
        {
            if (!TryReadPrice(fact.NormalizedValueJson, out var minimum, out var maximum,
                    out var currency, out var unit, out var approximate)) continue;
            // The normalized projection has precision 18,4. Preserve the evidence fact even if a provider returns
            // a value outside this comparison-index range.
            if (decimal.Round(minimum, 4) != minimum || decimal.Round(maximum, 4) != maximum ||
                minimum > 99999999999999.9999m || maximum > 99999999999999.9999m) continue;
            var product = supplier.Products.FirstOrDefault(item => item.ItemKey == fact.ItemKey);
            if (product is null) continue;
            var price = new SupplierPrice(product.Id, fact.Id, minimum, maximum, currency, unit, approximate);
            product.Prices.Add(price);
            context.SupplierPrices.Add(price);
        }

        await Task.CompletedTask;
    }

    private static SupplierDiscoveryCard ToCard(Supplier supplier)
    {
        var currentFacts = supplier.Facts.Where(fact => fact.IsCurrent).ToArray();
        var prices = currentFacts.Where(fact => fact.FieldKey is "price" or "product_price")
            .Select(fact => TryReadPrice(fact.NormalizedValueJson, out var minimum, out var maximum,
                    out var currency, out var unit, out var approximate)
                ? new { Fact = fact, Minimum = minimum, Maximum = maximum, Currency = currency, Unit = unit, Approximate = approximate }
                : null)
            .Where(item => item is not null)
            .OrderBy(item => item!.Fact.Status == VerificationStatus.AiGenerated ? 0 : 1)
            .ThenBy(item => item!.Approximate ? 1 : 0)
            .ThenBy(item => item!.Minimum)
            .ToArray();
        var selectedPrice = prices.FirstOrDefault();
        var pricePreview = selectedPrice is null ? null : FormatPrice(selectedPrice.Minimum, selectedPrice.Maximum,
            selectedPrice.Currency, selectedPrice.Unit);
        var deliveryDays = currentFacts.Where(fact => fact.FieldKey == "delivery_days")
            .Select(fact => TryReadStoredDecimal(fact.NormalizedValueJson, out var value) ? (decimal?)value : null)
            .Where(value => value.HasValue && value >= 0)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .Cast<decimal?>()
            .FirstOrDefault();
        var productNames = currentFacts.Where(fact => fact.FieldKey == "product_name")
            .Select(fact => ReadString(fact.ValueJson))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var website = supplier.OfficialSiteUrl ?? CurrentString(currentFacts, "website");
        var contact = CurrentString(currentFacts, "phone") ?? CurrentString(currentFacts, "email");
        var city = CurrentString(currentFacts, "city") ?? supplier.City;
        var shownFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "name", "city", "product_name", "price", "product_price", "delivery_days", "website", "phone", "email"
        };
        var unconfirmed = currentFacts.Any(fact => shownFields.Contains(fact.FieldKey) &&
            fact.Status != VerificationStatus.Official);

        return new SupplierDiscoveryCard(supplier.Id, supplier.Name, city, productNames,
            pricePreview, selectedPrice?.Approximate ?? false,
            deliveryDays.HasValue ? $"до {deliveryDays.Value.ToString("0.####", CultureInfo.InvariantCulture)} дней" : null,
            website, contact, supplier.IsFavorite, unconfirmed, supplier.LastDiscoveredAt);
    }

    private static bool TryReadPrice(string json, out decimal minimum, out decimal maximum,
        out string currency, out string unit, out bool approximate)
    {
        using var document = JsonDocument.Parse(json);
        return TryReadPrice(document.RootElement, out minimum, out maximum, out currency, out unit, out approximate);
    }

    private static bool TryReadPrice(JsonElement value, out decimal minimum, out decimal maximum,
        out string currency, out string unit, out bool approximate)
    {
        minimum = maximum = default;
        currency = unit = string.Empty;
        approximate = false;
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (!TryGet(value, "currency", out var currencyElement) || currencyElement.ValueKind != JsonValueKind.String ||
            !TryGet(value, "unit", out var unitElement) || unitElement.ValueKind != JsonValueKind.String) return false;
        currency = SupplierFactNormalizer.NormalizeCurrency(currencyElement.GetString() ?? string.Empty).ToUpperInvariant();
        unit = SupplierFactNormalizer.NormalizeUnit(unitElement.GetString() ?? string.Empty);
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter) || string.IsNullOrWhiteSpace(unit) || unit.Length > 40) return false;
        if (!TryGet(value, "isApproximate", out var approximateElement) ||
            approximateElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        approximate = approximateElement.GetBoolean();
        var hasMinimum = TryGet(value, "amountMin", out var minElement);
        var hasMaximum = TryGet(value, "amountMax", out var maxElement);
        if (hasMinimum && hasMaximum && SupplierFactNormalizer.TryDecimal(minElement, out minimum) &&
            SupplierFactNormalizer.TryDecimal(maxElement, out maximum))
            return minimum >= 0 && maximum >= minimum;
        if (!hasMinimum && !hasMaximum && TryGet(value, "amount", out var amountElement) &&
            SupplierFactNormalizer.TryDecimal(amountElement, out var amount) && amount >= 0)
        {
            minimum = maximum = amount;
            return true;
        }
        return false;
    }

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

    private static string? CurrentString(IEnumerable<SupplierObservedFact> facts, string fieldKey) =>
        facts.Where(fact => fact.FieldKey == fieldKey).Select(fact => fact.NormalizedValue.ValueKind == JsonValueKind.String
            ? fact.NormalizedValue.GetString()
            : null).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? CurrentString(IEnumerable<SupplierFact> facts, string fieldKey) =>
        facts.Where(fact => fact.FieldKey == fieldKey && fact.IsCurrent)
            .OrderBy(fact => fact.Status == VerificationStatus.AiGenerated ? 0 : fact.Status == VerificationStatus.Official ? 1 : 2)
            .Select(fact => ReadString(fact.ValueJson))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? ReadString(string valueJson)
    {
        using var document = JsonDocument.Parse(valueJson);
        return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null;
    }

    private static bool TryReadStoredDecimal(string valueJson, out decimal number)
    {
        using var document = JsonDocument.Parse(valueJson);
        if (SupplierFactNormalizer.TryDecimal(document.RootElement, out number)) return true;
        number = default;
        return false;
    }

    private static string? ResolveSupplierDomain(string? websiteUrl)
    {
        if (!SupplierDiscoverySourcePolicy.TryNormalizeHttpUrl(websiteUrl, out var uri)) return null;
        return SupplierDiscoverySourcePolicy.GetRegistrableDomain(uri.IdnHost) ?? uri.IdnHost.ToLowerInvariant();
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

    private static string CanonicalJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CanonicalJson(document.RootElement);
    }

    private static string CanonicalJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + CanonicalJson(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(CanonicalJson)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(element.GetString()),
        JsonValueKind.Number => element.TryGetDecimal(out var number) ? number.ToString("G29", CultureInfo.InvariantCulture) : element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "null"
    };
}
