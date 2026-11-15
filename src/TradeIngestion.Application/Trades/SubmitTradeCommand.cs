namespace TradeIngestion.Application.Trades;

/// <summary>
/// Raw trade submission as received. Fields are nullable so that validation can report every problem at once
/// instead of failing on the first missing field during deserialization.
/// </summary>
public sealed record SubmitTradeCommand(
    string? ExternalRef,
    string? AccountId,
    string? Isin,
    string? Symbol,
    string? Side,
    decimal? Quantity,
    decimal? Price,
    DateOnly? TradeDate,
    DateTimeOffset? AsOf,
    string? CorrelationId);
