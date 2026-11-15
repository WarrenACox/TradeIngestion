namespace TradeIngestion.Application.Trades;

/// <summary>Bound from configuration section "TradeProcessing". Read through IOptionsMonitor so it can change without a restart.</summary>
public sealed class TradeProcessingOptions
{
    public const string SectionName = "TradeProcessing";

    /// <summary>
    /// When true: duplicates are ignored idempotently, later-AsOf corrections create new versions, stale events are ignored.
    /// When false (legacy behaviour): first submission wins and any re-submission of an ExternalRef is rejected with 409.
    /// The decision the new logic would have made is still logged ("shadow mode") so the rollout can be validated.
    /// </summary>
    public bool EnableVersionedCorrections { get; set; } = true;

    /// <summary>How many times a submission is re-evaluated after losing a database-detected race. Must be at least 1.</summary>
    public int MaxConflictRetries { get; set; } = 3;
}
