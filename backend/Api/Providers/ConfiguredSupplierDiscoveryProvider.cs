using System.Security.Cryptography;
using Goulash.Application;
using Goulash.Domain;
using Goulash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Goulash.Api.Providers;

/// <summary>Loads the encrypted active provider settings and invokes the registered discovery adapter.</summary>
public sealed class ConfiguredSupplierDiscoveryProvider(
    ApplicationDbContext db,
    IAiProviderRegistry registry,
    AiApiKeyProtector keyProtector,
    SupplierDiscoveryService discoveryService) : ISupplierDiscoveryProvider
{
    public async Task<SupplierDiscoveryResult> DiscoverAsync(string query, SupplierDiscoveryFilters filters, int limit,
        CancellationToken cancellationToken)
    {
        var setting = await db.AiProviderSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == AiProviderSetting.SingletonId, cancellationToken);
        if (setting?.EncryptedApiKey is null)
            throw new AiProviderException(ProviderFailureCode.NotConfigured);

        if (registry.FindWebSearchProvider(setting.ProviderId) is not IAiSearchTransport adapter)
            throw new AiProviderException(ProviderFailureCode.Unavailable);
        if (!adapter.SupportsModel(setting.Model))
            throw new AiProviderException(ProviderFailureCode.UnsupportedModel);

        var promptSetting = await db.AiProviderPromptSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ProviderId == "discovery", cancellationToken);
        var basePrompt = promptSetting?.Prompt ?? AiProviderPromptDefaults.Shared;

        string apiKey;
        try
        {
            apiKey = keyProtector.Unprotect(setting.EncryptedApiKey);
        }
        catch (CryptographicException)
        {
            throw new AiProviderException(ProviderFailureCode.Unavailable);
        }

        return await discoveryService.DiscoverAsync(adapter, setting.Model, apiKey, setting.RouteProvider,
            basePrompt, query, filters, limit, cancellationToken);
    }
}
