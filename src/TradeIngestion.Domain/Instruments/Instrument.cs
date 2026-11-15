namespace TradeIngestion.Domain.Instruments;

/// <summary>Instrument master record. ISIN is the canonical identifier; Symbol is informational and must match on ingestion.</summary>
public sealed class Instrument
{
    private Instrument()
    {
    }

    public Instrument(string isin, string symbol, string name, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isin);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        Isin = isin;
        Symbol = symbol;
        Name = name;
        Currency = currency;
    }

    public int Id { get; private set; }

    public string Isin { get; private set; } = null!;

    public string Symbol { get; private set; } = null!;

    public string Name { get; private set; } = null!;

    public string Currency { get; private set; } = null!;
}
