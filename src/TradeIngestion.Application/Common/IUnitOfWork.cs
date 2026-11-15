namespace TradeIngestion.Application.Common;

/// <summary>
/// Runs an operation inside a single database transaction and commits it.
/// Implementations must make the operation safe to re-run (transient-fault retries re-invoke it from scratch).
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}
