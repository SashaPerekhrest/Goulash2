namespace Goulash.Application;

/// <summary>Server-compiled provider adapter. Only adapters with web search are exposed to clients.</summary>
public interface IAiProviderAdapter
{
    string Id { get; }
    string DisplayName { get; }
    bool SupportsWebSearch { get; }

    /// <summary>Confirms both provider connectivity and availability of web search for the selected model.</summary>
    Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey, CancellationToken cancellationToken);
}

public sealed record AiProviderCheckResult(bool Connected, bool WebSearchAvailable);
