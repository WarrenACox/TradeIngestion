using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TradeIngestion.Application.Trades;
using TradeIngestion.Domain.Trades;

namespace TradeIngestion.Infrastructure.Persistence;

internal sealed class TradeRepository : ITradeRepository
{
    private readonly TradeIngestionDbContext _db;

    public TradeRepository(TradeIngestionDbContext db) => _db = db;

    public async Task<Trade?> GetForUpdateAsync(string externalRef, CancellationToken cancellationToken)
    {
        if (_db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("GetForUpdateAsync must be called inside a transaction; the lock is held until commit.");
        }

        // UPDLOCK: only one transaction at a time may hold the intent-to-modify lock on this key.
        // HOLDLOCK: serializable semantics for THIS predicate only - if the row does not exist, SQL Server takes a
        // key-range lock on the gap in UX_Trades_ExternalRef, so a concurrent first-time insert of the same
        // ExternalRef blocks instead of racing. Both locks are held until commit.
        // The parameter is explicitly varchar(64) to match the column: an nvarchar parameter would force an
        // implicit conversion on the column side and turn the seek into a scan (and lock far more than one key).
        var parameter = new SqlParameter("@externalRef", SqlDbType.VarChar, 64) { Value = externalRef };

        var trade = await _db.Trades
            .FromSqlRaw("SELECT * FROM dbo.Trades WITH (UPDLOCK, HOLDLOCK) WHERE ExternalRef = @externalRef", parameter)
            .SingleOrDefaultAsync(cancellationToken);

        if (trade is not null)
        {
            await _db.Entry(trade).Collection(t => t.Versions).LoadAsync(cancellationToken);
        }

        return trade;
    }

    public void Add(Trade trade) => _db.Trades.Add(trade);
}
