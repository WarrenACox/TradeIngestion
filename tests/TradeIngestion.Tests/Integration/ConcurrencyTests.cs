using System.Net;
using TradeIngestion.Tests.Integration.Infrastructure;

namespace TradeIngestion.Tests.Integration;

/// <summary>Idempotency and correction ordering under genuinely concurrent requests against SQL Server.</summary>
public sealed class ConcurrencyTests : IClassFixture<SqlServerApiFixture>
{
    private readonly SqlServerApiFixture _fixture;

    public ConcurrencyTests(SqlServerApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Concurrent_identical_first_submissions_create_exactly_one_trade()
    {
        var account = await _fixture.CreateAccountAsync("CONC");
        var trade = ApiClientExtensions.DemoTrade($"RACE-{Guid.NewGuid():N}"[..30], account);
        var client = _fixture.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => client.SubmitAsync(trade)));
        var outcomes = await Task.WhenAll(responses.Select(r => r.ReadAsync<TradeResponse>()));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, $"Unexpected {r.StatusCode}"));
        Assert.Single(outcomes, o => o.Outcome == "Created");
        Assert.Equal(24, outcomes.Count(o => o.Outcome == "DuplicateIgnored"));
        Assert.Single(outcomes.Select(o => o.TradeId).Distinct());
        Assert.Equal(1, await _fixture.CountTradesAsync(trade.ExternalRef));
        Assert.Equal(1, await _fixture.CountVersionsAsync(trade.ExternalRef));

        var snapshot = await client.GetSnapshotAsync(account, "2025-03-01");
        Assert.Equal(120m, Assert.Single(snapshot.Positions).NetQuantity);
    }

    [Fact]
    public async Task Concurrent_corrections_converge_on_the_latest_asof_with_a_gapless_history()
    {
        var account = await _fixture.CreateAccountAsync("CONC");
        var original = ApiClientExtensions.DemoTrade($"CORR-{Guid.NewGuid():N}"[..30], account);
        var client = _fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await client.SubmitAsync(original)).StatusCode);

        // 10 corrections with increasing AsOf, fired at once in reverse order to maximise out-of-order arrival.
        var corrections = Enumerable.Range(1, 10)
            .Reverse()
            .Select(i => original with { Quantity = 100m + i, AsOf = $"2025-03-01T{10 + i:00}:30:00Z" })
            .ToList();

        var responses = await Task.WhenAll(corrections.Select(c => client.SubmitAsync(c)));
        var outcomes = await Task.WhenAll(responses.Select(r => r.ReadAsync<TradeResponse>()));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.All(outcomes, o => Assert.Contains(o.Outcome, new[] { "CorrectionApplied", "StaleIgnored" }));
        var applied = outcomes.Count(o => o.Outcome == "CorrectionApplied");

        var history = await (await client.GetAsync($"/api/trades/{original.ExternalRef}")).ReadAsync<HistoryResponse>();
        Assert.Equal(applied + 1, history.Versions.Count);
        Assert.Equal(Enumerable.Range(1, history.Versions.Count), history.Versions.Select(v => v.VersionNumber)); // gapless
        Assert.True(history.Versions.Zip(history.Versions.Skip(1)).All(p => p.First.AsOfUtc < p.Second.AsOfUtc), "AsOf must increase with version");

        // Whatever the arrival order, the latest AsOf (20:30Z, quantity 110) must win.
        var current = history.Versions.Single(v => v.IsCurrent);
        Assert.Equal(110m, current.Quantity);
        Assert.Equal(new DateTime(2025, 3, 1, 20, 30, 0, DateTimeKind.Utc), current.AsOfUtc);

        var snapshot = await client.GetSnapshotAsync(account, "2025-03-01");
        Assert.Equal(110m, Assert.Single(snapshot.Positions).NetQuantity);
    }
}
