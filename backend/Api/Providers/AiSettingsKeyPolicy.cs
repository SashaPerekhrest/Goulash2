using Goulash.Domain;

namespace Goulash.Api.Providers;

public static class AiSettingsKeyPolicy
{
    public static bool RequiresNewKey(AiProviderSetting? current, string providerId, string? newKey) =>
        newKey is null && (current is null || current.EncryptedApiKey is null ||
                           !string.Equals(current.ProviderId, providerId, StringComparison.Ordinal));

    public static byte[] SelectEncryptedKey(AiProviderSetting? current, string? newKey, AiApiKeyProtector protector) =>
        newKey is not null ? protector.Protect(newKey) : current!.EncryptedApiKey!;
}
