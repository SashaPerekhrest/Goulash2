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
    AiApiKeyProtector keyProtector) : ISupplierDiscoveryProvider
{
    public async Task<SupplierDiscoveryResult> DiscoverAsync(string query, SupplierDiscoveryFilters filters, int limit,
        CancellationToken cancellationToken)
    {
        var setting = await db.AiProviderSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == AiProviderSetting.SingletonId, cancellationToken);
        if (setting?.EncryptedApiKey is null)
            throw new AiProviderException(ProviderFailureCode.NotConfigured);

        if (registry.FindWebSearchProvider(setting.ProviderId) is not ISupplierDiscoveryAdapter adapter)
            throw new AiProviderException(ProviderFailureCode.Unavailable);
        if (!adapter.SupportsModel(setting.Model))
            throw new AiProviderException(ProviderFailureCode.UnsupportedModel);

        string apiKey;
        try
        {
            apiKey = keyProtector.Unprotect(setting.EncryptedApiKey);
        }
        catch (CryptographicException)
        {
            throw new AiProviderException(ProviderFailureCode.Unavailable);
        }

        return await adapter.DiscoverAsync(setting.Model, apiKey, query, filters, Math.Clamp(limit, 1, 5), cancellationToken);
    }
}
