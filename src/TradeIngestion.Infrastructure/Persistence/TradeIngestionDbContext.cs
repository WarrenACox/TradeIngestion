using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TradeIngestion.Domain.Accounts;
using TradeIngestion.Domain.Instruments;
using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Infrastructure.Persistence;

public sealed class TradeIngestionDbContext : DbContext
{
    public TradeIngestionDbContext(DbContextOptions<TradeIngestionDbContext> options)
        : base(options)
    {
    }

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<Instrument> Instruments => Set<Instrument>();

    public DbSet<InstrumentPrice> InstrumentPrices => Set<InstrumentPrice>();

    public DbSet<Trade> Trades => Set<Trade>();

    public DbSet<TradeVersion> TradeVersions => Set<TradeVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dbo");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TradeIngestionDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every DateTime in this model is UTC. Stored as datetime2(7) (no offset needed once normalised) and
        // materialised with DateTimeKind.Utc so domain invariants and JSON serialisation ("Z") are correct.
        configurationBuilder.Properties<DateTime>()
            .HaveConversion<UtcDateTimeConverter>()
            .HaveColumnType("datetime2(7)");
    }

    private sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter()
            : base(
                v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
                v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
        {
        }
    }
}
