namespace TradeIngestion.Domain.Trades;

/// <summary>Classification of a submission for an ExternalRef that already exists.</summary>
public enum TradeSubmissionDecision
{
    /// <summary>Same AsOf and same economic content as a stored version (a retry/replay). Nothing to do.</summary>
    Duplicate,

    /// <summary>Later AsOf with different economic content. A new version becomes effective.</summary>
    Correction,

    /// <summary>Later AsOf but identical economic content to the current version. Watermark advances, no new version.</summary>
    NoEconomicChange,

    /// <summary>Earlier AsOf than the latest one seen and not a replay of a stored version. Ignored.</summary>
    Stale,

    /// <summary>Same AsOf as the latest one seen but different content. The source contradicts itself; rejected for investigation.</summary>
    ConflictingPayload,
}
