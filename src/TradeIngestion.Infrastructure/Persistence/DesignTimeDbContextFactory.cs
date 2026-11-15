using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TradeIngestion.Infrastructure.Persistence;

/// <summary>
/// Used only by the dotnet-ef tooling. "migrations add" never connects; "database update" uses
/// the TRADEINGESTION_CONNECTION environment variable (falls back to the docker-compose SQL Server).
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TradeIngestionDbContext>
{
    public TradeIngestionDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("TRADEINGESTION_CONNECTION")
            ?? "Server=localhost,1433;Database=TradeIngestion;User Id=sa;Password=TradeIngestion!Dev2025;TrustServerCertificate=True;Encrypt=True";

        var options = new DbContextOptionsBuilder<TradeIngestionDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options;
        return new TradeIngestionDbContext(options);
    }
}
