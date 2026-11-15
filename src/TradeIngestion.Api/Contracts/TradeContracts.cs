using System.Text.Json.Serialization;
using TradeIngestion.Api.Json;
using TradeIngestion.Application.Trades;

namespace TradeIngestion.Api.Contracts;

/// <summary>A trade event from an upstream booking system.</summary>
public sealed class SubmitTradeRequest
{
    /// <summary>Upstream unique reference. The idempotency key: re-submissions with the same value never double count.</summary>
    /// <example>TRX-1001</example>
    public string? ExternalRef { get; init; }

    /// <summary>Account code.</summary>
    /// <example>ACC-001</example>
    public string? AccountId { get; init; }

    /// <summary>ISO 6166 ISIN (check digit validated). Must exist in the instrument master.</summary>
    /// <example>US0378331005</example>
    public string? Isin { get; init; }

    /// <summary>Ticker symbol; must match the instrument master for the ISIN.</summary>
    /// <example>AAPL</example>
    public string? Symbol { get; init; }

    /// <summary>BUY or SELL.</summary>
    /// <example>BUY</example>
    public string? Side { get; init; }

    /// <summary>Positive quantity, up to 6 decimal places. Direction is given by Side.</summary>
    /// <example>120</example>
    public decimal? Quantity { get; init; }

    /// <summary>Execution price in USD, positive, up to 6 decimal places.</summary>
    /// <example>185.40</example>
    public decimal? Price { get; init; }

    /// <summary>Trade date (yyyy-MM-dd).</summary>
    /// <example>2025-03-01</example>
    public DateOnly? TradeDate { get; init; }

    /// <summary>
    /// Source knowledge timestamp. ISO 8601 with an explicit offset ("Z" or "+hh:mm") is mandatory; it decides
    /// whether a re-submission is a duplicate, a correction (later) or stale (earlier).
    /// </summary>
    /// <example>2025-03-01T10:15:00Z</example>
    [JsonConverter(typeof(RequireOffsetDateTimeOffsetConverter))]
    public DateTimeOffset? AsOf { get; init; }

    public SubmitTradeCommand ToCommand(string correlationId) =>
        new(ExternalRef, AccountId, Isin, Symbol, Side, Quantity, Price, TradeDate, AsOf, correlationId);
}

/// <summary>Result of an accepted (2xx) trade submission.</summary>
public sealed class TradeSubmissionResponse
{
    /// <example>TRX-1001</example>
    public required string ExternalRef { get; init; }

    /// <summary>Created | DuplicateIgnored | CorrectionApplied | StaleIgnored</summary>
    /// <example>Created</example>
    public required string Outcome { get; init; }

    /// <example>1</example>
    public long? TradeId { get; init; }

    /// <summary>The version that is effective after processing.</summary>
    /// <example>1</example>
    public int? EffectiveVersion { get; init; }

    /// <summary>Latest AsOf accepted for this trade (UTC).</summary>
    /// <example>2025-03-01T10:15:00Z</example>
    public DateTime? LatestAsOf { get; init; }

    /// <example>Trade accepted.</example>
    public required string Message { get; init; }

    public static TradeSubmissionResponse From(TradeSubmissionResult result) => new()
    {
        ExternalRef = result.ExternalRef ?? string.Empty,
        Outcome = result.Outcome.ToString(),
        TradeId = result.TradeId,
        EffectiveVersion = result.EffectiveVersion,
        LatestAsOf = result.LatestAsOfUtc,
        Message = result.Message,
    };
}
