namespace TradeIngestion.Domain.Portfolio;

/// <summary>
/// Values a position. Money amounts are rounded to 2 dp (half away from zero), matching ROUND() in SQL Server.
/// Unrealized P/L = MarketValue - CostBasis, both already rounded, so components always reconcile to the cent.
/// </summary>
public sealed record PositionValuation(
    decimal? MarketPrice,
    DateOnly? PriceDate,
    decimal? MarketValue,
    decimal? CostBasis,
    decimal? UnrealizedPnl,
    ValuationStatus Status)
{
    public const int MoneyScale = 2;

    public static PositionValuation Value(PositionCostBasis position, decimal? marketPrice, DateOnly? priceDate)
    {
        ArgumentNullException.ThrowIfNull(position);

        decimal? marketValue = marketPrice is { } price ? RoundMoney(position.NetQuantity * price) : null;
        decimal? costBasis = position.AverageUnitCost is { } avg ? RoundMoney(position.NetQuantity * avg) : null;
        decimal? unrealized = marketValue is { } mv && costBasis is { } cb ? mv - cb : null;

        var status = marketPrice is null
            ? ValuationStatus.MissingPrice
            : position.NetQuantity < 0 ? ValuationStatus.ShortPosition : ValuationStatus.Valued;

        return new PositionValuation(marketPrice, priceDate, marketValue, costBasis, unrealized, status);
    }

    private static decimal RoundMoney(decimal value) => Math.Round(value, MoneyScale, MidpointRounding.AwayFromZero);
}
