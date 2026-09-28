namespace Goulash.Domain;

/// <summary>
/// Verification state of a stored observation. Missing is represented by the absence of a current fact.
/// </summary>
public enum VerificationStatus
{
    Official,
    External,
    AiGenerated
}

/// <summary>
/// Read-model state; Missing is computed when no current SupplierFact exists and is never stored as a fact.
/// </summary>
public enum FactStatus
{
    Official,
    External,
    AiGenerated,
    Missing
}

public enum SourceType
{
    Official,
    External
}

public sealed record FactEvidence(string Url, string? Title, string Excerpt, SourceType Type, DateTimeOffset RetrievedAt);

public sealed record FactAlternative<T>(T Value, FactStatus Status, IReadOnlyList<FactEvidence> Sources,
    DateTimeOffset ObservedAt);

public sealed record SourcedValue<T>(T? Value, FactStatus Status, IReadOnlyList<FactEvidence> Sources,
    DateTimeOffset? ObservedAt, IReadOnlyList<FactAlternative<T>> Alternatives)
{
    public static SourcedValue<T> Missing { get; } = new(default, FactStatus.Missing,
        Array.Empty<FactEvidence>(), null, Array.Empty<FactAlternative<T>>());
}
