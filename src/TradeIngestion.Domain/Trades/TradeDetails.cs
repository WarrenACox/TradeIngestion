namespace TradeIngestion.Domain.Trades;

/// <summary>
/// The economic content of a trade version. Two submissions with equal <see cref="TradeDetails"/> describe the same
/// economic event. Record equality on <see cref="decimal"/> is numeric (185.40m == 185.4m), which is what we want.
/// </summary>
public sealed record TradeDetails
{
    public TradeDetails(int accountId, int instrumentId, TradeSide side, decimal quantity, decimal price, DateOnly tradeDate)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive; direction is expressed by Side.");
        }

        if (price <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(price), "Price must be positive.");
        }

        if (!Enum.IsDefined(side))
        {
            throw new ArgumentOutOfRangeException(nameof(side));
        }

        AccountId = accountId;
        InstrumentId = instrumentId;
        Side = side;
        Quantity = quantity;
        Price = price;
        TradeDate = tradeDate;
    }

    public int AccountId { get; }

    public int InstrumentId { get; }

    public TradeSide Side { get; }

    public decimal Quantity { get; }

    public decimal Price { get; }

    public DateOnly TradeDate { get; }
}
