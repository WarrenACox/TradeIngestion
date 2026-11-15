using Microsoft.Data.SqlClient;
using TradeIngestion.Tests.Integration.Infrastructure;

namespace TradeIngestion.Tests.Integration;

/// <summary>
/// Proves dbo.GetPortfolioSnapshot and the C# snapshot agree on a non-trivial portfolio (seeded ACC-002):
/// a round trip that resets cost, a partial sell, a correction known only after 2025-03-01, carried-forward
/// prices and an unpriced instrument. Each date is also checked against hand-calculated expectations.
/// </summary>
public sealed class SnapshotSqlParityTests : IClassFixture<SqlServerApiFixture>
{
    private readonly SqlServerApiFixture _fixture;

    public SnapshotSqlParityTests(SqlServerApiFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("2025-01-15")] // AAPL 100 held, no price yet -> MissingPrice
    [InlineData("2025-01-25")] // AAPL round trip closed -> no AAPL position
    [InlineData("2025-03-01")] // SEED-2006 v1 (20 shares) effective
    [InlineData("2025-03-10")] // SEED-2006 v2 (25 shares) effective, newer MSFT price
    public async Task Stored_procedure_matches_api_snapshot(string date)
    {
        var api = await _fixture.CreateClient().GetSnapshotAsync("ACC-002", date);
        var sql = await _fixture.ExecuteSnapshotProcedureAsync("ACC-002", DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(api.Positions.Count, sql.Positions.Count);
        foreach (var (a, s) in api.Positions.Zip(sql.Positions))
        {
            Assert.Equal(a.Isin, s.Isin.Trim());
            Assert.Equal(a.Symbol, s.Symbol);
            Assert.Equal(a.NetQuantity, s.NetQuantity);
            Assert.Equal(a.AverageUnitCost, s.AverageUnitCost);
            Assert.Equal(a.MarketPrice, s.MarketPrice);
            Assert.Equal(a.MarketValue, s.MarketValue);
            Assert.Equal(a.CostBasis, s.CostBasis);
            Assert.Equal(a.UnrealizedPnl, s.UnrealizedPnl);
            Assert.Equal(a.ValuationStatus, s.ValuationStatus);
        }

        Assert.Equal(api.TotalMarketValue, sql.Summary.AccountTotalMarketValue);
        Assert.Equal(api.TotalUnrealizedPnl, sql.Summary.TotalUnrealizedPnl);
        Assert.Equal(api.PositionCount, sql.Summary.PositionCount);
        Assert.Equal(api.UnpricedPositionCount, sql.Summary.UnpricedPositionCount);
        Assert.Equal(api.IsValuationComplete, sql.Summary.IsValuationComplete);
    }

    [Fact]
    public async Task Snapshot_on_2025_03_01_uses_the_version_known_at_that_date()
    {
        var snapshot = await _fixture.CreateClient().GetSnapshotAsync("ACC-002", "2025-03-01");

        var aapl = snapshot.Positions.Single(p => p.Symbol == "AAPL");
        Assert.Equal(50m, aapl.NetQuantity);
        Assert.Equal(170m, aapl.AverageUnitCost); // cost reset after the round trip

        var msft = snapshot.Positions.Single(p => p.Symbol == "MSFT");
        Assert.Equal(50m, msft.NetQuantity);              // 40 - 10 + 20 (v1; correction to 25 not yet known)
        Assert.Equal(406.666667m, msft.AverageUnitCost);  // (40*400 + 20*420) / 60
        Assert.Equal(410m, msft.MarketPrice);             // carried forward from 2025-02-28
        Assert.Equal("2025-02-28", msft.PriceDate);
        Assert.Equal(20_500.00m, msft.MarketValue);
        Assert.Equal(166.67m, msft.UnrealizedPnl);        // 20500.00 - ROUND(50 * 406.666667, 2)

        var nvda = snapshot.Positions.Single(p => p.Symbol == "NVDA");
        Assert.Equal("MissingPrice", nvda.ValuationStatus);
        Assert.Null(nvda.MarketValue);
        Assert.Null(nvda.UnrealizedPnl);

        Assert.Equal(29_800.00m, snapshot.TotalMarketValue);
        Assert.Equal(966.67m, snapshot.TotalUnrealizedPnl);
        Assert.Equal(1, snapshot.UnpricedPositionCount);
        Assert.False(snapshot.IsValuationComplete);
    }

    [Fact]
    public async Task Snapshot_on_2025_03_10_reflects_the_later_correction()
    {
        var msft = (await _fixture.CreateClient().GetSnapshotAsync("ACC-002", "2025-03-10")).Positions.Single(p => p.Symbol == "MSFT");

        Assert.Equal(55m, msft.NetQuantity);
        Assert.Equal(407.692308m, msft.AverageUnitCost); // (40*400 + 25*420) / 65
        Assert.Equal(415m, msft.MarketPrice);
        Assert.Equal(22_825.00m, msft.MarketValue);
        Assert.Equal(401.92m, msft.UnrealizedPnl);
    }

    [Fact]
    public async Task A_correction_that_moves_a_trade_to_another_account_is_not_double_counted()
    {
        // v1 on account A; v2 moves it to account B. Both accounts' snapshots - in SQL and in C# - must count it once.
        var accountA = await _fixture.CreateAccountAsync("MOVE");
        var accountB = await _fixture.CreateAccountAsync("MOVE");
        var client = _fixture.CreateClient();
        var trade = ApiClientExtensions.DemoTrade($"MOVE-{Guid.NewGuid():N}"[..30], accountA);
        await client.SubmitAsync(trade);
        await client.SubmitAsync(trade with { AccountId = accountB, AsOf = "2025-03-01T12:00:00Z" });

        Assert.Empty((await client.GetSnapshotAsync(accountA, "2025-03-01")).Positions);
        Assert.Equal(120m, Assert.Single((await client.GetSnapshotAsync(accountB, "2025-03-01")).Positions).NetQuantity);
        Assert.Empty((await _fixture.ExecuteSnapshotProcedureAsync(accountA, new DateOnly(2025, 3, 1))).Positions);
        Assert.Equal(120m, Assert.Single((await _fixture.ExecuteSnapshotProcedureAsync(accountB, new DateOnly(2025, 3, 1))).Positions).NetQuantity);
    }

    [Fact]
    public async Task Stored_procedure_rejects_unknown_account()
    {
        var ex = await Assert.ThrowsAsync<SqlException>(() => _fixture.ExecuteSnapshotProcedureAsync("NOPE", new DateOnly(2025, 3, 1)));
        Assert.Equal(50002, ex.Number);
    }

    [Fact]
    public async Task Indexes_script_matches_the_migrated_schema()
    {
        const string countIndexes = "SELECT COUNT(*) FROM sys.indexes WHERE object_id IN (OBJECT_ID('dbo.Accounts'), OBJECT_ID('dbo.Instruments'), OBJECT_ID('dbo.Trades'), OBJECT_ID('dbo.TradeVersions'), OBJECT_ID('dbo.InstrumentPrices')) AND name IS NOT NULL";
        var before = await _fixture.ScalarAsync<int>(countIndexes);

        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Scripts", "Indexes.sql"));
        foreach (var batch in TradeIngestion.Infrastructure.Persistence.EmbeddedSqlScripts.SplitBatches(script))
        {
            await _fixture.ExecuteAsync(batch);
        }

        // Every index the script defines already exists (it was a no-op), so the script cannot drift silently.
        Assert.Equal(before, await _fixture.ScalarAsync<int>(countIndexes));
    }
}
