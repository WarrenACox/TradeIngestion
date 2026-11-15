namespace TradeIngestion.Application.Common;

/// <summary>
/// Raised by the persistence layer when a write lost a race that the database detected
/// (unique-key violation or optimistic-concurrency failure). The operation can be safely re-evaluated.
/// </summary>
public sealed class PersistenceConflictException : Exception
{
    public PersistenceConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
