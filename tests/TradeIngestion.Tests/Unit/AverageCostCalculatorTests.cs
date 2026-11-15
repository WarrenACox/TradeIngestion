using TradeIngestion.Domain.Portfolio;
using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Tests.Unit;

public sealed class AverageCostCalculatorTests
{
    private const int Aapl = 1;
    private const int Msft = 2;
    private static long _tradeId;

    private static EffectiveTrade Buy(decimal qty, decimal price, int day, int instrument = Aapl, long? id = null) =>
        new(id ?? Interlocked.Increment(ref _tradeId), instrument, TradeSide.Buy, qty, price, new DateOnly(2025, 1, day));

    private static EffectiveTrade Sell(decimal qty, decimal price, int day, int instrument = Aapl, long? id = null) =>
        new(id ?? Interlocked.Increment(ref _tradeId), instrument, TradeSide.Sell, qty, price, new DateOnly(2025, 1, day));

    [Fact]
    public void Single_buy_cost_equals_price()
    {
        var position = Assert.Single(AverageCostCalculator.Calculate(new[] { Buy(150, 180m, 1) }));

        Assert.Equal(150m, position.NetQuantity);
        Assert.Equal(180m, position.AverageUnitCost);
    }

    [Fact]
    public void Buys_are_quantity_weighted()
    {
        var position = Assert.Single(AverageCostCalculator.Calculate(new[] { Buy(100, 10m, 1), Buy(300, 20m, 2) }));

        Assert.Equal(400m, position.NetQuantity);
        Assert.Equal(17.5m, position.AverageUnitCost); // (1000 + 6000) / 400
    }

    [Fact]
    public void Sell_reduces_quantity_but_not_average_cost()
    {
        var position = Assert.Single(AverageCostCalculator.Calculate(new[] { Buy(100, 10m, 1), Buy(100, 20m, 2), Sell(50, 99m, 3) }));

        Assert.Equal(150m, position.NetQuantity);
        Assert.Equal(15m, position.AverageUnitCost);
    }

    [Fact]
    public void Going_flat_resets_the_cost_pool_for_the_next_holding_period()
    {
        var position = Assert.Single(AverageCostCalculator.Calculate(new[] { Buy(100, 150m, 1), Sell(100, 175m, 2), Buy(50, 170m, 3) }));

        Assert.Equal(50m, position.NetQuantity);
        Assert.Equal(170m, position.AverageUnitCost); // not (15000 + 8500) / 150
    }

    [Fact]
    public void Closed_positions_are_not_reported()
    {
        Assert.Empty(AverageCostCalculator.Calculate(new[] { Buy(10, 1m, 1), Sell(10, 2m, 2) }));
    }

    [Fact]
    public void Short_position_has_no_cost_basis()
    {
        var position = Assert.Single(AverageCostCalculator.Calculate(new[] { Buy(10, 5m, 1), Sell(15, 6m, 2) }));

        Assert.Equal(-5m, position.NetQuantity);
        Assert.Null(position.AverageUnitCost);
    }

    [Fact]
    public void Trades_are_ordered_by_trade_date_then_trade_id_regardless_of_input_order()
    {
        // Supplied out of order. Sorted by date, the day-2 sell flattens the day-1 buy, so only the day-3 buy is in the pool.
        var trades = new[] { Buy(50, 170m, 3, id: 30), Sell(100, 175m, 2, id: 20), Buy(100, 150m, 1, id: 10) };

        var position = Assert.Single(AverageCostCalculator.Calculate(trades));

        Assert.Equal(170m, position.AverageUnitCost);
    }

    [Fact]
    public void Positions_are_calculated_per_instrument()
    {
        var positions = AverageCostCalculator.Calculate(new[] { Buy(10, 100m, 1, Aapl), Buy(20, 300m, 1, Msft) });

        Assert.Equal(2, positions.Count);
        Assert.Equal(300m, positions.Single(p => p.InstrumentId == Msft).AverageUnitCost);
    }

    [Fact]
    public void Average_cost_is_rounded_to_six_places_half_away_from_zero()
    {
        var position = Assert.Single(AverageCostCalculator.Calculate(new[] { Buy(40, 400m, 1), Buy(20, 420m, 2) }));

        Assert.Equal(406.666667m, position.AverageUnitCost);
    }
}
