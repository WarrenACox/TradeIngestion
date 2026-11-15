using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TradeIngestion.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Deploys dbo.GetPortfolioSnapshot from Database/Scripts/GetPortfolioSnapshot.sql (embedded resource).
    /// The script is CREATE OR ALTER, so it is idempotent. Future changes to the procedure should be shipped in a
    /// NEW migration (re-running the script) so that the history of deployments stays explicit.
    /// </summary>
    public partial class AddGetPortfolioSnapshotProcedure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var batch in EmbeddedSqlScripts.SplitBatches(EmbeddedSqlScripts.Read(EmbeddedSqlScripts.GetPortfolioSnapshot)))
            {
                migrationBuilder.Sql(batch);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.GetPortfolioSnapshot;");
        }
    }
}
