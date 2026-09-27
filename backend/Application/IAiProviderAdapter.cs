using System.Text.Json;

namespace Goulash.Application;

/// <summary>Server-compiled provider adapter. Only adapters with web search are exposed to clients.</summary>
public interface IAiProviderAdapter
{
    string Id { get; }
    string DisplayName { get; }
    bool SupportsWebSearch { get; }
    IReadOnlyCollection<string> SupportedModels => Array.Empty<string>();

    bool SupportsModel(string model) =>
        SupportedModels.Contains(model, StringComparer.Ordinal);

    /// <summary>Confirms both provider connectivity and availability of web search for the selected model.</summary>
    Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey, CancellationToken cancellationToken);
}

public sealed record AiProviderCheckResult(bool Connected, bool WebSearchAvailable);

public interface ISupplierDiscoveryAdapter : IAiProviderAdapter
{
    Task<SupplierDiscoveryResult> DiscoverAsync(string model, string apiKey, string query,
        SupplierDiscoveryFilters filters, int limit, CancellationToken cancellationToken);
}

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
    int RejectedRecordCount, int RejectedFactCount = 0);

public enum ProviderFailureCode
{
    NotConfigured,
    Unavailable,
    Timeout,
    InvalidResponse,
    UnsupportedModel
}

public sealed class AiProviderException(ProviderFailureCode code) : Exception("The AI provider request failed.")
{
    public ProviderFailureCode Code { get; } = code;
}
