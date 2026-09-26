using System.Security.Claims;
using System.Text;
using Goulash.Api.Auth;
using Goulash.Api.Providers;
using Goulash.Application;
using Goulash.Domain;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Goulash.Api.Tests;

public sealed class AuthAndSettingsTests
{
    [Fact]
    public void RevokedSessionCannotBeUsedAgain()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new AdminSessionStore(cache);
        var principal = new ClaimsPrincipal(sessions.CreateIdentity());

        Assert.True(sessions.IsActive(principal));
        sessions.Revoke(principal);
        Assert.False(sessions.IsActive(principal));
        Assert.False(sessions.IsActive(new ClaimsPrincipal(AdminPasswordVerifier.CreateIdentity())));
    }

    [Fact]
    public void RegistryExposesOnlyCompiledWebAdapters()
    {
        var registry = new AiProviderRegistry([
            new FakeAdapter("web", true),
            new FakeAdapter("offline", false)
        ]);

        Assert.Single(registry.WebSearchProviders);
        Assert.NotNull(registry.FindWebSearchProvider("web"));
        Assert.Null(registry.FindWebSearchProvider("offline"));
        Assert.Null(registry.FindWebSearchProvider("https://arbitrary.example"));
    }

    [Fact]
    public void EncryptedKeyIsNotStoredAsPlaintextAndDeletionPreservesSelection()
    {
        var protector = new AiApiKeyProtector(Convert.ToBase64String(new byte[32]));
        const string apiKey = "secret-api-key-for-test";
        var encrypted = protector.Protect(apiKey);
        var setting = new AiProviderSetting("web", "model-1", encrypted, DateTimeOffset.UtcNow);

        Assert.DoesNotContain(apiKey, Encoding.UTF8.GetString(encrypted));
        Assert.Equal(apiKey, protector.Unprotect(setting.EncryptedApiKey!));

        setting.RemoveApiKey(DateTimeOffset.UtcNow);
        Assert.Null(setting.EncryptedApiKey);
        Assert.Equal("web", setting.ProviderId);
        Assert.Equal("model-1", setting.Model);
    }

    [Fact]
    public void ExistingKeyCanBeKeptOnlyForTheSameProvider()
    {
        var protector = new AiApiKeyProtector(Convert.ToBase64String(new byte[32]));
        var encrypted = protector.Protect("first-key");
        var setting = new AiProviderSetting("web", "model-1", encrypted, DateTimeOffset.UtcNow);

        Assert.True(AiSettingsKeyPolicy.RequiresNewKey(null, "web", null));
        Assert.False(AiSettingsKeyPolicy.RequiresNewKey(setting, "web", null));
        Assert.Same(encrypted, AiSettingsKeyPolicy.SelectEncryptedKey(setting, null, protector));
        Assert.True(AiSettingsKeyPolicy.RequiresNewKey(setting, "other", null));

        var replacement = AiSettingsKeyPolicy.SelectEncryptedKey(setting, "second-key", protector);
        Assert.NotEqual(encrypted, replacement);
        Assert.Equal("second-key", protector.Unprotect(replacement));

        setting.RemoveApiKey(DateTimeOffset.UtcNow);
        Assert.True(AiSettingsKeyPolicy.RequiresNewKey(setting, "web", null));
    }

    private sealed class FakeAdapter(string id, bool supportsWebSearch) : IAiProviderAdapter
    {
        public string Id => id;
        public string DisplayName => id;
        public bool SupportsWebSearch => supportsWebSearch;
        public Task<AiProviderCheckResult> CheckConnectionAsync(string model, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(new AiProviderCheckResult(true, supportsWebSearch));
    }
}
