using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TradeIngestion.Api.Contracts;
using TradeIngestion.Api.Middleware;
using TradeIngestion.Application.Trades;

namespace TradeIngestion.Api.Controllers;

[ApiController]
[Route("api/trades")]
public sealed class TradesController : ControllerBase
{
    private readonly ITradeIngestionService _ingestion;
    private readonly ITradeHistoryReader _history;

    public TradesController(ITradeIngestionService ingestion, ITradeHistoryReader history)
    {
        _ingestion = ingestion;
        _history = history;
    }

    /// <summary>Submit a trade (new, duplicate re-send, or correction). Idempotent on ExternalRef.</summary>
    /// <remarks>
    /// Outcomes:
    ///
    /// | Status | Outcome | Meaning |
    /// |---|---|---|
    /// | 201 | `Created` | First time this ExternalRef was seen. `Location` points at the trade history. |
    /// | 200 | `DuplicateIgnored` | Same ExternalRef + AsOf + content (or a later AsOf with no economic change). Nothing applied. |
    /// | 200 | `CorrectionApplied` | Later AsOf with changed content. New version effective; previous versions retained. |
    /// | 200 | `StaleIgnored` | AsOf older than the latest accepted version. Ignored so newer knowledge is not overwritten. |
    /// | 400 | validation | Malformed or missing fields (ValidationProblemDetails). |
    /// | 409 | `ConflictingPayload` | Same AsOf as the current version but different content - the source contradicts itself. |
    /// | 409 | `ExternalRefAlreadyExists` | Versioned corrections are disabled (feature flag) and the ExternalRef exists. |
    /// | 422 | reference data | Unknown account/instrument, or the symbol does not match the ISIN. |
    /// | 500 | unexpected | ProblemDetails with `correlationId`. |
    ///
    /// Example correction request (same ExternalRef, later AsOf):
    ///
    ///     {
    ///       "externalRef": "TRX-1001", "accountId": "ACC-001", "isin": "US0378331005", "symbol": "AAPL",
    ///       "side": "BUY", "quantity": 150, "price": 180.00, "tradeDate": "2025-03-01", "asOf": "2025-03-01T11:30:00Z"
    ///     }
    ///
    /// Example response (200):
    ///
    ///     {
    ///       "externalRef": "TRX-1001", "outcome": "CorrectionApplied", "tradeId": 1, "effectiveVersion": 2,
    ///       "latestAsOf": "2025-03-01T11:30:00Z",
    ///       "message": "Correction applied; version 2 is now effective. Prior versions retained."
    ///     }
    /// </remarks>
    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(TradeSubmissionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(TradeSubmissionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Submit([FromBody] SubmitTradeRequest request, CancellationToken cancellationToken)
    {
        var result = await _ingestion.SubmitAsync(request.ToCommand(CorrelationIdMiddleware.GetCorrelationId(HttpContext)), cancellationToken);

        return result.Outcome switch
        {
            TradeSubmissionOutcome.Created =>
                CreatedAtAction(nameof(GetHistory), new { externalRef = result.ExternalRef }, TradeSubmissionResponse.From(result)),

            TradeSubmissionOutcome.DuplicateIgnored or TradeSubmissionOutcome.CorrectionApplied or TradeSubmissionOutcome.StaleIgnored =>
                Ok(TradeSubmissionResponse.From(result)),

            TradeSubmissionOutcome.ValidationFailed =>
                ValidationProblem(title: "Trade submission is invalid.", modelStateDictionary: ToModelState(result.Errors)),

            TradeSubmissionOutcome.ReferenceDataRejected =>
                ValidationProblem(
                    title: "Trade references unknown or inconsistent reference data.",
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    modelStateDictionary: ToModelState(result.Errors)),

            TradeSubmissionOutcome.ConflictingPayload or TradeSubmissionOutcome.ExternalRefAlreadyExists =>
                ConflictProblem(result),

            _ => throw new InvalidOperationException($"Unmapped trade submission outcome {result.Outcome}."),
        };
    }

    /// <summary>Trade with its full, append-only version history (audit view).</summary>
    [HttpGet("{externalRef}")]
    [ProducesResponseType(typeof(TradeHistoryView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHistory(string externalRef, CancellationToken cancellationToken)
    {
        var history = await _history.GetHistoryAsync(externalRef, cancellationToken);
        return history is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Trade not found", detail: $"No trade with ExternalRef '{externalRef}'.")
            : Ok(history);
    }

    private ObjectResult ConflictProblem(TradeSubmissionResult result)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext,
            statusCode: StatusCodes.Status409Conflict,
            title: result.Outcome == TradeSubmissionOutcome.ConflictingPayload ? "Conflicting trade payload" : "Trade already exists",
            type: "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            detail: result.Message);
        problem.Extensions["outcome"] = result.Outcome.ToString();
        problem.Extensions["externalRef"] = result.ExternalRef;
        problem.Extensions["effectiveVersion"] = result.EffectiveVersion;

        return new ObjectResult(problem)
        {
            StatusCode = StatusCodes.Status409Conflict,
            ContentTypes = { "application/problem+json" },
        };
    }

    private static ModelStateDictionary ToModelState(IReadOnlyDictionary<string, string[]> errors)
    {
        var modelState = new ModelStateDictionary();
        foreach (var (field, messages) in errors)
        {
            foreach (var message in messages)
            {
                modelState.AddModelError(field, message);
            }
        }

        return modelState;
    }
}
