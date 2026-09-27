using System.Text.Json;
using Goulash.Application;

namespace Goulash.Api.Providers;

/// <summary>Validates structured supplier records against the independently collected source snippets.</summary>
internal static class SupplierDiscoveryResponseParser
{
    private const int MaxCandidatesInResponse = 100;
    private const int MaxFactsPerCandidate = 100;

    public static SupplierDiscoveryResult Parse(string content, IReadOnlyCollection<SupplierDiscoveryEvidence> evidence)
    {
        JsonDocument modelDocument;
        try
        {
            var trimmed = content.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var firstLineEnd = trimmed.IndexOf('\n');
                var closingFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
                if (firstLineEnd >= 0 && closingFence > firstLineEnd)
                    trimmed = trimmed[(firstLineEnd + 1)..closingFence].Trim();
            }
            if (!trimmed.StartsWith('{'))
            {
                var start = trimmed.IndexOf('{');
                var end = trimmed.LastIndexOf('}');
                if (start >= 0 && end > start) trimmed = trimmed[start..(end + 1)];
            }
            modelDocument = JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }

        using (modelDocument)
        {
            var modelRoot = modelDocument.RootElement;
            if (modelRoot.ValueKind != JsonValueKind.Object ||
                !modelRoot.TryGetProperty("suppliers", out var suppliers) || suppliers.ValueKind != JsonValueKind.Array)
                throw InvalidResponse();

            var byUrl = evidence.GroupBy(item => UrlKey(item.Url), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var candidates = new List<SupplierDiscoveryCandidate>();
            var rejectedRecords = 0;
            var rejectedFacts = 0;
            var rejectedReasons = new Dictionary<string, int>(StringComparer.Ordinal);
            var index = 0;
            foreach (var supplier in suppliers.EnumerateArray())
            {
                if (index++ >= MaxCandidatesInResponse)
                {
                    rejectedRecords++;
                    continue;
                }

                if (!TryParseCandidate(supplier, byUrl, out var candidate, out var rejectedFactsForRecord,
                        out var rejectionReason))
                {
                    rejectedRecords++;
                    rejectedFacts += rejectedFactsForRecord;
                    rejectedReasons[rejectionReason!] = rejectedReasons.GetValueOrDefault(rejectionReason!) + 1;
                    continue;
                }

                candidates.Add(candidate!);
                rejectedFacts += rejectedFactsForRecord;
            }

            return new SupplierDiscoveryResult(candidates.AsReadOnly(), rejectedRecords, rejectedFacts,
                RejectedReasons: rejectedReasons);
        }
    }

