using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradeIngestion.Domain.Accounts;
using TradeIngestion.Domain.Instruments;

namespace TradeIngestion.Infrastructure.Persistence.Configurations;

internal sealed class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("Accounts");
        builder.HasKey(a => a.Id).HasName("PK_Accounts");
        builder.Property(a => a.Id).UseIdentityColumn();

        builder.Property(a => a.AccountCode).HasMaxLength(32).IsUnicode(false).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(200).IsRequired();
        builder.Property(a => a.BaseCurrency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();

        builder.HasIndex(a => a.AccountCode).IsUnique().HasDatabaseName("UX_Accounts_AccountCode");
    }
}

internal sealed class InstrumentConfiguration : IEntityTypeConfiguration<Instrument>
{
    public void Configure(EntityTypeBuilder<Instrument> builder)
    {
        builder.ToTable("Instruments");
        builder.HasKey(i => i.Id).HasName("PK_Instruments");
        builder.Property(i => i.Id).UseIdentityColumn();

        builder.Property(i => i.Isin).HasMaxLength(12).IsFixedLength().IsUnicode(false).IsRequired();
        builder.Property(i => i.Symbol).HasMaxLength(16).IsUnicode(false).IsRequired();
        builder.Property(i => i.Name).HasMaxLength(200).IsRequired();
        builder.Property(i => i.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();

        builder.HasIndex(i => i.Isin).IsUnique().HasDatabaseName("UX_Instruments_Isin");
    }
}

internal sealed class InstrumentPriceConfiguration : IEntityTypeConfiguration<InstrumentPrice>
{
    public void Configure(EntityTypeBuilder<InstrumentPrice> builder)
    {
        builder.ToTable("InstrumentPrices", t =>
        {
            t.HasCheckConstraint("CK_InstrumentPrices_ClosePrice_Positive", "[ClosePrice] > 0");
            // Base currency only until an FX rate source exists; prevents silently mixing currencies in totals.
            t.HasCheckConstraint("CK_InstrumentPrices_Currency_USD", "[Currency] = 'USD'");
        });
        builder.HasKey(p => p.Id).HasName("PK_InstrumentPrices");
        builder.Property(p => p.Id).UseIdentityColumn();

        builder.Property(p => p.PriceDate).HasColumnType("date");
        builder.Property(p => p.ClosePrice).HasPrecision(19, 6);
        builder.Property(p => p.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();

        builder.HasOne<Instrument>()
            .WithMany()
            .HasForeignKey(p => p.InstrumentId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_InstrumentPrices_Instruments");

        builder.HasIndex(p => new { p.InstrumentId, p.PriceDate })
            .IsUnique()
            .IsDescending(false, true)
            .IncludeProperties(p => p.ClosePrice)
            .HasDatabaseName("UX_InstrumentPrices_Instrument_PriceDate");
    }
}
