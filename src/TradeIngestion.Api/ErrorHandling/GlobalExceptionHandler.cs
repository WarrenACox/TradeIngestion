using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TradeIngestion.Application.Common;

namespace TradeIngestion.Api.ErrorHandling;

/// <summary>
/// Last-resort handler: every unhandled exception becomes an RFC 7807 ProblemDetails response with the
/// correlation id, and is logged once with full detail. Internal details are never written to the response.
/// Expected business outcomes never reach here - they are returned as results, not thrown.
/// </summary>
internal sealed partial class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogRequestAborted(_logger, httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.StatusCode = 499; // client closed request; nobody is listening for a body
            return true;
        }

        var problem = exception switch
        {
            PersistenceConflictException => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Concurrent modification",
                Detail = "The trade was being modified concurrently and could not be processed after retries. The request is idempotent and can be safely retried.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            },
            BadHttpRequestException badRequest => new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = "Bad request",
                Detail = "The request could not be read.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Unexpected processing failure",
                Detail = "The request could not be processed. Quote the correlationId when reporting the issue.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            },
        };

        if (problem.Status >= 500)
        {
            LogUnhandled(_logger, exception, httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            LogHandled(_logger, exception, problem.Status ?? 0, httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    [LoggerMessage(EventId = 9000, Level = LogLevel.Error, Message = "Unhandled exception processing {HttpMethod} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string httpMethod, string path);

    [LoggerMessage(EventId = 9001, Level = LogLevel.Warning, Message = "Request {HttpMethod} {Path} failed with {StatusCode}")]
    private static partial void LogHandled(ILogger logger, Exception exception, int statusCode, string httpMethod, string path);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information, Message = "Request {HttpMethod} {Path} was cancelled by the client")]
    private static partial void LogRequestAborted(ILogger logger, string httpMethod, string path);
}