    public static object BuildSupplierSchema()
    {
        var scalar = new object[]
        {
            new { type = "string" }, new { type = "number" }, new { type = "integer" }, new { type = "boolean" },
            new { type = "object", additionalProperties = true },
            new { type = "array", items = new { anyOf = new object[] { new { type = "string" }, new { type = "number" }, new { type = "integer" }, new { type = "boolean" } } } }
        };
        var factSchema = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                fieldKey = new { type = "string" },
                itemKey = new { type = "string" },
                value = new { anyOf = scalar },
                sourceUrl = new { type = new[] { "string", "null" } }
            },
            required = new[] { "fieldKey", "itemKey", "value", "sourceUrl" }
        };
        var supplierSchema = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                name = new { type = "string" },
                nameSourceUrl = new { type = new[] { "string", "null" } },
                websiteUrl = new { type = new[] { "string", "null" } },
                websiteSourceUrl = new { type = new[] { "string", "null" } },
                facts = new { type = "array", items = factSchema }
            },
            required = new[] { "name", "nameSourceUrl", "websiteUrl", "websiteSourceUrl", "facts" }
        };
        return new
        {
            type = "object",
            additionalProperties = false,
            properties = new { suppliers = new { type = "array", items = supplierSchema } },
            required = new[] { "suppliers" }
        };
    }

    public static string UrlKey(Uri uri)
    {
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped).TrimEnd('/');
    }

    public static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryParseCandidate(JsonElement input, IReadOnlyDictionary<string, SupplierDiscoveryEvidence> evidenceByUrl,
        out SupplierDiscoveryCandidate? candidate, out int rejectedFacts, out string? rejectionReason)
    {
        candidate = null;
        rejectedFacts = 0;
        rejectionReason = null;
        if (input.ValueKind != JsonValueKind.Object)
        {
            rejectionReason = "invalid_shape";
            return false;
        }
        var name = ReadString(input, "name");
        if (!SupplierDiscoveryEvidencePolicy.IsValidSupplierName(name))
        {
            rejectionReason = "invalid_name";
            return false;
        }
        if (!TryGetNameEvidence(input, name!, evidenceByUrl, out var nameEvidence))
        {
            rejectionReason = "name_not_in_sources";
            return false;
        }
        if (input.TryGetProperty("facts", out var facts) &&
            (facts.ValueKind != JsonValueKind.Array || facts.GetArrayLength() > MaxFactsPerCandidate))
        {
            rejectionReason = "invalid_facts_shape";
            return false;
        }

        string? websiteUrl = null;
        SupplierDiscoveryEvidence? websiteEvidence = null;
        var rawWebsiteUrl = ReadString(input, "websiteUrl");
        if (!string.IsNullOrWhiteSpace(rawWebsiteUrl) &&
            SupplierDiscoveryEvidencePolicy.TryNormalizeHttpUrl(rawWebsiteUrl, out var parsedWebsite) &&
            TryGetEvidence(input, "websiteSourceUrl", evidenceByUrl, out var foundWebsiteEvidence))
        {
            websiteUrl = parsedWebsite.AbsoluteUri;
            websiteEvidence = foundWebsiteEvidence;
        }

        var observations = new List<SupplierObservedFact>();
        foreach (var fact in facts.ValueKind == JsonValueKind.Array ? facts.EnumerateArray().ToArray() : Array.Empty<JsonElement>())
        {
            if (TryParseFact(fact, name!, evidenceByUrl, out var observation))
                observations.Add(observation!);
            else
                rejectedFacts++;
        }

        candidate = new SupplierDiscoveryCandidate(name!.Trim(), nameEvidence!, websiteUrl, websiteEvidence,
            observations.AsReadOnly());
        return true;
    }

    private static bool TryParseFact(JsonElement fact, string supplierName,
        IReadOnlyDictionary<string, SupplierDiscoveryEvidence> evidenceByUrl,
        out SupplierObservedFact? observation)
    {
        observation = null;
        if (fact.ValueKind != JsonValueKind.Object ||
            ReadString(fact, "fieldKey") is not { } fieldKey || !SupplierDiscoveryEvidencePolicy.IsAllowedFactField(fieldKey) ||
            ReadString(fact, "itemKey") is not { } itemKey || itemKey.Length > 200 ||
            !fact.TryGetProperty("value", out var value) || !SupplierDiscoveryEvidencePolicy.IsSupportedFactValue(value) ||
            !TryGetFactEvidence(fact, supplierName, value, evidenceByUrl, out var evidence) ||
            !SupplierDiscoveryEvidencePolicy.EvidenceSupportsValue(value, evidence!.Excerpt))
            return false;

        if (string.Equals(fieldKey.Trim(), "name", StringComparison.OrdinalIgnoreCase)) return false;
        var normalizedField = fieldKey.Trim().ToLowerInvariant();
        if (normalizedField is "website" or "image" &&
            (value.ValueKind != JsonValueKind.String ||
             !SupplierDiscoveryEvidencePolicy.TryNormalizeHttpUrl(value.GetString(), out _)))
            return false;

        observation = new SupplierObservedFact(normalizedField, itemKey.Trim(), value.Clone(),
            SupplierFactNormalizer.Normalize(normalizedField, value), evidence!);
        return true;
    }

    private static bool TryGetEvidence(JsonElement source, string propertyName,
        IReadOnlyDictionary<string, SupplierDiscoveryEvidence> evidenceByUrl, out SupplierDiscoveryEvidence? evidence)
    {
        evidence = null;
        var rawUrl = ReadString(source, propertyName);
        if (!SupplierDiscoveryEvidencePolicy.TryNormalizeHttpUrl(rawUrl, out var url)) return false;
        return evidenceByUrl.TryGetValue(UrlKey(url), out evidence);
    }

    private static bool TryGetNameEvidence(JsonElement source, string name,
        IReadOnlyDictionary<string, SupplierDiscoveryEvidence> evidenceByUrl, out SupplierDiscoveryEvidence? evidence)
    {
        if (!string.IsNullOrWhiteSpace(ReadString(source, "nameSourceUrl")) &&
            TryGetEvidence(source, "nameSourceUrl", evidenceByUrl, out evidence) &&
            (SupplierDiscoveryEvidencePolicy.ContainsSupplierName(name, evidence!.Title) ||
             SupplierDiscoveryEvidencePolicy.ContainsSupplierName(name, evidence.Excerpt)))
            return true;
        evidence = evidenceByUrl.Values.FirstOrDefault(item =>
            SupplierDiscoveryEvidencePolicy.ContainsSupplierName(name, item.Title) ||
            SupplierDiscoveryEvidencePolicy.ContainsSupplierName(name, item.Excerpt));
        return evidence is not null;
    }

    private static bool TryGetFactEvidence(JsonElement source, string supplierName, JsonElement value,
        IReadOnlyDictionary<string, SupplierDiscoveryEvidence> evidenceByUrl, out SupplierDiscoveryEvidence? evidence)
    {
        if (!string.IsNullOrWhiteSpace(ReadString(source, "sourceUrl")) &&
            TryGetEvidence(source, "sourceUrl", evidenceByUrl, out evidence) &&
            SupplierDiscoveryEvidencePolicy.EvidenceSupportsValue(value, evidence!.Excerpt))
            return true;
        evidence = evidenceByUrl.Values.FirstOrDefault(item =>
            (SupplierDiscoveryEvidencePolicy.ContainsSupplierName(supplierName, item.Title) ||
             SupplierDiscoveryEvidencePolicy.ContainsSupplierName(supplierName, item.Excerpt)) &&
            SupplierDiscoveryEvidencePolicy.EvidenceSupportsValue(value, item.Excerpt));
        return evidence is not null;
    }

    private static AiProviderException InvalidResponse() => new(ProviderFailureCode.InvalidResponse, "supplier_json");
}
