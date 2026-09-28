using System.Collections.Concurrent;
using System.Text.Json;
using Goulash.Application;
using Goulash.Domain;
using Goulash.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Goulash.Api.Providers;

public sealed class DiscoveryBackgroundWorker(
    DiscoveryJobQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<DiscoveryBackgroundWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeJobs = new();

    public bool RequestCancellation(Guid id)
    {
        if (!_activeJobs.TryGetValue(id, out var cancellation)) return false;
        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueInterruptedJobsAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var id = await queue.DequeueAsync(stoppingToken);
            try
            {
                await ProcessAsync(id, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError("Discovery background job failed unexpectedly. DiscoveryId={DiscoveryId}, ErrorType={ErrorType}",
                    id, exception.GetType().Name);
                await FailUnexpectedJobAsync(id, stoppingToken);
            }
        }
    }

    private async Task RequeueInterruptedJobsAsync(CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var runs = await db.DiscoveryRuns.Where(run => run.Status == "queued" || run.Status == "running")
            .OrderBy(run => run.StartedAt).ToListAsync(token);
        foreach (var run in runs)
        {
            run.RequeueAfterRestart();
            await queue.EnqueueAsync(run.Id, token);
        }
        await db.SaveChangesAsync(token);
    }

    private async Task ProcessAsync(Guid id, CancellationToken token)
    {
        using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _activeJobs[id] = jobCancellation;
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<ApplicationDbContext>();
        var run = await db.DiscoveryRuns.SingleOrDefaultAsync(item => item.Id == id, token);
        if (run is null || run.Status != "queued")
        {
            _activeJobs.TryRemove(id, out _);
            return;
        }

        var claimed = await db.DiscoveryRuns.Where(item => item.Id == id && item.Status == "queued")
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, "running")
                .SetProperty(item => item.Stage, "searching"), token);
        if (claimed == 0)
        {
            _activeJobs.TryRemove(id, out _);
            return;
        }
        await db.Entry(run).ReloadAsync(token);
        var jobToken = jobCancellation.Token;
        try
        {
            var work = ReadWorkRequest(run.QueryJson);
            var provider = services.GetRequiredService<ISupplierDiscoveryProvider>();
            using var progressGate = new SemaphoreSlim(1, 1);
            var discovery = await provider.DiscoverAsync(work.Query, work.Filters, 20, jobToken, async progress =>
            {
                await progressGate.WaitAsync(jobToken);
                try
                {
                    run.UpdateProgress(progress.Stage, progress.CompletedCandidates, progress.TotalCandidates);
                    await db.SaveChangesAsync(jobToken);
                }
                finally
                {
                    progressGate.Release();
                }
            });

            jobToken.ThrowIfCancellationRequested();

            run.UpdateProgress("saving", run.CompletedCandidates, run.CandidateCount);
            await db.SaveChangesAsync(token);

            var response = await services.GetRequiredService<DiscoveryPersistence>().SaveAsync(run,
                discovery.Candidates, discovery.FailedProfileCount, jobToken, discovery.Outcome, discovery.SourcePageCount);
            logger.LogInformation("Discovery completed. DiscoveryId={DiscoveryId}, Accepted={Accepted}, FailedProfiles={FailedProfiles}, Outcome={Outcome}",
                id, response.AcceptedCount, response.FailedProfileCount, response.Outcome);
        }
        catch (AiProviderException exception) when (!jobToken.IsCancellationRequested)
        {
            run.Fail(ToErrorCode(exception.Code), DateTimeOffset.UtcNow, exception.Stage);
            await db.SaveChangesAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested)
        {
            run.Cancel(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        catch (Exception) when (jobToken.IsCancellationRequested)
        {
            run.Cancel(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        catch (Exception exception)
        {
            logger.LogError("Discovery job failed. DiscoveryId={DiscoveryId}, ErrorType={ErrorType}",
                id, exception.GetType().Name);
            run.Fail("INTERNAL_ERROR", DateTimeOffset.UtcNow, "processing");
            await db.SaveChangesAsync(token);
        }
        finally
        {
            _activeJobs.TryRemove(id, out _);
        }
    }

    private async Task FailUnexpectedJobAsync(Guid id, CancellationToken token)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var run = await db.DiscoveryRuns.SingleOrDefaultAsync(item => item.Id == id, token);
        if (run is null || run.Status is "failed" or "succeeded" or "cancelled") return;
        if (run.Status == "queued") run.MarkStarted();
        run.Fail("INTERNAL_ERROR", DateTimeOffset.UtcNow, "worker");
        await db.SaveChangesAsync(token);
    }

    private static DiscoveryWorkRequest ReadWorkRequest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var query = root.GetProperty("query").GetString() ?? string.Empty;
        var filters = root.GetProperty("filters").Deserialize<SupplierDiscoveryFilters>(JsonOptions)
            ?? new SupplierDiscoveryFilters();
        return new DiscoveryWorkRequest(query, filters);
    }

    private static string ToErrorCode(ProviderFailureCode code) => code switch
    {
        ProviderFailureCode.NotConfigured => "PROVIDER_NOT_CONFIGURED",
        ProviderFailureCode.Timeout => "PROVIDER_TIMEOUT",
        ProviderFailureCode.InvalidResponse => "PROVIDER_INVALID_RESPONSE",
        ProviderFailureCode.UnsupportedModel => "UNSUPPORTED_MODEL",
        _ => "PROVIDER_UNAVAILABLE"
    };
}

public sealed record DiscoveryWorkRequest(string Query, SupplierDiscoveryFilters Filters);
