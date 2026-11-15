using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Domain.Portfolio;

/// <summary>
/// Holding-period weighted average cost.
/// <list type="bullet">
/// <item>Trades are ordered per instrument by (TradeDate, TradeId).</item>
/// <item>BUY adds quantity and adds Quantity x Price to the open cost pool.</item>
/// <item>SELL reduces quantity only; it relieves cost at the average, so it does not change the average.</item>
/// <item>Whenever the running quantity reaches zero or below, the holding period ends and the cost pool resets.</item>
/// <item>AverageUnitCost = open BUY cost / open BUY quantity in the current holding period, rounded to 6 dp (half away from zero).</item>
/// </list>
/// This is mirrored exactly by dbo.GetPortfolioSnapshot; see SOLUTION.md "Cost-basis assumptions".
/// </summary>
public static class AverageCostCalculator
{
    public const int UnitPriceScale = 6;

    public static IReadOnlyList<PositionCostBasis> Calculate(IEnumerable<EffectiveTrade> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);

        var positions = new List<PositionCostBasis>();
        foreach (var instrumentTrades in trades.GroupBy(t => t.InstrumentId))
        {
            decimal runningQuantity = 0m;
            decimal openBuyQuantity = 0m;
            decimal openBuyCost = 0m;

            foreach (var trade in instrumentTrades.OrderBy(t => t.TradeDate).ThenBy(t => t.TradeId))
            {
                if (trade.Side == TradeSide.Buy)
                {
                    runningQuantity += trade.Quantity;
                    openBuyQuantity += trade.Quantity;
                    openBuyCost += trade.Quantity * trade.Price;
                }
                else
                {
                    runningQuantity -= trade.Quantity;
                }

                if (runningQuantity <= 0m)
                {
                    openBuyQuantity = 0m;
                    openBuyCost = 0m;
                }
            }

            if (runningQuantity == 0m)
            {
                continue; // closed position: not reported
            }

            decimal? averageUnitCost = runningQuantity > 0m
                ? Math.Round(openBuyCost / openBuyQuantity, UnitPriceScale, MidpointRounding.AwayFromZero)
                : null;

            positions.Add(new PositionCostBasis(instrumentTrades.Key, runningQuantity, averageUnitCost));
        }

        return positions;
    }
}
