using TradeIngestion.Application.Trades;
using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Tests.Unit;

public sealed class TradeSubmissionValidatorTests
{
    private static SubmitTradeCommand Valid() => new(
        " TRX-1001 ", "ACC-001", "us0378331005", "aapl", "buy", 120m, 185.40m,
        new DateOnly(2025, 3, 1), new DateTimeOffset(2025, 3, 1, 12, 15, 0, TimeSpan.FromHours(2)), "corr-1");

    [Fact]
    public void Valid_submission_is_normalised()
    {
        var (value, errors) = TradeSubmissionValidator.Validate(Valid());

        Assert.Empty(errors);
        Assert.NotNull(value);
        Assert.Equal("TRX-1001", value.ExternalRef);
        Assert.Equal("US0378331005", value.Isin);
        Assert.Equal("AAPL", value.Symbol);
        Assert.Equal(TradeSide.Buy, value.Side);
        Assert.Equal(new DateTime(2025, 3, 1, 10, 15, 0, DateTimeKind.Utc), value.AsOfUtc); // offset normalised
        Assert.Equal(DateTimeKind.Utc, value.AsOfUtc.Kind);
    }

    [Fact]
    public void All_missing_fields_are_reported_together()
    {
        var (value, errors) = TradeSubmissionValidator.Validate(new SubmitTradeCommand(null, null, null, null, null, null, null, null, null, null));

        Assert.Null(value);
        Assert.Equal(
            new[] { "accountId", "asOf", "externalRef", "isin", "price", "quantity", "side", "symbol", "tradeDate" },
            errors.Keys.Order().ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1.0000001)]
    public void Quantity_must_be_positive_with_at_most_six_decimals(double quantity)
    {
        var (_, errors) = TradeSubmissionValidator.Validate(Valid() with { Quantity = (decimal)quantity });

        Assert.Contains("quantity", errors.Keys);
    }

    [Fact]
    public void Unknown_side_is_rejected()
    {
        var (_, errors) = TradeSubmissionValidator.Validate(Valid() with { Side = "HOLD" });

        Assert.Contains("side", errors.Keys);
    }

    [Theory]
    [InlineData("US0378331005", true)]  // Apple
    [InlineData("US5949181045", true)]  // Microsoft
    [InlineData("US67066G1040", true)]  // NVIDIA (letter in the NSIN)
    [InlineData("US0378331006", false)] // wrong check digit
    [InlineData("US037833100", false)]  // too short
    [InlineData("1S0378331005", false)] // country code must be letters
    public void Isin_check_digit_is_validated(string isin, bool expected)
    {
        Assert.Equal(expected, IsinValidator.IsValid(isin));
    }
}
