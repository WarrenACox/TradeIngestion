using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeIngestion.Domain.Accounts;
using TradeIngestion.Domain.Instruments;
using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Infrastructure.Persistence.Configurations;

internal sealed class TradeConfiguration : IEntityTypeConfiguration<Trade>
{
    public void Configure(EntityTypeBuilder<Trade> builder)
    {
        builder.ToTable("Trades", t =>
            t.HasCheckConstraint("CK_Trades_CurrentVersionNumber_Positive", "[CurrentVersionNumber] >= 1"));
        builder.HasKey(t => t.Id).HasName("PK_Trades");
        builder.Property(t => t.Id).UseIdentityColumn();

        // varchar, not nvarchar: upstream references are ASCII, and the parameter type must match the column
        // exactly for the idempotency lookup to be an index seek (see TradeRepository).
        builder.Property(t => t.ExternalRef).HasMaxLength(64).IsUnicode(false).IsRequired();
        builder.Property(t => t.CurrentVersionNumber).IsRequired();
        builder.Property(t => t.LatestAsOfUtc).IsRequired();
        builder.Property(t => t.CreatedAtUtc).IsRequired();
        builder.Property(t => t.UpdatedAtUtc).IsRequired();
        builder.Property(t => t.RowVersion).IsRowVersion();

        builder.Ignore(t => t.CurrentVersion);

        builder.HasMany(t => t.Versions)
            .WithOne()
            .HasForeignKey(v => v.TradeId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TradeVersions_Trades");
        builder.Navigation(t => t.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The idempotency key. The database, not the application, is the final arbiter of uniqueness.
        builder.HasIndex(t => t.ExternalRef).IsUnique().HasDatabaseName("UX_Trades_ExternalRef");
    }
}

internal sealed class TradeVersionConfiguration : IEntityTypeConfiguration<TradeVersion>
{
    public void Configure(EntityTypeBuilder<TradeVersion> builder)
    {
        builder.ToTable("TradeVersions", t =>
        {
            t.HasCheckConstraint("CK_TradeVersions_Side", "[Side] IN ('BUY', 'SELL')");
            t.HasCheckConstraint("CK_TradeVersions_Quantity_Positive", "[Quantity] > 0");
            t.HasCheckConstraint("CK_TradeVersions_Price_Positive", "[Price] > 0");
            t.HasCheckConstraint("CK_TradeVersions_VersionNumber_Positive", "[VersionNumber] >= 1");
        });
        builder.HasKey(v => v.Id).HasName("PK_TradeVersions");
        builder.Property(v => v.Id).UseIdentityColumn();

        builder.Property(v => v.Side)
            .HasConversion(side => side == TradeSide.Buy ? "BUY" : "SELL", value => value == "BUY" ? TradeSide.Buy : TradeSide.Sell)
            .HasMaxLength(4)
            .IsUnicode(false)
            .IsRequired();
        builder.Property(v => v.Quantity).HasPrecision(18, 6);
        builder.Property(v => v.Price).HasPrecision(19, 6);
        builder.Property(v => v.TradeDate).HasColumnType("date");
        builder.Property(v => v.AsOfUtc).IsRequired();
        builder.Property(v => v.ReceivedAtUtc).IsRequired();
        builder.Property(v => v.CorrelationId).HasMaxLength(64).IsUnicode(false);

        builder.Ignore(v => v.Details);

        builder.HasOne<Account>()
            .WithMany()
            .HasForeignKey(v => v.AccountId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TradeVersions_Accounts");

        builder.HasOne<Instrument>()
            .WithMany()
            .HasForeignKey(v => v.InstrumentId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_TradeVersions_Instruments");

        // Uniqueness: one row per version number, and one version per AsOf, per trade.
        // The first also covers the snapshot's version-ranking access path (see Database/Scripts/Indexes.sql).
        builder.HasIndex(v => new { v.TradeId, v.VersionNumber })
            .IsUnique()
            .IncludeProperties(v => new { v.AsOfUtc, v.AccountId, v.InstrumentId, v.Side, v.Quantity, v.Price, v.TradeDate })
            .HasDatabaseName("UX_TradeVersions_Trade_Version");

        builder.HasIndex(v => new { v.TradeId, v.AsOfUtc })
            .IsUnique()
            .HasDatabaseName("UX_TradeVersions_Trade_AsOf");

        builder.HasIndex(v => new { v.AccountId, v.AsOfUtc })
            .IncludeProperties(v => v.TradeId)
            .HasDatabaseName("IX_TradeVersions_Account_AsOf");

        builder.HasIndex(v => v.InstrumentId).HasDatabaseName("IX_TradeVersions_InstrumentId");
    }
}
