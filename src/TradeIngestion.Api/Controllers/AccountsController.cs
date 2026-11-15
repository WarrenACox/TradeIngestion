using Microsoft.AspNetCore.Mvc;
using TradeIngestion.Application.Portfolio;

namespace TradeIngestion.Api.Controllers;

[ApiController]
[Route("api/accounts")]
public sealed class AccountsController : ControllerBase
{
    private readonly IPortfolioSnapshotService _snapshots;

    public AccountsController(IPortfolioSnapshotService snapshots) => _snapshots = snapshots;

    /// <summary>Point-in-time portfolio snapshot in USD, using average cost.</summary>
    /// <remarks>
    /// Includes trades with TradeDate on or before `date`, using for each trade the latest version known by the end of
    /// that day (UTC). Corrections received later are not visible, so historical snapshots are reproducible.
    /// Market price is the latest close on or before `date` (`priceDate` shows its age). Positions without any price
    /// have `valuationStatus = MissingPrice`, null market value / P&amp;L, are excluded from totals, and set
    /// `isValuationComplete = false`. Values are never guessed.
    ///
    /// Example: `GET /api/accounts/ACC-001/snapshot?date=2025-03-01`
    ///
    ///     {
    ///       "accountId": "ACC-001", "snapshotDate": "2025-03-01", "baseCurrency": "USD",
    ///       "positions": [{
    ///         "isin": "US0378331005", "symbol": "AAPL", "netQuantity": 150.000000, "averageUnitCost": 180.000000,
    ///         "marketPrice": 186.000000, "priceDate": "2025-03-01", "marketValue": 27900.00, "costBasis": 27000.00,
    ///         "unrealizedPnl": 900.00, "valuationStatus": "Valued"
    ///       }],
    ///       "totalMarketValue": 27900.00, "totalUnrealizedPnl": 900.00,
    ///       "positionCount": 1, "unpricedPositionCount": 0, "isValuationComplete": true
    ///     }
    /// </remarks>
    /// <param name="accountId">Account code, e.g. ACC-001.</param>
    /// <param name="date">Snapshot date (yyyy-MM-dd). Defaults to today (UTC). Future dates are rejected.</param>
    /// <param name="cancellationToken">Request cancellation.</param>
    [HttpGet("{accountId}/snapshot")]
    [ProducesResponseType(typeof(PortfolioSnapshot), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSnapshot(string accountId, [FromQuery] DateOnly? date, CancellationToken cancellationToken)
    {
        var result = await _snapshots.GetSnapshotAsync(accountId, date, cancellationToken);
        return result.Status switch
        {
            SnapshotQueryStatus.Found => Ok(result.Snapshot),
            SnapshotQueryStatus.AccountNotFound => Problem(statusCode: StatusCodes.Status404NotFound, title: "Account not found", detail: result.Error),
            SnapshotQueryStatus.InvalidDate => Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid snapshot date", detail: result.Error),
            _ => throw new InvalidOperationException($"Unmapped snapshot status {result.Status}."),
        };
    }
}
