using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradeIngestion.Application.Portfolio;
using TradeIngestion.Application.Trades;

namespace TradeIngestion.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TradeProcessingOptions>()
            .Bind(configuration.GetSection(TradeProcessingOptions.SectionName))
            .Validate(o => o.MaxConflictRetries is >= 1 and <= 10, "TradeProcessing:MaxConflictRetries must be between 1 and 10.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ITradeIngestionService, TradeIngestionService>();
        services.AddScoped<IPortfolioSnapshotService, PortfolioSnapshotService>();
        return services;
    }
}
