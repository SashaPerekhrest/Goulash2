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
    int RejectedCount,
    int UpdatedExistingCount);

/// <summary>Persists accepted, evidence-checked discoveries and their database-backed response cards atomically.</summary>
public sealed class DiscoveryPersistence(SupplierDataTransaction transaction)
{
    public Task<SupplierDiscoveryResponse> SaveAsync(DiscoveryRun run,
        IReadOnlyList<SupplierDiscoveryCandidate> candidates, int providerRejectedCount, int initiallyRejectedCount,
        CancellationToken cancellationToken) => transaction.ExecuteAsync(async (context, identityLock, token) =>
    {
        context.DiscoveryRuns.Add(run);

        // Acquire every identity lock in a stable order before any lookup or insert. Domain locks serialize
        // domain matches; name/region locks serialize the fallback for records without a verified domain.
        var lockKeys = candidates.SelectMany(candidate => GetLockKeys(candidate)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        foreach (var key in lockKeys)
            await identityLock.AcquireKeyAsync(key, token);

        var acceptedIds = new List<Guid>(5);
        var createdIds = new HashSet<Guid>();
        var updatedIds = new HashSet<Guid>();
        var rejectedCount = Math.Max(0, providerRejectedCount) + Math.Max(0, initiallyRejectedCount);

        foreach (var candidate in candidates)
        {
            if (acceptedIds.Count >= 5) break;
            if (!IsEvidenceCheckedCandidateForRoute(candidate))
            {
                rejectedCount++;
                continue;
            }

            var match = await FindMatchAsync(context, candidate, token);
            if (match.IsAmbiguous)
            {
                rejectedCount++;
                continue;
            }

            var supplier = match.Supplier;
            var existedBeforeThisRun = supplier is not null && !createdIds.Contains(supplier.Id);
            if (supplier is null)
            {
                var normalizedName = SupplierIdentity.NormalizeName(candidate.Name);
                if (normalizedName.Length is 0 or > 300)
                {
                    rejectedCount++;
                    continue;
                }
                supplier = new Supplier(candidate.Name, normalizedName, DateTimeOffset.UtcNow);
                context.Suppliers.Add(supplier);
                createdIds.Add(supplier.Id);
            }

            await LoadSupplierGraphAsync(context, supplier, token);
            await UpsertCandidateAsync(context, supplier, candidate, token);
            if (existedBeforeThisRun) updatedIds.Add(supplier.Id);
            if (!acceptedIds.Contains(supplier.Id)) acceptedIds.Add(supplier.Id);
        }

        run.Complete(acceptedIds.Count, rejectedCount, DateTimeOffset.UtcNow);
        await context.SaveChangesAsync(token);

        var savedSuppliers = await context.Suppliers.AsNoTracking().AsSplitQuery()
            .Where(supplier => acceptedIds.Contains(supplier.Id))
            .Include(supplier => supplier.Facts)
                .ThenInclude(fact => fact.FactSources)
                    .ThenInclude(link => link.Source)
            .ToListAsync(token);
        var cards = savedSuppliers.OrderBy(supplier => acceptedIds.IndexOf(supplier.Id)).Select(ToCard).ToArray();
        return new SupplierDiscoveryResponse(run.Id, cards, cards.Length, rejectedCount, updatedIds.Count);
    }, cancellationToken);

    private static IEnumerable<string> GetLockKeys(SupplierDiscoveryCandidate candidate)
    {
        var name = SupplierIdentity.NormalizeName(candidate.Name);
        var region = CurrentString(candidate.Facts, "region");
        var normalizedRegion = string.IsNullOrWhiteSpace(region) ? null : SupplierIdentity.NormalizeName(region);
        if (!string.IsNullOrWhiteSpace(candidate.ConfirmedOfficialDomain))
            yield return $"supplier-domain:{candidate.ConfirmedOfficialDomain.Trim().ToLowerInvariant()}";
        yield return SupplierIdentity.CreateLockKey(name, normalizedRegion);
    }

    private static async Task<(Supplier? Supplier, bool IsAmbiguous)> FindMatchAsync(ApplicationDbContext context,
        SupplierDiscoveryCandidate candidate, CancellationToken cancellationToken)
    {
        var name = SupplierIdentity.NormalizeName(candidate.Name);
        var region = CurrentString(candidate.Facts, "region");
        var normalizedRegion = string.IsNullOrWhiteSpace(region) ? null : SupplierIdentity.NormalizeName(region);
        var domain = candidate.ConfirmedOfficialDomain?.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(domain))
        {
            var byDomain = context.Suppliers.Local.SingleOrDefault(item => item.OfficialDomain == domain)
                ?? await context.Suppliers.SingleOrDefaultAsync(item => item.OfficialDomain == domain, cancellationToken);
            if (byDomain is not null)
            {
                var nameContradicts = SupplierIdentity.NormalizeName(byDomain.Name) != name;
                var oldRegion = string.IsNullOrWhiteSpace(byDomain.Region) ? null : SupplierIdentity.NormalizeName(byDomain.Region);
                var regionContradicts = normalizedRegion is not null && oldRegion is not null && normalizedRegion != oldRegion;
                return nameContradicts && regionContradicts
                    ? (null, true)
                    : (byDomain, false);
            }

            // A new verified domain can adopt a same-name/same-region record only while that record is undomained.
            var undomained = await GetNameRegionMatchesAsync(context, name, normalizedRegion, undomainedOnly: true, cancellationToken);
            return MatchUnique(undomained);
        }

        var matches = await GetNameRegionMatchesAsync(context, name, normalizedRegion, undomainedOnly: false, cancellationToken);
        return MatchUnique(matches);
    }

