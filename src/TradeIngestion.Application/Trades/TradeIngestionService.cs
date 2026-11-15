using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeIngestion.Application.Common;
using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Application.Trades;

public interface ITradeIngestionService
{
    Task<TradeSubmissionResult> SubmitAsync(SubmitTradeCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates a trade submission: validate, resolve reference data, then classify and persist inside one
/// transaction that holds a key lock on the ExternalRef. Business classification lives in <see cref="Trade"/>.
/// </summary>
public sealed class TradeIngestionService : ITradeIngestionService
{
    private static readonly IReadOnlyDictionary<string, string[]> NoErrors = new Dictionary<string, string[]>();

    private readonly ITradeRepository _trades;
    private readonly IReferenceDataReader _referenceData;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOptionsMonitor<TradeProcessingOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TradeIngestionService> _logger;

    public TradeIngestionService(
        ITradeRepository trades,
        IReferenceDataReader referenceData,
        IUnitOfWork unitOfWork,
        IOptionsMonitor<TradeProcessingOptions> options,
        TimeProvider timeProvider,
        ILogger<TradeIngestionService> logger)
    {
        _trades = trades;
        _referenceData = referenceData;
        _unitOfWork = unitOfWork;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<TradeSubmissionResult> SubmitAsync(SubmitTradeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var (submission, validationErrors) = TradeSubmissionValidator.Validate(command);
        if (submission is null)
        {
            TradeLog.ValidationFailed(_logger, command.ExternalRef, string.Join(",", validationErrors.Keys));
            return TradeSubmissionResult.Failed(
                TradeSubmissionOutcome.ValidationFailed, command.ExternalRef, "The trade submission is invalid.", validationErrors);
        }

        var account = await _referenceData.FindAccountByCodeAsync(submission.AccountCode, cancellationToken);
        var instrument = await _referenceData.FindInstrumentByIsinAsync(submission.Isin, cancellationToken);

        var referenceErrors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (account is null)
        {
            referenceErrors["accountId"] = new[] { $"Account '{submission.AccountCode}' does not exist." };
        }

        if (instrument is null)
        {
            referenceErrors["isin"] = new[] { $"Instrument '{submission.Isin}' does not exist in the instrument master." };
        }
        else if (!string.Equals(instrument.Symbol, submission.Symbol, StringComparison.Ordinal))
        {
            referenceErrors["symbol"] = new[] { $"Symbol '{submission.Symbol}' does not match instrument {submission.Isin} (expected '{instrument.Symbol}')." };
        }

        if (referenceErrors.Count > 0)
        {
            TradeLog.ReferenceDataRejected(_logger, submission.ExternalRef, submission.AccountCode, submission.Isin);
            return TradeSubmissionResult.Failed(
                TradeSubmissionOutcome.ReferenceDataRejected, submission.ExternalRef, "The trade references unknown or inconsistent reference data.", referenceErrors);
        }

        var details = new TradeDetails(account!.Id, instrument!.Id, submission.Side, submission.Quantity, submission.Price, submission.TradeDate);
        var options = _options.CurrentValue;
        var maxAttempts = Math.Max(1, options.MaxConflictRetries) + 1;

        using var scope = _logger.BeginScope(
            "ExternalRef:{ExternalRef} AccountId:{AccountId} InstrumentId:{InstrumentId}",
            submission.ExternalRef,
            submission.AccountCode,
            instrument.Id);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var pending = await _unitOfWork.ExecuteInTransactionAsync(
                    ct => ProcessAsync(submission, details, command.CorrelationId, options.EnableVersionedCorrections, ct),
                    cancellationToken);

                // Built after commit so database-generated identifiers (TradeId) are populated.
                var trade = pending.Trade;
                var result = new TradeSubmissionResult(
                    pending.Outcome, trade.ExternalRef, trade.Id, trade.CurrentVersionNumber, trade.LatestAsOfUtc, pending.Message, NoErrors);

                TradeLog.SubmissionProcessed(_logger, result.Outcome, trade.ExternalRef, trade.Id, trade.CurrentVersionNumber, pending.Decision?.ToString() ?? "New");
                return result;
            }
            catch (PersistenceConflictException ex) when (attempt < maxAttempts)
            {
                // Another writer won a race the database detected (unique key / rowversion). Re-evaluating will
                // now see its committed state and classify this submission correctly (normally as a duplicate).
                TradeLog.RetryingAfterConflict(_logger, ex, submission.ExternalRef, attempt);
            }
        }
    }

    private async Task<PendingSubmission> ProcessAsync(
        ValidatedTradeSubmission submission,
        TradeDetails details,
        string? correlationId,
        bool versionedCorrectionsEnabled,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var existing = await _trades.GetForUpdateAsync(submission.ExternalRef, cancellationToken);

        if (existing is null)
        {
            var trade = Trade.CreateNew(submission.ExternalRef, details, submission.AsOfUtc, now, correlationId);
            _trades.Add(trade);
            return new PendingSubmission(TradeSubmissionOutcome.Created, trade, null, "Trade accepted.");
        }

        var decision = existing.Evaluate(details, submission.AsOfUtc);

        if (!versionedCorrectionsEnabled)
        {
            // Legacy behaviour; log what the new logic would have done (shadow mode) to validate before enabling.
            TradeLog.ShadowDecision(_logger, submission.ExternalRef, decision);
            return new PendingSubmission(
                TradeSubmissionOutcome.ExternalRefAlreadyExists, existing, decision,
                "A trade with this ExternalRef already exists and versioned corrections are disabled.");
        }

        switch (decision)
        {
            case TradeSubmissionDecision.Duplicate:
                return new PendingSubmission(TradeSubmissionOutcome.DuplicateIgnored, existing, decision, "Duplicate submission ignored; no changes applied.");

            case TradeSubmissionDecision.NoEconomicChange:
                existing.AcknowledgeNoEconomicChange(details, submission.AsOfUtc, now);
                return new PendingSubmission(
                    TradeSubmissionOutcome.DuplicateIgnored, existing, decision, "Later restatement with no economic change; AsOf watermark advanced, no new version.");

            case TradeSubmissionDecision.Correction:
                var version = existing.ApplyCorrection(details, submission.AsOfUtc, now, correlationId);
                return new PendingSubmission(
                    TradeSubmissionOutcome.CorrectionApplied, existing, decision, $"Correction applied; version {version.VersionNumber} is now effective. Prior versions retained.");

            case TradeSubmissionDecision.Stale:
                return new PendingSubmission(
                    TradeSubmissionOutcome.StaleIgnored, existing, decision, "Submission is older than the latest accepted version and was ignored.");

            case TradeSubmissionDecision.ConflictingPayload:
                return new PendingSubmission(
                    TradeSubmissionOutcome.ConflictingPayload, existing, decision, "Submission has the same AsOf as the current version but different content; not applied.");

            default:
                throw new InvalidOperationException($"Unhandled submission decision {decision}.");
        }
    }

    private sealed record PendingSubmission(TradeSubmissionOutcome Outcome, Trade Trade, TradeSubmissionDecision? Decision, string Message);
}
