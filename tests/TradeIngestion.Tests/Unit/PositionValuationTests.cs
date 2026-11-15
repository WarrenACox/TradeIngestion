using TradeIngestion.Domain.Portfolio;

namespace TradeIngestion.Tests.Unit;

public sealed class PositionValuationTests
{
    private static readonly DateOnly PriceDate = new(2025, 3, 1);

    [Fact]
    public void Demo_scenario_values_match_the_specification()
    {
        var valuation = PositionValuation.Value(new PositionCostBasis(1, 150m, 180m), 186m, PriceDate);

        Assert.Equal(27_900.00m, valuation.MarketValue);
        Assert.Equal(27_000.00m, valuation.CostBasis);
        Assert.Equal(900.00m, valuation.UnrealizedPnl);
        Assert.Equal(ValuationStatus.Valued, valuation.Status);
    }

    [Fact]
    public void Missing_price_yields_null_value_and_pnl_never_zero()
    {
        var valuation = PositionValuation.Value(new PositionCostBasis(1, 25m, 120m), null, null);

        Assert.Null(valuation.MarketValue);
        Assert.Null(valuation.UnrealizedPnl);
        Assert.Equal(3_000m, valuation.CostBasis);
        Assert.Equal(ValuationStatus.MissingPrice, valuation.Status);
    }

    [Fact]
    public void Short_position_reports_market_value_but_no_pnl()
    {
        var valuation = PositionValuation.Value(new PositionCostBasis(1, -5m, null), 10m, PriceDate);

        Assert.Equal(-50m, valuation.MarketValue);
        Assert.Null(valuation.CostBasis);
        Assert.Null(valuation.UnrealizedPnl);
        Assert.Equal(ValuationStatus.ShortPosition, valuation.Status);
    }

    [Fact]
    public void Money_is_rounded_half_away_from_zero_like_sql_server_round()
    {
        // Banker's rounding would give 0.12; SQL Server ROUND(0.125, 2) gives 0.13.
        var valuation = PositionValuation.Value(new PositionCostBasis(1, 1m, 0.125m), 0.125m, PriceDate);

        Assert.Equal(0.13m, valuation.MarketValue);
    }
}
