using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Tests.Unit;

public sealed class TradeAggregateTests
{
    private static readonly DateTime T0 = new(2025, 3, 1, 10, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime Received = new(2025, 3, 1, 10, 15, 1, DateTimeKind.Utc);

    private static TradeDetails Details(decimal quantity = 120m, decimal price = 185.40m) =>
        new(accountId: 1, instrumentId: 1, TradeSide.Buy, quantity, price, new DateOnly(2025, 3, 1));

    [Fact]
    public void CreateNew_starts_at_version_one_with_watermark_at_asof()
    {
        var trade = Trade.CreateNew("TRX-1", Details(), T0, Received, "corr");

        Assert.Equal(1, trade.CurrentVersionNumber);
        Assert.Equal(T0, trade.LatestAsOfUtc);
        Assert.Single(trade.Versions);
        Assert.Equal(Details(), trade.CurrentVersion.Details);
    }

    [Fact]
    public void Same_asof_and_same_content_is_a_duplicate_even_with_different_decimal_scale()
    {
        var trade = Trade.CreateNew("TRX-1", Details(price: 185.40m), T0, Received, null);

        Assert.Equal(TradeSubmissionDecision.Duplicate, trade.Evaluate(Details(price: 185.4m), T0));
    }

    [Fact]
    public void Same_asof_with_different_content_is_a_conflict()
    {
        var trade = Trade.CreateNew("TRX-1", Details(), T0, Received, null);

        Assert.Equal(TradeSubmissionDecision.ConflictingPayload, trade.Evaluate(Details(quantity: 121m), T0));
    }

    [Fact]
    public void Later_asof_with_different_content_is_a_correction_that_appends_a_version()
    {
        var trade = Trade.CreateNew("TRX-1", Details(), T0, Received, null);
        var later = T0.AddMinutes(75);

        Assert.Equal(TradeSubmissionDecision.Correction, trade.Evaluate(Details(150m, 180m), later));
        var version = trade.ApplyCorrection(Details(150m, 180m), later, Received.AddHours(1), null);

        Assert.Equal(2, version.VersionNumber);
        Assert.Equal(2, trade.CurrentVersionNumber);
        Assert.Equal(later, trade.LatestAsOfUtc);
        Assert.Equal(2, trade.Versions.Count);
        Assert.Equal(120m, trade.Versions.Single(v => v.VersionNumber == 1).Quantity); // history retained untouched
    }

    [Fact]
    public void Later_asof_with_identical_content_advances_watermark_without_new_version()
    {
        var trade = Trade.CreateNew("TRX-1", Details(), T0, Received, null);
        var later = T0.AddHours(1);

        Assert.Equal(TradeSubmissionDecision.NoEconomicChange, trade.Evaluate(Details(), later));
        trade.AcknowledgeNoEconomicChange(Details(), later, Received.AddHours(1));

        Assert.Single(trade.Versions);
        Assert.Equal(later, trade.LatestAsOfUtc);
        // A correction stamped between the original and the restatement is now stale.
        Assert.Equal(TradeSubmissionDecision.Stale, trade.Evaluate(Details(130m), T0.AddMinutes(30)));
    }

    [Fact]
    public void Earlier_asof_is_stale_unless_it_replays_a_stored_version_exactly()
    {
        var trade = Trade.CreateNew("TRX-1", Details(), T0, Received, null);
        trade.ApplyCorrection(Details(150m, 180m), T0.AddHours(1), Received, null);

        Assert.Equal(TradeSubmissionDecision.Duplicate, trade.Evaluate(Details(), T0)); // replay of v1
        Assert.Equal(TradeSubmissionDecision.Stale, trade.Evaluate(Details(999m), T0)); // v1's AsOf, other content
        Assert.Equal(TradeSubmissionDecision.Stale, trade.Evaluate(Details(), T0.AddMinutes(-5)));
    }

    [Fact]
    public void Applying_a_non_correction_is_rejected()
    {
        var trade = Trade.CreateNew("TRX-1", Details(), T0, Received, null);

        Assert.Throws<InvalidOperationException>(() => trade.ApplyCorrection(Details(), T0, Received, null));
    }

    [Fact]
    public void Non_utc_timestamps_are_rejected()
    {
        var local = DateTime.SpecifyKind(T0, DateTimeKind.Local);

        Assert.Throws<ArgumentException>(() => Trade.CreateNew("TRX-1", Details(), local, Received, null));
    }
}
