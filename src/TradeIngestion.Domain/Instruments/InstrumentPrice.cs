namespace TradeIngestion.Domain.Instruments;

/// <summary>End-of-day closing price for an instrument. One row per instrument per date.</summary>
public sealed class InstrumentPrice
{
    private InstrumentPrice()
    {
    }

    public InstrumentPrice(int instrumentId, DateOnly priceDate, decimal closePrice, string currency)
    {
        if (closePrice <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(closePrice), "Close price must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        InstrumentId = instrumentId;
        PriceDate = priceDate;
        ClosePrice = closePrice;
        Currency = currency;
    }

    public long Id { get; private set; }

    public int InstrumentId { get; private set; }

    public DateOnly PriceDate { get; private set; }

    public decimal ClosePrice { get; private set; }

    public string Currency { get; private set; } = null!;
}
