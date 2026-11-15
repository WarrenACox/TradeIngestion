namespace TradeIngestion.Application.Trades;

public enum TradeSubmissionOutcome
{
    /// <summary>First time this ExternalRef was seen; version 1 stored.</summary>
    Created,

    /// <summary>Exact re-submission (or a later restatement with no economic change). Nothing applied.</summary>
    DuplicateIgnored,

    /// <summary>Later AsOf with changed economics; new version stored and effective.</summary>
    CorrectionApplied,

    /// <summary>AsOf older than the latest accepted version; ignored to protect newer knowledge.</summary>
    StaleIgnored,

    /// <summary>Same AsOf as the latest accepted version but different content. Not applied; needs investigation.</summary>
    ConflictingPayload,

    /// <summary>Versioned corrections are disabled (feature flag off) and the ExternalRef already exists.</summary>
    ExternalRefAlreadyExists,

    /// <summary>Structural validation failed.</summary>
    ValidationFailed,

    /// <summary>Referenced account or instrument is unknown, or the symbol does not match the ISIN.</summary>
    ReferenceDataRejected,
}

public sealed record TradeSubmissionResult(
    TradeSubmissionOutcome Outcome,
    string? ExternalRef,
    long? TradeId,
    int? EffectiveVersion,
    DateTime? LatestAsOfUtc,
    string Message,
    IReadOnlyDictionary<string, string[]> Errors)
{
    public static TradeSubmissionResult Failed(TradeSubmissionOutcome outcome, string? externalRef, string message, IReadOnlyDictionary<string, string[]> errors) =>
        new(outcome, externalRef, null, null, null, message, errors);
}
