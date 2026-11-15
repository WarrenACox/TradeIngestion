using System.Text.RegularExpressions;
using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Application.Trades;

/// <summary>A submission that passed structural validation, normalised (trimmed, upper-cased, UTC).</summary>
public sealed record ValidatedTradeSubmission(
    string ExternalRef,
    string AccountCode,
    string Isin,
    string Symbol,
    TradeSide Side,
    decimal Quantity,
    decimal Price,
    DateOnly TradeDate,
    DateTime AsOfUtc);

/// <summary>
/// Structural validation of a trade submission (no database access). Error keys are the camelCase JSON field names
/// so they line up with ASP.NET Core ValidationProblemDetails.
/// </summary>
public static partial class TradeSubmissionValidator
{
    public const int ExternalRefMaxLength = 64;
    public const int AccountCodeMaxLength = 32;
    public const int SymbolMaxLength = 16;

    // Column limits: Quantity decimal(18,6), Price decimal(19,6). Values outside these would be silently rounded
    // or overflow in SQL Server, so they are rejected here instead.
    public const int MaxDecimalPlaces = 6;
    public const decimal MaxQuantity = 999_999_999_999m;
    public const decimal MaxPrice = 9_999_999_999_999m;

    public static (ValidatedTradeSubmission? Value, IReadOnlyDictionary<string, string[]> Errors) Validate(SubmitTradeCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var list))
            {
                errors[field] = list = new List<string>();
            }

            list.Add(message);
        }

        var externalRef = command.ExternalRef?.Trim();
        if (string.IsNullOrEmpty(externalRef))
        {
            Add("externalRef", "ExternalRef is required.");
        }
        else if (externalRef.Length > ExternalRefMaxLength || !IdentifierPattern().IsMatch(externalRef))
        {
            Add("externalRef", $"ExternalRef must be 1-{ExternalRefMaxLength} characters of letters, digits, '-', '_', '.' or '/'.");
        }

        var accountCode = command.AccountId?.Trim();
        if (string.IsNullOrEmpty(accountCode))
        {
            Add("accountId", "AccountId is required.");
        }
        else if (accountCode.Length > AccountCodeMaxLength || !IdentifierPattern().IsMatch(accountCode))
        {
            Add("accountId", $"AccountId must be 1-{AccountCodeMaxLength} characters of letters, digits, '-', '_', '.' or '/'.");
        }

        var isin = command.Isin?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(isin))
        {
            Add("isin", "ISIN is required.");
        }
        else if (!IsinValidator.IsValid(isin))
        {
            Add("isin", "ISIN must be a valid 12-character ISO 6166 identifier with a correct check digit.");
        }

        var symbol = command.Symbol?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(symbol))
        {
            Add("symbol", "Symbol is required.");
        }
        else if (symbol.Length > SymbolMaxLength)
        {
            Add("symbol", $"Symbol must be at most {SymbolMaxLength} characters.");
        }

        TradeSide? side = command.Side?.Trim().ToUpperInvariant() switch
        {
            "BUY" => TradeSide.Buy,
            "SELL" => TradeSide.Sell,
            _ => null,
        };
        if (side is null)
        {
            Add("side", "Side is required and must be 'BUY' or 'SELL'.");
        }

        ValidatePositiveDecimal(command.Quantity, "quantity", "Quantity", MaxQuantity, Add);
        ValidatePositiveDecimal(command.Price, "price", "Price", MaxPrice, Add);

        if (command.TradeDate is null)
        {
            Add("tradeDate", "TradeDate is required (yyyy-MM-dd).");
        }

        if (command.AsOf is null)
        {
            Add("asOf", "AsOf is required (ISO 8601 with an explicit offset, e.g. 2025-03-01T10:15:00Z).");
        }

        if (errors.Count > 0)
        {
            return (null, errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
        }

        var value = new ValidatedTradeSubmission(
            externalRef!,
            accountCode!,
            isin!,
            symbol!,
            side!.Value,
            command.Quantity!.Value,
            command.Price!.Value,
            command.TradeDate!.Value,
            DateTime.SpecifyKind(command.AsOf!.Value.UtcDateTime, DateTimeKind.Utc));
        return (value, new Dictionary<string, string[]>());
    }

    private static void ValidatePositiveDecimal(decimal? value, string field, string label, decimal max, Action<string, string> add)
    {
        if (value is null)
        {
            add(field, $"{label} is required.");
            return;
        }

        if (value <= 0)
        {
            add(field, $"{label} must be greater than zero.");
        }
        else if (value > max)
        {
            add(field, $"{label} must not exceed {max}.");
        }

        if (decimal.Round(value.Value, MaxDecimalPlaces) != value.Value)
        {
            add(field, $"{label} supports at most {MaxDecimalPlaces} decimal places.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_./]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
