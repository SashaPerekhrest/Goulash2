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
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Supplier identity locks require an active database transaction.");

        var key = SupplierIdentity.CreateLockKey(normalizedName, region);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))",
            cancellationToken);
    }
}
