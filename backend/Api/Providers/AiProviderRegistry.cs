using Goulash.Application;

namespace Goulash.Api.Providers;

public interface IAiProviderRegistry
{
    IReadOnlyCollection<IAiProviderAdapter> WebSearchProviders { get; }
    IAiProviderAdapter? FindWebSearchProvider(string providerId);
}

public sealed class AiProviderRegistry : IAiProviderRegistry
{
    private readonly IReadOnlyCollection<IAiProviderAdapter> _webSearchProviders;

    public AiProviderRegistry(IEnumerable<IAiProviderAdapter> adapters)
    {
        var providers = adapters.Where(adapter => adapter.SupportsWebSearch).ToArray();
        if (providers.Any(adapter => string.IsNullOrWhiteSpace(adapter.Id) || adapter.Id.Length > 120 ||
                                     string.IsNullOrWhiteSpace(adapter.DisplayName)))
            throw new InvalidOperationException("A web-search provider adapter has invalid identity metadata.");

        var duplicateId = providers.GroupBy(adapter => adapter.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Skip(1).Any());
        if (duplicateId is not null)
            throw new InvalidOperationException($"More than one web-search adapter uses provider id '{duplicateId.Key}'.");

        _webSearchProviders = Array.AsReadOnly(providers);
    }

    public IReadOnlyCollection<IAiProviderAdapter> WebSearchProviders => _webSearchProviders;

    public IAiProviderAdapter? FindWebSearchProvider(string providerId) =>
        _webSearchProviders.FirstOrDefault(adapter => string.Equals(adapter.Id, providerId, StringComparison.Ordinal));
}
