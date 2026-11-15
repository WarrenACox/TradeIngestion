using System.Text.RegularExpressions;

namespace TradeIngestion.Api.Middleware;

/// <summary>
/// Accepts an inbound X-Correlation-ID (validated to prevent log injection) or generates one, echoes it on the
/// response, stores it on every trade version written, and puts it in the logging scope of the whole request.
/// </summary>
public sealed partial class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    private const string ItemKey = "CorrelationId";

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var inbound = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = inbound is not null && SafeId().IsMatch(inbound) ? inbound : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        // Message-template scope: rendered as "CorrelationId:..." by the simple formatter and as a structured
        // { "CorrelationId": "..." } property by the JSON formatter.
        using (_logger.BeginScope("CorrelationId:{CorrelationId}", correlationId))
        {
            await _next(context);
        }
    }

    public static string GetCorrelationId(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;

    [GeneratedRegex(@"^[A-Za-z0-9\-_.]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
