using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeIngestion.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dbo");

            migrationBuilder.CreateTable(
                name: "Accounts",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountCode = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BaseCurrency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Instruments",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Isin = table.Column<string>(type: "char(12)", unicode: false, fixedLength: true, maxLength: 12, nullable: false),
                    Symbol = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instruments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Trades",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ExternalRef = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    CurrentVersionNumber = table.Column<int>(type: "int", nullable: false),
                    LatestAsOfUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trades", x => x.Id);
                    table.CheckConstraint("CK_Trades_CurrentVersionNumber_Positive", "[CurrentVersionNumber] >= 1");
                });

            migrationBuilder.CreateTable(
                name: "InstrumentPrices",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InstrumentId = table.Column<int>(type: "int", nullable: false),
                    PriceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ClosePrice = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstrumentPrices", x => x.Id);
                    table.CheckConstraint("CK_InstrumentPrices_ClosePrice_Positive", "[ClosePrice] > 0");
                    table.CheckConstraint("CK_InstrumentPrices_Currency_USD", "[Currency] = 'USD'");
                    table.ForeignKey(
                        name: "FK_InstrumentPrices_Instruments",
                        column: x => x.InstrumentId,
                        principalSchema: "dbo",
                        principalTable: "Instruments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TradeVersions",
                schema: "dbo",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TradeId = table.Column<long>(type: "bigint", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    InstrumentId = table.Column<int>(type: "int", nullable: false),
                    Side = table.Column<string>(type: "varchar(4)", unicode: false, maxLength: 4, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Price = table.Column<decimal>(type: "decimal(19,6)", precision: 19, scale: 6, nullable: false),
                    TradeDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AsOfUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TradeVersions", x => x.Id);
                    table.CheckConstraint("CK_TradeVersions_Price_Positive", "[Price] > 0");
                    table.CheckConstraint("CK_TradeVersions_Quantity_Positive", "[Quantity] > 0");
                    table.CheckConstraint("CK_TradeVersions_Side", "[Side] IN ('BUY', 'SELL')");
                    table.CheckConstraint("CK_TradeVersions_VersionNumber_Positive", "[VersionNumber] >= 1");
                    table.ForeignKey(
                        name: "FK_TradeVersions_Accounts",
                        column: x => x.AccountId,
                        principalSchema: "dbo",
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradeVersions_Instruments",
                        column: x => x.InstrumentId,
                        principalSchema: "dbo",
                        principalTable: "Instruments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TradeVersions_Trades",
                        column: x => x.TradeId,
                        principalSchema: "dbo",
                        principalTable: "Trades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_Accounts_AccountCode",
                schema: "dbo",
                table: "Accounts",
                column: "AccountCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_InstrumentPrices_Instrument_PriceDate",
                schema: "dbo",
                table: "InstrumentPrices",
                columns: new[] { "InstrumentId", "PriceDate" },
                unique: true,
                descending: new[] { false, true })
                .Annotation("SqlServer:Include", new[] { "ClosePrice" });

            migrationBuilder.CreateIndex(
                name: "UX_Instruments_Isin",
                schema: "dbo",
                table: "Instruments",
                column: "Isin",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TradeVersions_Account_AsOf",
                schema: "dbo",
                table: "TradeVersions",
                columns: new[] { "AccountId", "AsOfUtc" })
                .Annotation("SqlServer:Include", new[] { "TradeId" });

            migrationBuilder.CreateIndex(
                name: "IX_TradeVersions_InstrumentId",
                schema: "dbo",
                table: "TradeVersions",
                column: "InstrumentId");

            migrationBuilder.CreateIndex(
                name: "UX_TradeVersions_Trade_AsOf",
                schema: "dbo",
                table: "TradeVersions",
                columns: new[] { "TradeId", "AsOfUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_TradeVersions_Trade_Version",
                schema: "dbo",
                table: "TradeVersions",
                columns: new[] { "TradeId", "VersionNumber" },
                unique: true)
                .Annotation("SqlServer:Include", new[] { "AsOfUtc", "AccountId", "InstrumentId", "Side", "Quantity", "Price", "TradeDate" });

            migrationBuilder.CreateIndex(
                name: "UX_Trades_ExternalRef",
                schema: "dbo",
                table: "Trades",
                column: "ExternalRef",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstrumentPrices",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "TradeVersions",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Accounts",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Instruments",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Trades",
                schema: "dbo");
        }
    }
}
