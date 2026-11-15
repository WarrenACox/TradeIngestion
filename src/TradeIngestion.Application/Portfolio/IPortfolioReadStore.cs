using TradeIngestion.Domain.Accounts;
using TradeIngestion.Domain.Portfolio;

namespace TradeIngestion.Application.Portfolio;

/// <summary>Read-side queries backing the portfolio snapshot. All queries are no-tracking.</summary>
public interface IPortfolioReadStore
{
    Task<Account?> FindAccountByCodeAsync(string accountCode, CancellationToken cancellationToken);

    /// <summary>
    /// Returns exactly one row per trade: the latest version with AsOf before <paramref name="knowledgeCutoffUtc"/>,
    /// kept only if that version belongs to the account and has TradeDate on or before <paramref name="snapshotDate"/>.
    /// (Version selection happens before the account/date filter so a correction that moves a trade can never
    /// resurrect an older version.)
    /// </summary>
    Task<IReadOnlyList<EffectiveTrade>> GetEffectiveTradesAsync(int accountId, DateOnly snapshotDate, DateTime knowledgeCutoffUtc, CancellationToken cancellationToken);

    /// <summary>Most recent close on or before <paramref name="onOrBefore"/> per instrument; instruments without one are absent.</summary>
    Task<IReadOnlyDictionary<int, LatestPrice>> GetLatestPricesAsync(IReadOnlyCollection<int> instrumentIds, DateOnly onOrBefore, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<int, InstrumentInfo>> GetInstrumentsAsync(IReadOnlyCollection<int> instrumentIds, CancellationToken cancellationToken);
}
