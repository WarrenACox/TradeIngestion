using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TradeIngestion.Application.Common;

namespace TradeIngestion.Infrastructure.Persistence;

/// <summary>
/// Runs the operation in a READ COMMITTED transaction inside EF Core's execution strategy, so transient faults
/// (including deadlock victim 1205) re-run the whole unit from a clean change tracker.
/// READ COMMITTED is sufficient because the critical read takes explicit UPDLOCK + HOLDLOCK on the idempotency key;
/// a blanket SERIALIZABLE transaction would add range locks to every read and raise deadlock risk for no benefit.
/// </summary>
internal sealed class EfUnitOfWork : IUnitOfWork
{
    private const int UniqueConstraintViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    private readonly TradeIngestionDbContext _db;

    public EfUnitOfWork(TradeIngestionDbContext db) => _db = db;

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var strategy = _db.Database.CreateExecutionStrategy();

        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                var result = await operation(cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            });
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new PersistenceConflictException("The trade was modified concurrently.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: UniqueConstraintViolation or UniqueIndexViolation })
        {
            throw new PersistenceConflictException("A uniqueness constraint rejected a concurrent write.", ex);
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }
}
