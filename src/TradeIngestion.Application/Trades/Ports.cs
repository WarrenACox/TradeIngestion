using TradeIngestion.Domain.Accounts;
using TradeIngestion.Domain.Instruments;
using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Application.Trades;

/// <summary>
/// Write-side access to the Trade aggregate. Deliberately narrow (not a generic repository): the one non-trivial
/// operation is a locking read whose semantics are what make concurrent submissions safe.
/// </summary>
public interface ITradeRepository
{
    /// <summary>
    /// Loads the trade (with all versions) and takes an update/range lock on its ExternalRef key for the rest of the
    /// current transaction - even when the trade does not exist yet - so concurrent submissions for the same
    /// ExternalRef are serialised. Must be called inside <see cref="Common.IUnitOfWork.ExecuteInTransactionAsync{T}"/>.
    /// </summary>
    Task<Trade?> GetForUpdateAsync(string externalRef, CancellationToken cancellationToken);

    void Add(Trade trade);
}

public interface IReferenceDataReader
{
    Task<Account?> FindAccountByCodeAsync(string accountCode, CancellationToken cancellationToken);

    Task<Instrument?> FindInstrumentByIsinAsync(string isin, CancellationToken cancellationToken);
}

public sealed record TradeVersionView(
    int VersionNumber,
    string AccountId,
    string Isin,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal Price,
    DateOnly TradeDate,
    DateTime AsOfUtc,
    DateTime ReceivedAtUtc,
    string? CorrelationId,
    bool IsCurrent);

public sealed record TradeHistoryView(
    long TradeId,
    string ExternalRef,
    int CurrentVersion,
    DateTime LatestAsOfUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<TradeVersionView> Versions);

/// <summary>Read model for the audit/history endpoint.</summary>
public interface ITradeHistoryReader
{
    Task<TradeHistoryView?> GetHistoryAsync(string externalRef, CancellationToken cancellationToken);
}
