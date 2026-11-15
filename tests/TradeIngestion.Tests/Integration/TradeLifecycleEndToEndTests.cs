using System.Net;
using TradeIngestion.Tests.Integration.Infrastructure;

namespace TradeIngestion.Tests.Integration;

/// <summary>
/// The required end-to-end scenario, executed over HTTP against the real API and a real SQL Server database:
/// new trade -> duplicate -> correction -> snapshot, verifying quantity, valuation, preserved history, and that
/// the stored procedure agrees with the API.
/// </summary>
public sealed class TradeLifecycleEndToEndTests : IClassFixture<SqlServerApiFixture>
{
    private readonly SqlServerApiFixture _fixture;

    public TradeLifecycleEndToEndTests(SqlServerApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task New_duplicate_and_correction_produce_a_correct_snapshot_and_preserve_history()
    {
        var client = _fixture.CreateClient();
        var original = ApiClientExtensions.DemoTrade(); // TRX-1001, ACC-001, AAPL BUY 120 @ 185.40, AsOf 10:15Z

        // 1. New trade accepted.
        var created = await client.SubmitAsync(original);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/trades/TRX-1001", created.Headers.Location?.AbsolutePath);
        var createdBody = await created.ReadAsync<TradeResponse>();
        Assert.Equal("Created", createdBody.Outcome);
        Assert.Equal(1, createdBody.EffectiveVersion);

        // 2. Exact duplicate ignored; quantity must not increase.
        var duplicate = await client.SubmitAsync(original);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var duplicateBody = await duplicate.ReadAsync<TradeResponse>();
        Assert.Equal("DuplicateIgnored", duplicateBody.Outcome);
        Assert.Equal(createdBody.TradeId, duplicateBody.TradeId);
        Assert.Equal(1, await _fixture.CountVersionsAsync("TRX-1001"));
        var afterDuplicate = await client.GetSnapshotAsync("ACC-001", "2025-03-01");
        Assert.Equal(120m, Assert.Single(afterDuplicate.Positions).NetQuantity);

        // 3. Correction (later AsOf) applied as a new version.
        var correction = await client.SubmitAsync(original with { Quantity = 150m, Price = 180.00m, AsOf = "2025-03-01T11:30:00Z" });
        Assert.Equal(HttpStatusCode.OK, correction.StatusCode);
        var correctionBody = await correction.ReadAsync<TradeResponse>();
        Assert.Equal("CorrectionApplied", correctionBody.Outcome);
        Assert.Equal(2, correctionBody.EffectiveVersion);

        // 4. Snapshot reflects only the effective version, valued at the seeded AAPL price of 186.00.
        var snapshot = await client.GetSnapshotAsync("ACC-001", "2025-03-01");
        var position = Assert.Single(snapshot.Positions);
        Assert.Equal("US0378331005", position.Isin);
        Assert.Equal("AAPL", position.Symbol);
        Assert.Equal(150m, position.NetQuantity);
        Assert.Equal(180.00m, position.AverageUnitCost);
        Assert.Equal(186.00m, position.MarketPrice);
        Assert.Equal(27_900.00m, position.MarketValue);
        Assert.Equal(900.00m, position.UnrealizedPnl);
        Assert.Equal("Valued", position.ValuationStatus);
        Assert.Equal(27_900.00m, snapshot.TotalMarketValue);
        Assert.Equal(900.00m, snapshot.TotalUnrealizedPnl);
        Assert.True(snapshot.IsValuationComplete);
        Assert.Equal("USD", snapshot.BaseCurrency);

        // 5. History preserved: both versions stored, v1 untouched, v2 current.
        var history = await (await client.GetAsync("/api/trades/TRX-1001")).ReadAsync<HistoryResponse>();
        Assert.Equal(2, history.CurrentVersion);
        Assert.Collection(
            history.Versions,
            v1 =>
            {
                Assert.Equal(1, v1.VersionNumber);
                Assert.Equal(120m, v1.Quantity);
                Assert.Equal(185.40m, v1.Price);
                Assert.Equal(new DateTime(2025, 3, 1, 10, 15, 0, DateTimeKind.Utc), v1.AsOfUtc);
                Assert.False(v1.IsCurrent);
            },
            v2 =>
            {
                Assert.Equal(2, v2.VersionNumber);
                Assert.Equal(150m, v2.Quantity);
                Assert.Equal(180.00m, v2.Price);
                Assert.True(v2.IsCurrent);
            });
        Assert.Equal(1, await _fixture.CountTradesAsync("TRX-1001"));
        Assert.Equal(2, await _fixture.CountVersionsAsync("TRX-1001"));

        // 6. The SQL artifact agrees with the application calculation.
        var sql = await _fixture.ExecuteSnapshotProcedureAsync("ACC-001", new DateOnly(2025, 3, 1));
        var sqlPosition = Assert.Single(sql.Positions);
        Assert.Equal(150m, sqlPosition.NetQuantity);
        Assert.Equal(180.00m, sqlPosition.AverageUnitCost);
        Assert.Equal(186.00m, sqlPosition.MarketPrice);
        Assert.Equal(27_900.00m, sqlPosition.MarketValue);
        Assert.Equal(900.00m, sqlPosition.UnrealizedPnl);
        Assert.Equal(27_900.00m, sql.Summary.AccountTotalMarketValue);
        Assert.Equal(27_900.00m, sql.Summary.PortfolioValue);

        // 7. Point-in-time: a snapshot for the day before the trade shows nothing.
        var before = await client.GetSnapshotAsync("ACC-001", "2025-02-28");
        Assert.Empty(before.Positions);
        Assert.Equal(0m, before.TotalMarketValue);
    }
}
