/*
================================================================================
 Indexes.sql - reference copy of every index and uniqueness constraint that supports ingestion and the snapshot.

 The authoritative definitions are created by the EF Core migrations
 (src/TradeIngestion.Infrastructure/Persistence/Migrations). This script is the DBA-facing view of the same
 objects: every statement is guarded with IF NOT EXISTS, so running it against a migrated database is a no-op, and
 running it against a hand-built schema creates exactly what the application expects.

 Index                                      Used by
 -----------------------------------------  ---------------------------------------------------------------------
 UX_Trades_ExternalRef (UNIQUE)              Idempotency key. Ingestion seeks it under UPDLOCK, HOLDLOCK (key-range
                                             lock serialises concurrent submissions of one ExternalRef). Also the
                                             final guarantee against duplicates if application logic is bypassed.
 UX_TradeVersions_Trade_Version (UNIQUE)     No two writers can create the same version number. Covering index for
                                             the snapshot's ROW_NUMBER() OVER (PARTITION BY TradeId ORDER BY
                                             VersionNumber DESC): seek per trade, rows already ordered, no sort.
 UX_TradeVersions_Trade_AsOf (UNIQUE)        One version per (trade, AsOf): enforces "same AsOf = same event".
 IX_TradeVersions_Account_AsOf               Snapshot entry point: range seek on (AccountId = @a AND AsOfUtc < @c),
                                             yields candidate TradeIds without touching the base table.
 IX_TradeVersions_InstrumentId               Foreign-key support (instrument maintenance / referential checks).
 UX_InstrumentPrices_Instrument_PriceDate    One close per instrument per day; DESC order makes the
     (UNIQUE)                                "latest price on or before date" TOP (1) a single-row seek.
 UX_Accounts_AccountCode / UX_Instruments_Isin  Business-key lookups (equality seeks).
================================================================================
*/
SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Accounts_AccountCode' AND object_id = OBJECT_ID(N'dbo.Accounts'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_Accounts_AccountCode
        ON dbo.Accounts (AccountCode);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Instruments_Isin' AND object_id = OBJECT_ID(N'dbo.Instruments'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_Instruments_Isin
        ON dbo.Instruments (Isin);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Trades_ExternalRef' AND object_id = OBJECT_ID(N'dbo.Trades'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_Trades_ExternalRef
        ON dbo.Trades (ExternalRef);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TradeVersions_Trade_Version' AND object_id = OBJECT_ID(N'dbo.TradeVersions'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_TradeVersions_Trade_Version
        ON dbo.TradeVersions (TradeId, VersionNumber)
        INCLUDE (AsOfUtc, AccountId, InstrumentId, Side, Quantity, Price, TradeDate);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TradeVersions_Trade_AsOf' AND object_id = OBJECT_ID(N'dbo.TradeVersions'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_TradeVersions_Trade_AsOf
        ON dbo.TradeVersions (TradeId, AsOfUtc);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TradeVersions_Account_AsOf' AND object_id = OBJECT_ID(N'dbo.TradeVersions'))
    CREATE NONCLUSTERED INDEX IX_TradeVersions_Account_AsOf
        ON dbo.TradeVersions (AccountId, AsOfUtc)
        INCLUDE (TradeId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_TradeVersions_InstrumentId' AND object_id = OBJECT_ID(N'dbo.TradeVersions'))
    CREATE NONCLUSTERED INDEX IX_TradeVersions_InstrumentId
        ON dbo.TradeVersions (InstrumentId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_InstrumentPrices_Instrument_PriceDate' AND object_id = OBJECT_ID(N'dbo.InstrumentPrices'))
    CREATE UNIQUE NONCLUSTERED INDEX UX_InstrumentPrices_Instrument_PriceDate
        ON dbo.InstrumentPrices (InstrumentId, PriceDate DESC)
        INCLUDE (ClosePrice);
GO
