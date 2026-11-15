namespace TradeIngestion.Application.Portfolio;

public sealed record PositionSnapshot(
    string Isin,
    string Symbol,
    decimal NetQuantity,
    decimal? AverageUnitCost,
    decimal? MarketPrice,
    DateOnly? PriceDate,
    decimal? MarketValue,
    decimal? CostBasis,
    decimal? UnrealizedPnl,
    string ValuationStatus);

public sealed record PortfolioSnapshot(
    string AccountId,
    DateOnly SnapshotDate,
    string BaseCurrency,
    IReadOnlyList<PositionSnapshot> Positions,
    decimal TotalMarketValue,
    decimal TotalUnrealizedPnl,
    int PositionCount,
    int UnpricedPositionCount,
    bool IsValuationComplete);

public enum SnapshotQueryStatus
{
    Found,
    AccountNotFound,
    InvalidDate,
}

public sealed record SnapshotQueryResult(SnapshotQueryStatus Status, PortfolioSnapshot? Snapshot, string? Error);

public sealed record LatestPrice(int InstrumentId, DateOnly PriceDate, decimal ClosePrice);

public sealed record InstrumentInfo(int InstrumentId, string Isin, string Symbol);
