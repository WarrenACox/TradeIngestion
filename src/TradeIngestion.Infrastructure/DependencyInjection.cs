using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeIngestion.Application.Common;
using TradeIngestion.Application.Portfolio;
using TradeIngestion.Application.Trades;
using TradeIngestion.Infrastructure.Persistence;

namespace TradeIngestion.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "TradeIngestion";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. Set ConnectionStrings__{ConnectionStringName} or use user-secrets.");
        }

        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        services.AddDbContext<TradeIngestionDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "dbo");
                // Retries transient faults, including deadlock victims (1205). Safe because every unit of work is
                // re-evaluated from scratch and ingestion is idempotent.
                sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(2), errorNumbersToAdd: null);
                sql.CommandTimeout(30);
            }));

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<ITradeRepository, TradeRepository>();
        services.AddScoped<IReferenceDataReader, ReferenceDataReader>();
        services.AddScoped<ITradeHistoryReader, TradeHistoryReader>();
        services.AddScoped<IPortfolioReadStore, PortfolioReadStore>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }
}
