using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace TradeIngestion.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Apply pending EF Core migrations at startup. Convenient locally; in production run migrations from the pipeline.</summary>
    public bool ApplyMigrationsOnStartup { get; set; }

    /// <summary>Run Database/Scripts/SeedData.sql at startup (idempotent).</summary>
    public bool SeedOnStartup { get; set; }
}

public sealed partial class DatabaseInitializer
{
    private readonly TradeIngestionDbContext _db;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(TradeIngestionDbContext db, ILogger<DatabaseInitializer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        var pending = (await _db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        LogApplyingMigrations(_logger, pending.Count);
        await _db.Database.MigrateAsync(cancellationToken);
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var batch in EmbeddedSqlScripts.SplitBatches(EmbeddedSqlScripts.Read(EmbeddedSqlScripts.SeedData)))
        {
            await _db.Database.ExecuteSqlRawAsync(batch, cancellationToken);
        }

        LogSeeded(_logger);
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Applying {PendingMigrationCount} pending database migration(s)")]
    private static partial void LogApplyingMigrations(ILogger logger, int pendingMigrationCount);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "Seed data script applied")]
    private static partial void LogSeeded(ILogger logger);
}
