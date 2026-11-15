using System.Net.Http.Json;
using System.Text.Json;

namespace TradeIngestion.Tests.Integration.Infrastructure;

// Client-side view of the API contract. Deliberately separate from the server types so the tests verify the
// wire format, not just the C# model.

public sealed record TradeRequest(
    string ExternalRef,
    string AccountId,
    string Isin,
    string Symbol,
    string Side,
    decimal Quantity,
    decimal Price,
    string TradeDate,
    string AsOf);

public sealed record TradeResponse(string ExternalRef, string Outcome, long? TradeId, int? EffectiveVersion, DateTime? LatestAsOf, string Message);

public sealed record SnapshotPosition(
    string Isin,
    string Symbol,
    decimal NetQuantity,
    decimal? AverageUnitCost,
    decimal? MarketPrice,
    string? PriceDate,
    decimal? MarketValue,
    decimal? CostBasis,
    decimal? UnrealizedPnl,
    string ValuationStatus);

public sealed record SnapshotResponse(
    string AccountId,
    string SnapshotDate,
    string BaseCurrency,
    IReadOnlyList<SnapshotPosition> Positions,
    decimal TotalMarketValue,
    decimal TotalUnrealizedPnl,
    int PositionCount,
    int UnpricedPositionCount,
    bool IsValuationComplete);

public sealed record VersionResponse(int VersionNumber, string AccountId, string Side, decimal Quantity, decimal Price, string TradeDate, DateTime AsOfUtc, bool IsCurrent);

public sealed record HistoryResponse(long TradeId, string ExternalRef, int CurrentVersion, DateTime LatestAsOfUtc, IReadOnlyList<VersionResponse> Versions);

public static class ApiClientExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Task<HttpResponseMessage> SubmitAsync(this HttpClient client, TradeRequest request) =>
        client.PostAsJsonAsync("/api/trades", request, Json);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    public static async Task<SnapshotResponse> GetSnapshotAsync(this HttpClient client, string accountId, string date)
    {
        var response = await client.GetAsync($"/api/accounts/{accountId}/snapshot?date={date}");
        response.EnsureSuccessStatusCode();
        return await response.ReadAsync<SnapshotResponse>();
    }

    public static TradeRequest DemoTrade(string externalRef = "TRX-1001", string accountId = "ACC-001") =>
        new(externalRef, accountId, "US0378331005", "AAPL", "BUY", 120m, 185.40m, "2025-03-01", "2025-03-01T10:15:00Z");
}
