using Microsoft.EntityFrameworkCore;
using TradeIngestion.Application.Portfolio;
using TradeIngestion.Application.Trades;
using TradeIngestion.Domain.Accounts;
using TradeIngestion.Domain.Instruments;
using TradeIngestion.Domain.Portfolio;
using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Infrastructure.Persistence;

internal sealed class ReferenceDataReader : IReferenceDataReader
{
    private readonly TradeIngestionDbContext _db;

    public ReferenceDataReader(TradeIngestionDbContext db) => _db = db;

    public Task<Account?> FindAccountByCodeAsync(string accountCode, CancellationToken cancellationToken) =>
        _db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.AccountCode == accountCode, cancellationToken);

    public Task<Instrument?> FindInstrumentByIsinAsync(string isin, CancellationToken cancellationToken) =>
        _db.Instruments.AsNoTracking().SingleOrDefaultAsync(i => i.Isin == isin, cancellationToken);
}

internal sealed class PortfolioReadStore : IPortfolioReadStore
{
    private readonly TradeIngestionDbContext _db;

    public PortfolioReadStore(TradeIngestionDbContext db) => _db = db;

    public Task<Account?> FindAccountByCodeAsync(string accountCode, CancellationToken cancellationToken) =>
        _db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.AccountCode == accountCode, cancellationToken);

    public async Task<IReadOnlyList<EffectiveTrade>> GetEffectiveTradesAsync(
        int accountId, DateOnly snapshotDate, DateTime knowledgeCutoffUtc, CancellationToken cancellationToken)
    {
        var versions = _db.TradeVersions.AsNoTracking();

        // Translates to NOT EXISTS: a version is effective if no later version of the same trade was known before
        // the cutoff. The subquery deliberately ignores AccountId/TradeDate (see IPortfolioReadStore remarks).
        var effective = await versions
            .Where(v => v.AsOfUtc < knowledgeCutoffUtc
                        && !versions.Any(newer => newer.TradeId == v.TradeId
                                                  && newer.AsOfUtc < knowledgeCutoffUtc
                                                  && newer.VersionNumber > v.VersionNumber))
            .Where(v => v.AccountId == accountId && v.TradeDate <= snapshotDate)
            .Select(v => new { v.TradeId, v.InstrumentId, v.Side, v.Quantity, v.Price, v.TradeDate })
            .ToListAsync(cancellationToken);

        return effective
            .Select(v => new EffectiveTrade(v.TradeId, v.InstrumentId, v.Side, v.Quantity, v.Price, v.TradeDate))
            .ToList();
    }

    public async Task<IReadOnlyDictionary<int, LatestPrice>> GetLatestPricesAsync(
        IReadOnlyCollection<int> instrumentIds, DateOnly onOrBefore, CancellationToken cancellationToken)
    {
        if (instrumentIds.Count == 0)
        {
            return new Dictionary<int, LatestPrice>();
        }

        var prices = _db.InstrumentPrices.AsNoTracking();
        var latest = await prices
            .Where(p => instrumentIds.Contains(p.InstrumentId)
                        && p.PriceDate <= onOrBefore
                        && !prices.Any(later => later.InstrumentId == p.InstrumentId
                                                && later.PriceDate <= onOrBefore
                                                && later.PriceDate > p.PriceDate))
            .Select(p => new LatestPrice(p.InstrumentId, p.PriceDate, p.ClosePrice))
            .ToListAsync(cancellationToken);

        return latest.ToDictionary(p => p.InstrumentId);
    }

    public async Task<IReadOnlyDictionary<int, InstrumentInfo>> GetInstrumentsAsync(IReadOnlyCollection<int> instrumentIds, CancellationToken cancellationToken)
    {
        if (instrumentIds.Count == 0)
        {
            return new Dictionary<int, InstrumentInfo>();
        }

        var instruments = await _db.Instruments.AsNoTracking()
            .Where(i => instrumentIds.Contains(i.Id))
            .Select(i => new InstrumentInfo(i.Id, i.Isin, i.Symbol))
            .ToListAsync(cancellationToken);

        return instruments.ToDictionary(i => i.InstrumentId);
    }
}

internal sealed class TradeHistoryReader : ITradeHistoryReader
{
    private readonly TradeIngestionDbContext _db;

    public TradeHistoryReader(TradeIngestionDbContext db) => _db = db;

    public async Task<TradeHistoryView?> GetHistoryAsync(string externalRef, CancellationToken cancellationToken)
    {
        var trade = await _db.Trades.AsNoTracking()
            .Where(t => t.ExternalRef == externalRef)
            .Select(t => new { t.Id, t.ExternalRef, t.CurrentVersionNumber, t.LatestAsOfUtc, t.CreatedAtUtc, t.UpdatedAtUtc })
            .SingleOrDefaultAsync(cancellationToken);

        if (trade is null)
        {
            return null;
        }

        var versions = await (
                from v in _db.TradeVersions.AsNoTracking()
                join a in _db.Accounts on v.AccountId equals a.Id
                join i in _db.Instruments on v.InstrumentId equals i.Id
                where v.TradeId == trade.Id
                orderby v.VersionNumber
                select new { v.VersionNumber, a.AccountCode, i.Isin, i.Symbol, v.Side, v.Quantity, v.Price, v.TradeDate, v.AsOfUtc, v.ReceivedAtUtc, v.CorrelationId })
            .ToListAsync(cancellationToken);

        return new TradeHistoryView(
            trade.Id,
            trade.ExternalRef,
            trade.CurrentVersionNumber,
            trade.LatestAsOfUtc,
            trade.CreatedAtUtc,
            trade.UpdatedAtUtc,
            versions.Select(v => new TradeVersionView(
                    v.VersionNumber,
                    v.AccountCode,
                    v.Isin,
                    v.Symbol,
                    v.Side == TradeSide.Buy ? "BUY" : "SELL",
                    v.Quantity,
                    v.Price,
                    v.TradeDate,
                    v.AsOfUtc,
                    v.ReceivedAtUtc,
                    v.CorrelationId,
                    v.VersionNumber == trade.CurrentVersionNumber))
                .ToList());
    }
}
