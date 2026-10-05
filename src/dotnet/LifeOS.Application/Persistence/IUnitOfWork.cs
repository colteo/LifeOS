namespace LifeOS.Application.Persistence;

// One database transaction around several store/repository calls of the same request scope
// (they share one unit of work). AUTO-001 §8: a fenced automation completion, the artifact it
// produced and its notification deliveries commit together or not at all.
//
// The work commits only when it returns true; false or an exception rolls everything back (e.g. the
// fenced update affected no row because another attempt took the lease over). Calls do not nest:
// everything that must be atomic goes into one work delegate, and code running inside it must not
// open its own transaction.
public interface IUnitOfWork
{
    Task<bool> TryInTransactionAsync(Func<CancellationToken, Task<bool>> work, CancellationToken cancellationToken);
}
