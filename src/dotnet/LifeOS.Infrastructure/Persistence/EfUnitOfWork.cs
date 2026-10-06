using LifeOS.Application.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Infrastructure.Persistence;

// IUnitOfWork on the scope's DbContext: every store and repository of the scope shares this context,
// so their statements (ExecuteSql, ExecuteUpdate, SaveChanges) run inside the transaction.
internal sealed class EfUnitOfWork(LifeOSDbContext db) : IUnitOfWork
{
    public async Task<bool> TryInTransactionAsync(Func<CancellationToken, Task<bool>> work, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Units of work do not nest: put all atomic work in one delegate.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await work(cancellationToken))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
