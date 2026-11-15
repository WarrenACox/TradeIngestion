namespace TradeIngestion.Domain.Trades;

/// <summary>
/// Immutable, append-only record of one version of a trade as known at <see cref="AsOfUtc"/>.
/// Versions are never updated or deleted; they are the audit trail.
/// </summary>
public sealed class TradeVersion
{
    private TradeVersion()
    {
    }

    internal TradeVersion(int versionNumber, TradeDetails details, DateTime asOfUtc, DateTime receivedAtUtc, string? correlationId)
    {
        VersionNumber = versionNumber;
        AccountId = details.AccountId;
        InstrumentId = details.InstrumentId;
        Side = details.Side;
        Quantity = details.Quantity;
        Price = details.Price;
        TradeDate = details.TradeDate;
        AsOfUtc = asOfUtc;
        ReceivedAtUtc = receivedAtUtc;
        CorrelationId = correlationId;
    }

    public long Id { get; private set; }

    public long TradeId { get; private set; }

    public int VersionNumber { get; private set; }

    public int AccountId { get; private set; }

    public int InstrumentId { get; private set; }

    public TradeSide Side { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal Price { get; private set; }

    public DateOnly TradeDate { get; private set; }

    /// <summary>Source-system knowledge time of this version (UTC). Drives correction ordering.</summary>
    public DateTime AsOfUtc { get; private set; }

    /// <summary>When this service persisted the version (UTC). Operational audit only.</summary>
    public DateTime ReceivedAtUtc { get; private set; }

    public string? CorrelationId { get; private set; }

    public TradeDetails Details => new(AccountId, InstrumentId, Side, Quantity, Price, TradeDate);
}