    private static (Supplier? Supplier, bool IsAmbiguous) MatchUnique(IReadOnlyList<Supplier> matches) => matches.Count switch
    {
        0 => (null, false),
        1 => (matches[0], false),
        _ => (null, true)
    };

    private static async Task<IReadOnlyList<Supplier>> GetNameRegionMatchesAsync(ApplicationDbContext context,
        string normalizedName, string? normalizedRegion, bool undomainedOnly, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(normalizedRegion)) return Array.Empty<Supplier>();
        var query = context.Suppliers.Where(item => item.NormalizedName == normalizedName);
        if (undomainedOnly) query = query.Where(item => item.OfficialDomain == null);
        var sameName = await query.ToListAsync(cancellationToken);
        var localAdded = context.Suppliers.Local.Where(item => context.Entry(item).State == EntityState.Added &&
            item.NormalizedName == normalizedName && (!undomainedOnly || item.OfficialDomain is null));
        return sameName.Concat(localAdded).DistinctBy(item => item.Id)
            .Where(item => !string.IsNullOrWhiteSpace(item.Region) &&
                SupplierIdentity.NormalizeName(item.Region) == normalizedRegion).ToArray();
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
        var observations = new List<(string Field, string Item, JsonElement Value, JsonElement NormalizedValue, SupplierDiscoveryEvidence Evidence)>();
        var nameValue = JsonSerializer.SerializeToElement(candidate.Name);
        observations.Add(("name", "name", nameValue, nameValue, candidate.NameEvidence));
        if (!string.IsNullOrWhiteSpace(candidate.WebsiteUrl) && candidate.WebsiteEvidence is not null &&
            SupplierDiscoveryEvidencePolicy.TryNormalizeHttpUrl(candidate.WebsiteUrl, out var website))
        {
            var websiteValue = JsonSerializer.SerializeToElement(candidate.WebsiteUrl);
            var normalizedWebsite = JsonSerializer.SerializeToElement(website.AbsoluteUri);
            observations.Add(("website", "website", websiteValue, normalizedWebsite, candidate.WebsiteEvidence));
        }
        observations.AddRange(candidate.Facts.Select(fact => (fact.FieldKey.ToLowerInvariant(), fact.ItemKey,
            fact.Value, fact.NormalizedValue, fact.Evidence)));

        foreach (var observation in observations)
        {
            var effectiveDomain = candidate.ConfirmedOfficialDomain ?? supplier.OfficialDomain;
            var status = SupplierDiscoveryEvidencePolicy.IsEvidenceFromOfficialDomain(observation.Evidence, effectiveDomain)
                ? VerificationStatus.Official
                : VerificationStatus.External;
            await StoreFactAsync(context, supplier, observation.Field, observation.Item,
                observation.Value, observation.NormalizedValue, status, observation.Evidence, now, cancellationToken);
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
        var officialDomain = candidate.ConfirmedOfficialDomain ?? supplier.OfficialDomain;
        var officialSiteUrl = candidate.ConfirmedOfficialDomain is not null
            ? candidate.WebsiteUrl
            : supplier.OfficialSiteUrl;
        supplier.SetOfficialIdentity(officialSiteUrl, officialDomain,
            city is { Length: <= 160 } ? city : supplier.City,
            region is { Length: <= 160 } ? region : supplier.Region);
        supplier.MarkDiscovered(now);

        await RefreshProductProjectionsAsync(context, supplier, cancellationToken);
    }

