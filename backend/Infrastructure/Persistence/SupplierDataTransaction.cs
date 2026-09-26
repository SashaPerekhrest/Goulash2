using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Goulash.Infrastructure.Persistence;

/// <summary>
/// Writes facts, sources, and their supplier/product/price projections under one database commit.
/// </summary>
public sealed class SupplierDataTransaction(ApplicationDbContext db, SupplierIdentityLock identityLock)
{
    public async Task<TResult> ExecuteAsync<TResult>(
        Func<ApplicationDbContext, SupplierIdentityLock, CancellationToken, Task<TResult>> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var result = await update(db, identityLock, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
