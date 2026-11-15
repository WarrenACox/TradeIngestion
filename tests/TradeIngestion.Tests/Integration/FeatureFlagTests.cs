using System.Net;
using System.Text.Json;
using TradeIngestion.Tests.Integration.Infrastructure;

namespace TradeIngestion.Tests.Integration;

/// <summary>TradeProcessing:EnableVersionedCorrections = false -> legacy insert-only behaviour, still never double counts.</summary>
public sealed class FeatureFlagTests : IClassFixture<LegacyModeApiFixture>
{
    private readonly LegacyModeApiFixture _fixture;

    public FeatureFlagTests(LegacyModeApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task With_flag_off_resubmissions_are_rejected_and_never_applied()
    {
        var account = await _fixture.CreateAccountAsync("LEGACY");
        var client = _fixture.CreateClient();
        var trade = ApiClientExtensions.DemoTrade($"LEG-{Guid.NewGuid():N}"[..30], account);

        Assert.Equal(HttpStatusCode.Created, (await client.SubmitAsync(trade)).StatusCode);

        var duplicate = await client.SubmitAsync(trade);
        var correction = await client.SubmitAsync(trade with { Quantity = 150m, AsOf = "2025-03-01T11:30:00Z" });

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, correction.StatusCode);
        Assert.Equal("ExternalRefAlreadyExists", (await correction.ReadAsync<JsonElement>()).GetProperty("outcome").GetString());
        Assert.Equal(1, await _fixture.CountVersionsAsync(trade.ExternalRef));
        Assert.Equal(120m, Assert.Single((await client.GetSnapshotAsync(account, "2025-03-01")).Positions).NetQuantity);
    }
}
