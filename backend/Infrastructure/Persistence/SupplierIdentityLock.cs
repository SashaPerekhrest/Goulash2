using Goulash.Application;
using Microsoft.EntityFrameworkCore;

namespace Goulash.Infrastructure.Persistence;

/// <summary>
/// Serializes duplicate lookup and insertion for one normalized name/region pair.
/// Call while an explicit EF transaction is active, before querying candidate suppliers.
/// </summary>
public sealed class SupplierIdentityLock(ApplicationDbContext db)
{
    public async Task AcquireAsync(string normalizedName, string? region, CancellationToken cancellationToken = default)
    {
        var key = SupplierIdentity.CreateLockKey(normalizedName, region);
        await AcquireKeyAsync(key, cancellationToken);
    }

    public async Task AcquireDomainAsync(string normalizedDomain, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(normalizedDomain))
            throw new ArgumentException("A normalized domain is required.", nameof(normalizedDomain));
        await AcquireKeyAsync($"supplier-domain:{normalizedDomain.Trim().ToLowerInvariant()}", cancellationToken);
    }

    public async Task AcquireKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Supplier identity locks require an active database transaction.");

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))",
            cancellationToken);
    }
}
