using System.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace TradeIngestion.Tests.Integration.Infrastructure;

/// <summary>
/// Real SQL Server integration fixture. Creates a uniquely named database per test class, lets the API's own
/// startup path apply the EF Core migrations (including dbo.GetPortfolioSnapshot) and run SeedData.sql, and drops
/// the database afterwards.
///
/// Server selection (no database name - one is generated):
///   TRADEINGESTION_TEST_SQLSERVER  e.g. "Server=localhost;Integrated Security=True;TrustServerCertificate=True"
///   default                        the docker-compose instance (localhost,1433 / sa).
///
/// SQL Server is used deliberately instead of SQLite or the EF in-memory provider: the behaviours under test
/// (UPDLOCK/HOLDLOCK key-range locking, unique-index races, rowversion, decimal(38,x) arithmetic, the T-SQL
/// stored procedure) do not exist in those providers.
/// </summary>
public class SqlServerApiFixture : IAsyncLifetime
{
    public const string ServerEnvironmentVariable = "TRADEINGESTION_TEST_SQLSERVER";
    private const string DefaultServer = "Server=localhost,1433;User Id=sa;Password=TradeIngestion!Dev2025;TrustServerCertificate=True;Encrypt=True";

    private readonly string _databaseName = $"TradeIngestionTests_{Guid.NewGuid():N}";
    private WebApplicationFactory<Program>? _factory;

    public string ConnectionString { get; private set; } = string.Empty;

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("Fixture not initialised.");

    protected virtual bool EnableVersionedCorrections => true;

    public HttpClient CreateClient() => Factory.CreateClient();

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable(ServerEnvironmentVariable) ?? DefaultServer;
        ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = _databaseName }.ConnectionString;

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("IntegrationTest");
            builder.UseSetting("ConnectionStrings:TradeIngestion", ConnectionString);
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
            builder.UseSetting("Database:SeedOnStartup", "true");
            builder.UseSetting("TradeProcessing:EnableVersionedCorrections", EnableVersionedCorrections ? "true" : "false");
            builder.UseSetting("Logging:LogLevel:Default", "Warning");
        });

        try
        {
            // Starting the host runs migrations + seed (Program.InitializeDatabaseAsync).
            _ = _factory.Server;
        }
        catch (Exception ex) when (ex is SqlException || ex.InnerException is SqlException)
        {
            throw new InvalidOperationException(
                $"Integration tests need a reachable SQL Server. Start one with 'docker compose up -d' or set {ServerEnvironmentVariable}. " +
                $"Tried: {new SqlConnectionStringBuilder(server) { Password = "***" }.DataSource}",
                ex);
        }

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID(N'{_databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_databaseName}];
            END
            """;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Creates an isolated account so tests in the same class never share positions.</summary>
    public async Task<string> CreateAccountAsync(string prefix)
    {
        var code = $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}".ToUpperInvariant();
        await ExecuteAsync(
            "INSERT INTO dbo.Accounts (AccountCode, Name, BaseCurrency) VALUES (@code, N'Test account', 'USD')",
            new SqlParameter("@code", SqlDbType.VarChar, 32) { Value = code });
        return code;
    }

    public async Task ExecuteAsync(string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params SqlParameter[] parameters)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task<int> CountVersionsAsync(string externalRef) =>
        ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.TradeVersions v JOIN dbo.Trades t ON t.Id = v.TradeId WHERE t.ExternalRef = @r",
            new SqlParameter("@r", SqlDbType.VarChar, 64) { Value = externalRef });

    public Task<int> CountTradesAsync(string externalRef) =>
        ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.Trades WHERE ExternalRef = @r",
            new SqlParameter("@r", SqlDbType.VarChar, 64) { Value = externalRef });

    /// <summary>Executes dbo.GetPortfolioSnapshot exactly as a reporting client would.</summary>
    public async Task<StoredProcedureSnapshot> ExecuteSnapshotProcedureAsync(string accountCode, DateOnly snapshotDate)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("dbo.GetPortfolioSnapshot", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add(new SqlParameter("@AccountId", SqlDbType.VarChar, 32) { Value = accountCode });
        command.Parameters.Add(new SqlParameter("@SnapshotDate", SqlDbType.Date) { Value = snapshotDate.ToDateTime(TimeOnly.MinValue) });

        await using var reader = await command.ExecuteReaderAsync();
        var positions = new List<StoredProcedurePosition>();
        while (await reader.ReadAsync())
        {
            positions.Add(new StoredProcedurePosition(
                reader.GetString(reader.GetOrdinal("Isin")),
                reader.GetString(reader.GetOrdinal("Symbol")),
                reader.GetDecimal(reader.GetOrdinal("NetQuantity")),
                NullableDecimal(reader, "AverageUnitCost"),
                NullableDecimal(reader, "MarketPrice"),
                NullableDecimal(reader, "MarketValue"),
                NullableDecimal(reader, "CostBasis"),
                NullableDecimal(reader, "UnrealizedPnl"),
                reader.GetString(reader.GetOrdinal("ValuationStatus"))));
        }

        Assert.True(await reader.NextResultAsync(), "Expected a second (summary) result set.");
        Assert.True(await reader.ReadAsync(), "Expected exactly one summary row.");
        var summary = new StoredProcedureSummary(
            reader.GetDecimal(reader.GetOrdinal("AccountTotalMarketValue")),
            reader.GetDecimal(reader.GetOrdinal("TotalUnrealizedPnl")),
            reader.GetDecimal(reader.GetOrdinal("PortfolioValue")),
            reader.GetInt32(reader.GetOrdinal("PositionCount")),
            reader.GetInt32(reader.GetOrdinal("UnpricedPositionCount")),
            reader.GetBoolean(reader.GetOrdinal("IsValuationComplete")));

        return new StoredProcedureSnapshot(positions, summary);
    }

    private static decimal? NullableDecimal(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }
}

/// <summary>Same fixture with the feature flag off (legacy insert-only behaviour).</summary>
public sealed class LegacyModeApiFixture : SqlServerApiFixture
{
    protected override bool EnableVersionedCorrections => false;
}

public sealed record StoredProcedurePosition(
    string Isin, string Symbol, decimal NetQuantity, decimal? AverageUnitCost, decimal? MarketPrice,
    decimal? MarketValue, decimal? CostBasis, decimal? UnrealizedPnl, string ValuationStatus);

public sealed record StoredProcedureSummary(
    decimal AccountTotalMarketValue, decimal TotalUnrealizedPnl, decimal PortfolioValue,
    int PositionCount, int UnpricedPositionCount, bool IsValuationComplete);

public sealed record StoredProcedureSnapshot(IReadOnlyList<StoredProcedurePosition> Positions, StoredProcedureSummary Summary);
