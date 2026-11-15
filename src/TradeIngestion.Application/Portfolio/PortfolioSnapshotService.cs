using Microsoft.Extensions.Logging;
using TradeIngestion.Domain.Portfolio;

namespace TradeIngestion.Application.Portfolio;

public interface IPortfolioSnapshotService
{
    Task<SnapshotQueryResult> GetSnapshotAsync(string accountCode, DateOnly? snapshotDate, CancellationToken cancellationToken);
}

/// <summary>
/// Builds a point-in-time portfolio snapshot. The calculation rules live in the domain
/// (<see cref="AverageCostCalculator"/>, <see cref="PositionValuation"/>) and are mirrored by dbo.GetPortfolioSnapshot.
/// </summary>
public sealed partial class PortfolioSnapshotService : IPortfolioSnapshotService
{
    public const string BaseCurrency = "USD";

    private readonly IPortfolioReadStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PortfolioSnapshotService> _logger;

    public PortfolioSnapshotService(IPortfolioReadStore store, TimeProvider timeProvider, ILogger<PortfolioSnapshotService> logger)
    {
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<SnapshotQueryResult> GetSnapshotAsync(string accountCode, DateOnly? snapshotDate, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var date = snapshotDate ?? today;
        if (date > today)
        {
            // A future snapshot would silently present today's knowledge as if it were final.
            return new SnapshotQueryResult(SnapshotQueryStatus.InvalidDate, null, $"Snapshot date {date:yyyy-MM-dd} is in the future (today is {today:yyyy-MM-dd} UTC).");
        }

        var account = await _store.FindAccountByCodeAsync(accountCode, cancellationToken);
        if (account is null)
        {
            return new SnapshotQueryResult(SnapshotQueryStatus.AccountNotFound, null, $"Account '{accountCode}' does not exist.");
        }

        // Knowledge cutoff: the end of the snapshot day in UTC (exclusive). Corrections received after that are not
        // visible, so a historical snapshot is reproducible no matter when it is requested.
        var knowledgeCutoffUtc = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        var trades = await _store.GetEffectiveTradesAsync(account.Id, date, knowledgeCutoffUtc, cancellationToken);
        var costBases = AverageCostCalculator.Calculate(trades);
        var instrumentIds = costBases.Select(p => p.InstrumentId).ToArray();

        var instruments = await _store.GetInstrumentsAsync(instrumentIds, cancellationToken);
        var prices = await _store.GetLatestPricesAsync(instrumentIds, date, cancellationToken);

        var positions = costBases
            .Select(position =>
            {
                var instrument = instruments[position.InstrumentId];
                prices.TryGetValue(position.InstrumentId, out var price);
                var valuation = PositionValuation.Value(position, price?.ClosePrice, price?.PriceDate);
                return new PositionSnapshot(
                    instrument.Isin,
                    instrument.Symbol,
                    position.NetQuantity,
                    position.AverageUnitCost,
                    valuation.MarketPrice,
                    valuation.PriceDate,
                    valuation.MarketValue,
                    valuation.CostBasis,
                    valuation.UnrealizedPnl,
                    valuation.Status.ToString());
            })
            .OrderBy(p => p.Symbol, StringComparer.Ordinal)
            .ThenBy(p => p.Isin, StringComparer.Ordinal)
            .ToList();

        var unpriced = positions.Count(p => p.ValuationStatus == nameof(ValuationStatus.MissingPrice));
        if (unpriced > 0)
        {
            LogUnpricedPositions(_logger, accountCode, date, unpriced);
        }

        var snapshot = new PortfolioSnapshot(
            account.AccountCode,
            date,
            BaseCurrency,
            positions,
            TotalMarketValue: positions.Sum(p => p.MarketValue ?? 0m),
            TotalUnrealizedPnl: positions.Sum(p => p.UnrealizedPnl ?? 0m),
            PositionCount: positions.Count,
            UnpricedPositionCount: unpriced,
            IsValuationComplete: unpriced == 0);

        return new SnapshotQueryResult(SnapshotQueryStatus.Found, snapshot, null);
    }

    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning,
        Message = "Snapshot for AccountId {AccountId} on {SnapshotDate} has {UnpricedPositionCount} position(s) without a market price; totals are incomplete")]
    private static partial void LogUnpricedPositions(ILogger logger, string accountId, DateOnly snapshotDate, int unpricedPositionCount);
}
