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
            modelDocument = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 32 });
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
            var index = 0;
            foreach (var supplier in suppliers.EnumerateArray())
            {
                if (index++ >= MaxCandidatesInResponse)
                {
                    rejectedRecords++;
                    continue;
                }

                if (!TryParseCandidate(supplier, byUrl, out var candidate, out var rejectedFactsForRecord))
                {
                    rejectedRecords++;
                    rejectedFacts += rejectedFactsForRecord;
                    continue;
                }

                candidates.Add(candidate!);
                rejectedFacts += rejectedFactsForRecord;
            }

            return new SupplierDiscoveryResult(candidates.AsReadOnly(), rejectedRecords, rejectedFacts);
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
                sourceUrl = new { type = "string" }
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
                nameSourceUrl = new { type = "string" },
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
        out SupplierDiscoveryCandidate? candidate, out int rejectedFacts)
    {
        candidate = null;
        rejectedFacts = 0;
        if (input.ValueKind != JsonValueKind.Object ||
            ReadString(input, "name") is not { } name || !SupplierDiscoveryEvidencePolicy.IsValidSupplierName(name) ||
            !TryGetEvidence(input, "nameSourceUrl", evidenceByUrl, out var nameEvidence) ||
            !SupplierDiscoveryEvidencePolicy.ContainsSupplierName(name, nameEvidence!.Excerpt) ||
            !input.TryGetProperty("facts", out var facts) || facts.ValueKind != JsonValueKind.Array ||
            facts.GetArrayLength() > MaxFactsPerCandidate)
            return false;

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
        foreach (var fact in facts.EnumerateArray())
        {
            if (TryParseFact(fact, evidenceByUrl, out var observation))
                observations.Add(observation!);
            else
                rejectedFacts++;
        }

        candidate = new SupplierDiscoveryCandidate(name.Trim(), nameEvidence!, websiteUrl, websiteEvidence,
            observations.AsReadOnly());
        return true;
    }

    private static bool TryParseFact(JsonElement fact, IReadOnlyDictionary<string, SupplierDiscoveryEvidence> evidenceByUrl,
        out SupplierObservedFact? observation)
    {
        observation = null;
        if (fact.ValueKind != JsonValueKind.Object ||
            ReadString(fact, "fieldKey") is not { } fieldKey || !SupplierDiscoveryEvidencePolicy.IsAllowedFactField(fieldKey) ||
            ReadString(fact, "itemKey") is not { } itemKey || itemKey.Length > 200 ||
            !fact.TryGetProperty("value", out var value) || !SupplierDiscoveryEvidencePolicy.IsSupportedFactValue(value) ||
            !TryGetEvidence(fact, "sourceUrl", evidenceByUrl, out var evidence) ||
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

    private static AiProviderException InvalidResponse() => new(ProviderFailureCode.InvalidResponse);
}
