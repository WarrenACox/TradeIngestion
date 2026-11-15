using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Domain.Portfolio;

/// <summary>The single effective version of a trade for a given snapshot (one row per ExternalRef).</summary>
public sealed record EffectiveTrade(long TradeId, int InstrumentId, TradeSide Side, decimal Quantity, decimal Price, DateOnly TradeDate);

/// <summary>Net position and average unit cost for one instrument. AverageUnitCost is null for short positions.</summary>
public sealed record PositionCostBasis(int InstrumentId, decimal NetQuantity, decimal? AverageUnitCost);

public enum ValuationStatus
{
    /// <summary>Price available and long position with a cost basis.</summary>
    Valued,

    /// <summary>No price on or before the snapshot date. Market value and P/L are null and excluded from totals.</summary>
    MissingPrice,

    /// <summary>Net short position. Market value is reported (negative); cost basis and P/L are null.</summary>
    ShortPosition,
}
