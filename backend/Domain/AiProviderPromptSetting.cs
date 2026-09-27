namespace Goulash.Domain;

public sealed class AiProviderPromptSetting
{
    private AiProviderPromptSetting() { }

    public AiProviderPromptSetting(string providerId, string prompt, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("Provider id is required.", nameof(providerId));
        if (updatedAt.Offset != TimeSpan.Zero) throw new ArgumentException("Dates must use UTC.", nameof(updatedAt));
        ProviderId = providerId;
        Prompt = prompt;
        UpdatedAt = updatedAt;
    }

    public string ProviderId { get; private set; } = string.Empty;
    public string Prompt { get; private set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; private set; }

    public void Update(string prompt, DateTimeOffset updatedAt)
    {
        if (updatedAt.Offset != TimeSpan.Zero) throw new ArgumentException("Dates must use UTC.", nameof(updatedAt));
        Prompt = prompt;
        UpdatedAt = updatedAt;
    }
}
