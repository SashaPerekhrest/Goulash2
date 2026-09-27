using System.Text.Json;

namespace Goulash.Domain;

public sealed class DiscoveryRun
{
    private DiscoveryRun() { }

    public DiscoveryRun(JsonElement query, DateTimeOffset startedAt)
    {
        if (startedAt.Offset != TimeSpan.Zero) throw new ArgumentException("Dates must use UTC.", nameof(startedAt));
        Id = Guid.CreateVersion7();
        QueryJson = query.GetRawText();
        StartedAt = startedAt;
        Status = "running";
    }

    public Guid Id { get; private set; }
    public string QueryJson { get; private set; } = "{}";
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public int AcceptedCount { get; private set; }
    public int RejectedCount { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? Outcome { get; private set; }
    public int EvidenceCount { get; private set; }

    public void Complete(int acceptedCount, int rejectedCount, DateTimeOffset finishedAt,
        string outcome = "candidates", int evidenceCount = 0)
    {
        if (Status != "running") throw new InvalidOperationException("Only a running discovery can be completed.");
        if (acceptedCount < 0 || rejectedCount < 0) throw new ArgumentOutOfRangeException(nameof(acceptedCount));
        if (finishedAt.Offset != TimeSpan.Zero || finishedAt < StartedAt)
            throw new ArgumentException("Finish time must be UTC and no earlier than its start time.", nameof(finishedAt));

        AcceptedCount = acceptedCount;
        RejectedCount = rejectedCount;
        Outcome = outcome;
        EvidenceCount = evidenceCount;
        FinishedAt = finishedAt;
        Status = "succeeded";
    }

    public void Fail(string errorCode, DateTimeOffset finishedAt, string stage = "transport")
    {
        if (Status != "running") throw new InvalidOperationException("Only a running discovery can fail.");
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 80)
            throw new ArgumentOutOfRangeException(nameof(errorCode));
        if (finishedAt.Offset != TimeSpan.Zero || finishedAt < StartedAt)
            throw new ArgumentException("Finish time must be UTC and no earlier than its start time.", nameof(finishedAt));

        FinishedAt = finishedAt;
        ErrorCode = errorCode;
        Outcome = stage;
        Status = "failed";
    }
}
