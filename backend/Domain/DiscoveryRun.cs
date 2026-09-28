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
        Status = "queued";
        Stage = "queued";
    }

    public Guid Id { get; private set; }
    public string QueryJson { get; private set; } = "{}";
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public int AcceptedCount { get; private set; }
    public int FailedProfileCount { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? Outcome { get; private set; }
    public string Stage { get; private set; } = "queued";
    public int CandidateCount { get; private set; }
    public int CompletedCandidates { get; private set; }
    public string? ResultJson { get; private set; }

    public void MarkStarted()
    {
        if (Status != "queued") throw new InvalidOperationException("Only a queued discovery can start.");
        Status = "running";
        Stage = "searching";
    }

    public void UpdateProgress(string stage, int completedCandidates, int candidateCount)
    {
        if (Status != "running") throw new InvalidOperationException("Only a running discovery can report progress.");
        if (string.IsNullOrWhiteSpace(stage) || stage.Length > 40 || completedCandidates < 0 ||
            candidateCount < 0 || completedCandidates > candidateCount)
            throw new ArgumentOutOfRangeException(nameof(completedCandidates));
        Stage = stage;
        CandidateCount = candidateCount;
        CompletedCandidates = completedCandidates;
    }

    public void RequeueAfterRestart()
    {
        if (Status != "running") return;
        Status = "queued";
        Stage = "queued";
        CandidateCount = 0;
        CompletedCandidates = 0;
        FailedProfileCount = 0;
    }

    public void Complete(int acceptedCount, int failedProfileCount, DateTimeOffset finishedAt,
        string outcome, string? resultJson = null)
    {
        if (Status != "running") throw new InvalidOperationException("Only a running discovery can be completed.");
        if (acceptedCount < 0 || failedProfileCount < 0) throw new ArgumentOutOfRangeException(nameof(acceptedCount));
        if (finishedAt.Offset != TimeSpan.Zero || finishedAt < StartedAt)
            throw new ArgumentException("Finish time must be UTC and no earlier than its start time.", nameof(finishedAt));

        AcceptedCount = acceptedCount;
        FailedProfileCount = failedProfileCount;
        Outcome = outcome;
        ResultJson = resultJson;
        Stage = "completed";
        CompletedCandidates = CandidateCount;
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
        Stage = "failed";
        Status = "failed";
    }
}