    private static async Task StoreFactAsync(ApplicationDbContext context, Supplier supplier, string fieldKey,
        string itemKey, JsonElement value, JsonElement normalizedValue, VerificationStatus status, SupplierDiscoveryEvidence evidence,
        DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var canonicalValue = CanonicalJson(value);
        var matching = supplier.Facts.Where(fact => fact.FieldKey == fieldKey && fact.ItemKey == itemKey).ToArray();
        var current = matching.SingleOrDefault(fact => fact.IsCurrent);
        var source = GetOrCreateSource(context, supplier, evidence,
            status == VerificationStatus.Official ? SourceType.Official : SourceType.External);
        var reusable = matching.FirstOrDefault(fact => fact.Status == status && CanonicalJson(fact.ValueJson) == canonicalValue);

        if (reusable is not null)
        {
            reusable.RefreshObservedAt(observedAt);
            reusable.RefreshNormalizedValue(normalizedValue);
            reusable.AddSource(source);
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
        fact.AddSource(source);
        supplier.Facts.Add(fact);
        context.SupplierFacts.Add(fact);
    }

    private static bool ShouldReplaceCurrent(SupplierFact? current, VerificationStatus incomingStatus) =>
        current is null || current.Status == VerificationStatus.External && incomingStatus == VerificationStatus.Official ||
        current.Status == incomingStatus;

    private static SupplierSource GetOrCreateSource(ApplicationDbContext context, Supplier supplier,
        SupplierDiscoveryEvidence evidence, SourceType type)
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

    public static bool IsEvidenceCheckedCandidateForRoute(SupplierDiscoveryCandidate? candidate)
    {
        if (candidate is null || !SupplierDiscoveryEvidencePolicy.IsValidSupplierName(candidate.Name) ||
            !ValidEvidence(candidate.NameEvidence) ||
            !SupplierDiscoveryEvidencePolicy.ContainsSupplierName(candidate.Name, candidate.NameEvidence.Excerpt) ||
            candidate.Facts is null || candidate.Facts.Count > 100) return false;

        if (!string.IsNullOrWhiteSpace(candidate.WebsiteUrl) &&
            (!SupplierDiscoveryEvidencePolicy.TryNormalizeHttpUrl(candidate.WebsiteUrl, out _) ||
             candidate.WebsiteEvidence is null || !ValidEvidence(candidate.WebsiteEvidence))) return false;

        return candidate.Facts.All(fact => fact is not null &&
            SupplierDiscoveryEvidencePolicy.IsAllowedFactField(fact.FieldKey) &&
            !string.IsNullOrWhiteSpace(fact.ItemKey) && fact.ItemKey.Length <= 200 &&
            SupplierDiscoveryEvidencePolicy.IsSupportedFactValue(fact.Value) &&
            IsServerNormalizedValue(fact) &&
            ValidEvidence(fact.Evidence) &&
            SupplierDiscoveryEvidencePolicy.EvidenceSupportsValue(fact.Value, fact.Evidence.Excerpt));
    }

    private static bool IsServerNormalizedValue(SupplierObservedFact fact)
    {
        if (!SupplierDiscoveryEvidencePolicy.IsSupportedFactValue(fact.NormalizedValue)) return false;
        var expected = SupplierFactNormalizer.Normalize(fact.FieldKey, fact.Value);
        return CanonicalJson(expected) == CanonicalJson(fact.NormalizedValue);
    }

    private static bool ValidEvidence(SupplierDiscoveryEvidence evidence) =>
        evidence is not null && evidence.Url is not null && evidence.RetrievedAt.Offset == TimeSpan.Zero &&
        SupplierDiscoveryEvidencePolicy.TryCreateEvidence(evidence.Url.AbsoluteUri, evidence.Title, evidence.Excerpt,
            evidence.RetrievedAt, out _);

    private static SupplierDiscoveryCard ToCard(Supplier supplier)
    {
        var currentFacts = supplier.Facts.Where(fact => fact.IsCurrent).ToArray();
        var prices = currentFacts.Where(fact => fact.FieldKey is "price" or "product_price")
            .Select(fact => TryReadPrice(fact.NormalizedValueJson, out var minimum, out var maximum,
                    out var currency, out var unit, out var approximate)
                ? new { Fact = fact, Minimum = minimum, Maximum = maximum, Currency = currency, Unit = unit, Approximate = approximate }
                : null)
            .Where(item => item is not null)
            .OrderBy(item => item!.Fact.Status == VerificationStatus.Official ? 0 : 1)
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
            fact.Status == VerificationStatus.External);

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
        if (hasMinimum && hasMaximum && SupplierDiscoveryMatcher.TryDecimal(minElement, out minimum) &&
            SupplierDiscoveryMatcher.TryDecimal(maxElement, out maximum))
            return minimum >= 0 && maximum >= minimum;
        if (!hasMinimum && !hasMaximum && TryGet(value, "amount", out var amountElement) &&
            SupplierDiscoveryMatcher.TryDecimal(amountElement, out var amount) && amount >= 0)
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
            .OrderBy(fact => fact.Status == VerificationStatus.Official ? 0 : 1)
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
        if (SupplierDiscoveryMatcher.TryDecimal(document.RootElement, out number)) return true;
        number = default;
        return false;
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
