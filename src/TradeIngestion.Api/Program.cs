using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using TradeIngestion.Api.ErrorHandling;
using TradeIngestion.Api.Middleware;
using TradeIngestion.Application;
using TradeIngestion.Infrastructure;
using TradeIngestion.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);

builder.Services
    // Validation is explicit (TradeSubmissionValidator); don't add implicit [Required] errors for nullable-annotated DTOs.
    .AddControllers(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
        context.ProblemDetails.Extensions["correlationId"] = CorrelationIdMiddleware.GetCorrelationId(context.HttpContext);
    });
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddHealthChecks().AddDbContextCheck<TradeIngestionDbContext>("database");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Trade Ingestion and Portfolio Snapshot API",
        Version = "v1",
        Description = "Idempotent trade ingestion with versioned corrections, and point-in-time portfolio snapshots (USD, average cost).",
    });
    options.MapType<DateOnly>(() => new OpenApiSchema { Type = "string", Format = "date", Example = new OpenApiString("2025-03-01") });
    var xmlPath = Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml");
    options.IncludeXmlComments(xmlPath);
});

var app = builder.Build();

// Correlation first so that every log line - including the exception handler's - carries the CorrelationId.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.DocumentTitle = "Trade Ingestion API");
}

app.MapControllers();
app.MapHealthChecks("/health");

await InitializeDatabaseAsync(app);

app.Run();

static async Task InitializeDatabaseAsync(WebApplication app)
{
    var options = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
    if (!options.ApplyMigrationsOnStartup && !options.SeedOnStartup)
    {
        return;
    }

    await using var scope = app.Services.CreateAsyncScope();
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    if (options.ApplyMigrationsOnStartup)
    {
        await initializer.MigrateAsync(app.Lifetime.ApplicationStopping);
    }

    if (options.SeedOnStartup)
    {
        await initializer.SeedAsync(app.Lifetime.ApplicationStopping);
    }
}

/// <summary>Entry point; partial so integration tests can use WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
