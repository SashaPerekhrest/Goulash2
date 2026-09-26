namespace Goulash.Domain;

public sealed class AiProviderSetting
{
    public static readonly Guid SingletonId = Guid.Parse("00000000-0000-7000-8000-000000000001");

    private AiProviderSetting() { }

    public AiProviderSetting(string providerId, string model, byte[]? encryptedApiKey, DateTimeOffset updatedAt)
    {
        if (updatedAt.Offset != TimeSpan.Zero) throw new ArgumentException("Dates must use UTC.", nameof(updatedAt));
        Id = SingletonId;
        ProviderId = providerId;
        Model = model;
        EncryptedApiKey = encryptedApiKey;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; private set; }
    public string ProviderId { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public byte[]? EncryptedApiKey { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
