using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TradeIngestion.Tests.Integration.Infrastructure;

namespace TradeIngestion.Tests.Integration;

/// <summary>HTTP contract: status codes, ProblemDetails shape, correlation ids, stale and conflicting events.</summary>
public sealed class ApiBehaviourTests : IClassFixture<SqlServerApiFixture>
{
    private readonly SqlServerApiFixture _fixture;

    public ApiBehaviourTests(SqlServerApiFixture fixture) => _fixture = fixture;

    private static string NewRef(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..30];

    [Fact]
    public async Task Stale_event_is_ignored_and_does_not_change_the_effective_version()
    {
        var account = await _fixture.CreateAccountAsync("API");
        var client = _fixture.CreateClient();
        var trade = ApiClientExtensions.DemoTrade(NewRef("STALE"), account);
        await client.SubmitAsync(trade);
        await client.SubmitAsync(trade with { Quantity = 150m, AsOf = "2025-03-01T11:30:00Z" });

        var stale = await client.SubmitAsync(trade with { Quantity = 999m, AsOf = "2025-03-01T11:00:00Z" });

        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        var body = await stale.ReadAsync<TradeResponse>();
        Assert.Equal("StaleIgnored", body.Outcome);
        Assert.Equal(2, body.EffectiveVersion);
        Assert.Equal(2, await _fixture.CountVersionsAsync(trade.ExternalRef));
        Assert.Equal(150m, Assert.Single((await client.GetSnapshotAsync(account, "2025-03-01")).Positions).NetQuantity);
    }

    [Fact]
    public async Task Same_asof_with_different_content_is_a_409_conflict()
    {
        var client = _fixture.CreateClient();
        var trade = ApiClientExtensions.DemoTrade(NewRef("CONFLICT"), await _fixture.CreateAccountAsync("API"));
        await client.SubmitAsync(trade);

        var response = await client.SubmitAsync(trade with { Quantity = 121m });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.ReadAsync<JsonElement>();
        Assert.Equal("ConflictingPayload", problem.GetProperty("outcome").GetString());
        Assert.Equal(1, await _fixture.CountVersionsAsync(trade.ExternalRef));
    }

    [Fact]
    public async Task Timestamp_without_offset_is_rejected_with_validation_problem()
    {
        var client = _fixture.CreateClient();
        var trade = ApiClientExtensions.DemoTrade(NewRef("TZ")) with { AsOf = "2025-03-01T10:15:00" };

        var response = await client.SubmitAsync(trade);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.ReadAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("$.asOf", out _));
    }

    [Fact]
    public async Task Invalid_fields_return_400_with_every_error()
    {
        var client = _fixture.CreateClient();
        var trade = ApiClientExtensions.DemoTrade(NewRef("BAD")) with { Isin = "US0378331006", Side = "HOLD", Quantity = -1m };

        var response = await client.SubmitAsync(trade);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.ReadAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("isin", out _));
        Assert.True(errors.TryGetProperty("side", out _));
        Assert.True(errors.TryGetProperty("quantity", out _));
    }

    [Fact]
    public async Task Unknown_reference_data_returns_422()
    {
        var client = _fixture.CreateClient();
        var trade = ApiClientExtensions.DemoTrade(NewRef("REF"), "ACC-404") with { Symbol = "MSFT" };

        var response = await client.SubmitAsync(trade);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var errors = (await response.ReadAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("accountId", out _));
        Assert.True(errors.TryGetProperty("symbol", out _));
    }

    [Fact]
    public async Task Malformed_json_returns_400_problem_details()
    {
        var client = _fixture.CreateClient();

        var response = await client.PostAsync("/api/trades", new StringContent("{ not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_stored_on_the_version_and_returned_in_problems()
    {
        var client = _fixture.CreateClient();
        var trade = ApiClientExtensions.DemoTrade(NewRef("CORRID"), await _fixture.CreateAccountAsync("API"));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/trades") { Content = JsonContent.Create(trade) };
        request.Headers.Add("X-Correlation-ID", "test-corr-123");

        var response = await client.SendAsync(request);

        Assert.Equal("test-corr-123", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal("test-corr-123", await _fixture.ScalarAsync<string>(
            "SELECT v.CorrelationId FROM dbo.TradeVersions v JOIN dbo.Trades t ON t.Id = v.TradeId WHERE t.ExternalRef = @r",
            new Microsoft.Data.SqlClient.SqlParameter("@r", trade.ExternalRef)));

        var notFound = await client.GetAsync("/api/accounts/NOPE/snapshot?date=2025-03-01");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.True((await notFound.ReadAsync<JsonElement>()).TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task Future_snapshot_date_is_rejected()
    {
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        var response = await _fixture.CreateClient().GetAsync($"/api/accounts/ACC-001/snapshot?date={future}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_trade_history_returns_404()
    {
        var response = await _fixture.CreateClient().GetAsync("/api/trades/DOES-NOT-EXIST");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
