namespace TradeIngestion.Domain.Trades;

/// <summary>
/// Aggregate root for a logical trade identified by <see cref="ExternalRef"/>.
/// Holds the version history and decides how a new submission for the same ExternalRef is treated.
/// </summary>
public sealed class Trade
{
    private readonly List<TradeVersion> _versions = new();

    private Trade()
    {
    }

    public long Id { get; private set; }

    public string ExternalRef { get; private set; } = null!;

    public int CurrentVersionNumber { get; private set; }

    /// <summary>High-watermark of AsOf values accepted for this trade (UTC). Submissions older than this are stale.</summary>
    public DateTime LatestAsOfUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Optimistic concurrency token (SQL Server rowversion). Backstop behind the pessimistic key lock.</summary>
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public IReadOnlyCollection<TradeVersion> Versions => _versions.AsReadOnly();

    public TradeVersion CurrentVersion =>
        _versions.SingleOrDefault(v => v.VersionNumber == CurrentVersionNumber)
        ?? throw new InvalidOperationException($"Trade {ExternalRef} has no loaded version {CurrentVersionNumber}.");

    public static Trade CreateNew(string externalRef, TradeDetails details, DateTime asOfUtc, DateTime receivedAtUtc, string? correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalRef);
        ArgumentNullException.ThrowIfNull(details);
        EnsureUtc(asOfUtc, nameof(asOfUtc));
        EnsureUtc(receivedAtUtc, nameof(receivedAtUtc));

        var trade = new Trade
        {
            ExternalRef = externalRef,
            CurrentVersionNumber = 1,
            LatestAsOfUtc = asOfUtc,
            CreatedAtUtc = receivedAtUtc,
            UpdatedAtUtc = receivedAtUtc,
        };
        trade._versions.Add(new TradeVersion(1, details, asOfUtc, receivedAtUtc, correlationId));
        return trade;
    }

    /// <summary>Pure classification of a re-submission. Does not mutate state.</summary>
    public TradeSubmissionDecision Evaluate(TradeDetails details, DateTime asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(details);
        EnsureUtc(asOfUtc, nameof(asOfUtc));

        if (asOfUtc > LatestAsOfUtc)
        {
            return details == CurrentVersion.Details
                ? TradeSubmissionDecision.NoEconomicChange
                : TradeSubmissionDecision.Correction;
        }

        if (asOfUtc == LatestAsOfUtc)
        {
            return details == CurrentVersion.Details
                ? TradeSubmissionDecision.Duplicate
                : TradeSubmissionDecision.ConflictingPayload;
        }

        // Older than the watermark: an exact replay of a historical version is a harmless duplicate;
        // anything else is an out-of-order (stale) event that must not overwrite newer knowledge.
        return _versions.Any(v => v.AsOfUtc == asOfUtc && v.Details == details)
            ? TradeSubmissionDecision.Duplicate
            : TradeSubmissionDecision.Stale;
    }

    public TradeVersion ApplyCorrection(TradeDetails details, DateTime asOfUtc, DateTime receivedAtUtc, string? correlationId)
    {
        EnsureUtc(receivedAtUtc, nameof(receivedAtUtc));
        var decision = Evaluate(details, asOfUtc);
        if (decision != TradeSubmissionDecision.Correction)
        {
            throw new InvalidOperationException($"Cannot apply correction to {ExternalRef}: submission classified as {decision}.");
        }

        var version = new TradeVersion(CurrentVersionNumber + 1, details, asOfUtc, receivedAtUtc, correlationId);
        _versions.Add(version);
        CurrentVersionNumber = version.VersionNumber;
        LatestAsOfUtc = asOfUtc;
        UpdatedAtUtc = receivedAtUtc;
        return version;
    }

    /// <summary>Advances the AsOf watermark for a later re-statement that carries no economic change.</summary>
    public void AcknowledgeNoEconomicChange(TradeDetails details, DateTime asOfUtc, DateTime receivedAtUtc)
    {
        EnsureUtc(receivedAtUtc, nameof(receivedAtUtc));
        var decision = Evaluate(details, asOfUtc);
        if (decision != TradeSubmissionDecision.NoEconomicChange)
        {
            throw new InvalidOperationException($"Cannot acknowledge {ExternalRef}: submission classified as {decision}.");
        }

        LatestAsOfUtc = asOfUtc;
        UpdatedAtUtc = receivedAtUtc;
    }

    private static void EnsureUtc(DateTime value, string paramName)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamps must be UTC (DateTimeKind.Utc).", paramName);
        }
    }
}
