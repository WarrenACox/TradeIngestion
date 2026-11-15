using Microsoft.Extensions.Logging;
using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Application.Trades;

/// <summary>
/// Source-generated, strongly-typed structured log events. Quantities and prices are intentionally never logged:
/// identifiers and outcomes are enough to trace a trade, and the amounts are available in the audit table.
/// </summary>
internal static partial class TradeLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Trade submission {ExternalRef} processed: {ProcessingOutcome} (TradeId {TradeId}, TradeVersion {TradeVersion}, Decision {Decision})")]
    public static partial void SubmissionProcessed(ILogger logger, TradeSubmissionOutcome processingOutcome, string externalRef, long tradeId, int tradeVersion, string decision);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Trade submission {ExternalRef} failed validation on fields {InvalidFields}")]
    public static partial void ValidationFailed(ILogger logger, string? externalRef, string invalidFields);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "Trade submission {ExternalRef} rejected: unknown or inconsistent reference data (AccountId {AccountId}, Isin {Isin})")]
    public static partial void ReferenceDataRejected(ILogger logger, string externalRef, string accountId, string isin);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "Concurrency conflict persisting {ExternalRef} on attempt {Attempt}; re-evaluating")]
    public static partial void RetryingAfterConflict(ILogger logger, Exception exception, string externalRef, int attempt);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information,
        Message = "Versioned corrections disabled; {ExternalRef} rejected as existing. Shadow decision would have been {ShadowDecision}")]
    public static partial void ShadowDecision(ILogger logger, string externalRef, TradeSubmissionDecision shadowDecision);
}
