using System.Text.Json;

namespace Goulash.Application;

/// <summary>Server-compiled provider adapter. Only adapters with web search are exposed to clients.</summary>
public interface IAiProviderAdapter
{
    string Id { get; }
    string DisplayName { get; }
    bool SupportsWebSearch { get; }
    bool SupportsFreeformModel => false;
    bool SupportsProviderRouting => false;
    string DefaultDiscoveryPrompt => AiProviderPromptDefaults.Shared;
    IReadOnlyCollection<string> SupportedModels => Array.Empty<string>();

    bool SupportsModel(string model) =>
        SupportedModels.Contains(model, StringComparer.Ordinal);

    /// <summary>Confirms both provider connectivity and availability of web search for the selected model.</summary>
    Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey, CancellationToken cancellationToken);

    Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey, string? routeProvider,
        CancellationToken cancellationToken) => CheckConnectionAsync(model, apiKey, cancellationToken);
}

public sealed record AiProviderCheckResult(bool Connected, bool WebSearchAvailable);

/// <summary>Provider wire protocol only. The discovery algorithm and evidence checks are shared.</summary>
public interface IAiSearchTransport : IAiProviderAdapter
{
    Task<AiSearchResponse> SearchAsync(string model, string apiKey, string? routeProvider,
        string systemPrompt, string userPrompt, string searchQuery, CancellationToken cancellationToken);
}

public sealed record AiSearchResponse(string Content, IReadOnlyList<SupplierDiscoveryEvidence> Evidence,
    string? FinishReason = null);

/// <summary>Configured server-side discovery entry point; credentials are loaded from protected settings.</summary>
public interface ISupplierDiscoveryProvider
{
    Task<SupplierDiscoveryResult> DiscoverAsync(string query, SupplierDiscoveryFilters filters, int limit,
        CancellationToken cancellationToken);
}

public sealed record SupplierPriceRange(decimal? Min, decimal? Max, string? Currency, string? Unit);

public sealed record SupplierMinimumOrderBound(decimal Amount, string Unit);

public sealed record SupplierDiscoveryFilters(
    string? City = null,
    string? Region = null,
    string? Category = null,
    string? Product = null,
    SupplierPriceRange? Price = null,
    bool IncludeApproximatePrices = false,
    int? MaxDeliveryDays = null,
    SupplierMinimumOrderBound? MinMinimumOrder = null,
    SupplierMinimumOrderBound? MaxMinimumOrder = null);

public sealed record SupplierDiscoveryEvidence(Uri Url, string Title, string Excerpt, DateTimeOffset RetrievedAt);

public sealed record SupplierObservedFact(string FieldKey, string ItemKey, JsonElement Value,
    JsonElement NormalizedValue, SupplierDiscoveryEvidence Evidence);

public sealed record SupplierDiscoveryCandidate(string Name, SupplierDiscoveryEvidence NameEvidence,
    string? WebsiteUrl, SupplierDiscoveryEvidence? WebsiteEvidence,
    IReadOnlyList<SupplierObservedFact> Facts)
{
    public string? ConfirmedOfficialDomain => SupplierDiscoveryEvidencePolicy.ResolveOfficialDomain(this);

    public SupplierEvidenceStatus Classify(SupplierDiscoveryEvidence evidence) =>
        SupplierDiscoveryEvidencePolicy.IsEvidenceFromOfficialDomain(evidence, ConfirmedOfficialDomain)
            ? SupplierEvidenceStatus.Official
            : SupplierEvidenceStatus.External;
}

public enum SupplierEvidenceStatus
{
    Official,
    External
}

public sealed record SupplierDiscoveryResult(IReadOnlyList<SupplierDiscoveryCandidate> Candidates,
    int RejectedRecordCount, int RejectedFactCount = 0, int EvidenceCount = 0,
    string Outcome = "candidates", IReadOnlyDictionary<string, int>? RejectedReasons = null);

public enum ProviderFailureCode
{
    NotConfigured,
    Unavailable,
    Timeout,
    InvalidResponse,
    UnsupportedModel
}

public sealed class AiProviderException(ProviderFailureCode code, string stage = "transport") : Exception("The AI provider request failed.")
{
    public ProviderFailureCode Code { get; } = code;
    public string Stage { get; } = stage;
}
