/*
================================================================================
 SeedData.sql - reference data and example trades. Idempotent: safe to run any number of times.
 Executed automatically by the API on startup when Database:SeedOnStartup = true (Development default),
 and by the integration tests. Can also be run manually:
     sqlcmd -S localhost -d TradeIngestion -E -C -i Database/Scripts/SeedData.sql

 Contents
   Accounts     ACC-001 (demo account, intentionally has NO trades - the demo script submits them)
                ACC-002 (example account with history: round trip, partial sell, correction, unpriced instrument)
   Instruments  AAPL US0378331005, MSFT US5949181045, NVDA US67066G1040
   Prices       AAPL 186.00 on 2025-03-01 (demo requirement); NVDA deliberately has no price.
   Trades       SEED-2001..SEED-2007 on ACC-002. SEED-2006 has a correction with AsOf 2025-03-05, so snapshots
                on 2025-03-01 and 2025-03-10 legitimately differ (effective-dated versions).
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

MERGE dbo.Accounts AS target
USING (VALUES
    ('ACC-001', N'Demo Account',                'USD'),
    ('ACC-002', N'Example Multi-Position Account', 'USD')
) AS source (AccountCode, Name, BaseCurrency)
    ON target.AccountCode = source.AccountCode
WHEN NOT MATCHED BY TARGET THEN
    INSERT (AccountCode, Name, BaseCurrency) VALUES (source.AccountCode, source.Name, source.BaseCurrency);

MERGE dbo.Instruments AS target
USING (VALUES
    ('US0378331005', 'AAPL', N'Apple Inc.',            'USD'),
    ('US5949181045', 'MSFT', N'Microsoft Corporation', 'USD'),
    ('US67066G1040', 'NVDA', N'NVIDIA Corporation',    'USD')
) AS source (Isin, Symbol, Name, Currency)
    ON target.Isin = source.Isin
WHEN NOT MATCHED BY TARGET THEN
    INSERT (Isin, Symbol, Name, Currency) VALUES (source.Isin, source.Symbol, source.Name, source.Currency);

-- Prices are upserted so re-running the seed restores the documented demo values.
MERGE dbo.InstrumentPrices AS target
USING (
    SELECT i.Id AS InstrumentId, v.PriceDate, v.ClosePrice, 'USD' AS Currency
    FROM (VALUES
        ('US0378331005', CAST('2025-02-28' AS date), CAST(184.500000 AS decimal(19,6))),
        ('US0378331005', CAST('2025-03-01' AS date), CAST(186.000000 AS decimal(19,6))),
        ('US5949181045', CAST('2025-02-28' AS date), CAST(410.000000 AS decimal(19,6))),
        ('US5949181045', CAST('2025-03-07' AS date), CAST(415.000000 AS decimal(19,6)))
    ) AS v (Isin, PriceDate, ClosePrice)
    INNER JOIN dbo.Instruments AS i ON i.Isin = v.Isin
) AS source
    ON target.InstrumentId = source.InstrumentId AND target.PriceDate = source.PriceDate
WHEN MATCHED AND target.ClosePrice <> source.ClosePrice THEN
    UPDATE SET ClosePrice = source.ClosePrice
WHEN NOT MATCHED BY TARGET THEN
    INSERT (InstrumentId, PriceDate, ClosePrice, Currency)
    VALUES (source.InstrumentId, source.PriceDate, source.ClosePrice, source.Currency);

DECLARE @SeedVersions TABLE
(
    ExternalRef   varchar(64)   NOT NULL,
    VersionNumber int           NOT NULL,
    AccountCode   varchar(32)   NOT NULL,
    Isin          char(12)      NOT NULL,
    Side          varchar(4)    NOT NULL,
    Quantity      decimal(18,6) NOT NULL,
    Price         decimal(19,6) NOT NULL,
    TradeDate     date          NOT NULL,
    AsOfUtc       datetime2(7)  NOT NULL,
    PRIMARY KEY (ExternalRef, VersionNumber)
);

INSERT INTO @SeedVersions (ExternalRef, VersionNumber, AccountCode, Isin, Side, Quantity, Price, TradeDate, AsOfUtc)
VALUES
    -- AAPL: round trip (position goes flat -> cost pool resets), then a new holding period at 170.
    ('SEED-2001', 1, 'ACC-002', 'US0378331005', 'BUY',  100, 150.00, '2025-01-10', '2025-01-10T15:00:00'),
    ('SEED-2002', 1, 'ACC-002', 'US0378331005', 'SELL', 100, 175.00, '2025-01-20', '2025-01-20T15:00:00'),
    ('SEED-2003', 1, 'ACC-002', 'US0378331005', 'BUY',   50, 170.00, '2025-02-03', '2025-02-03T15:00:00'),
    -- MSFT: buy, partial sell, buy; the last buy is corrected AFTER 2025-03-01 (20 -> 25 shares).
    ('SEED-2004', 1, 'ACC-002', 'US5949181045', 'BUY',   40, 400.00, '2025-02-05', '2025-02-05T15:00:00'),
    ('SEED-2005', 1, 'ACC-002', 'US5949181045', 'SELL',  10, 405.00, '2025-02-07', '2025-02-07T15:00:00'),
    ('SEED-2006', 1, 'ACC-002', 'US5949181045', 'BUY',   20, 420.00, '2025-02-10', '2025-02-10T15:00:00'),
    ('SEED-2006', 2, 'ACC-002', 'US5949181045', 'BUY',   25, 420.00, '2025-02-10', '2025-03-05T09:00:00'),
    -- NVDA: held but never priced -> reported as MissingPrice, excluded from totals, snapshot flagged incomplete.
    ('SEED-2007', 1, 'ACC-002', 'US67066G1040', 'BUY',   25, 120.00, '2025-02-12', '2025-02-12T15:00:00');

INSERT INTO dbo.Trades (ExternalRef, CurrentVersionNumber, LatestAsOfUtc, CreatedAtUtc, UpdatedAtUtc)
SELECT s.ExternalRef, MAX(s.VersionNumber), MAX(s.AsOfUtc), MIN(s.AsOfUtc), MAX(s.AsOfUtc)
FROM @SeedVersions AS s
WHERE NOT EXISTS (SELECT 1 FROM dbo.Trades AS t WHERE t.ExternalRef = s.ExternalRef)
GROUP BY s.ExternalRef;

INSERT INTO dbo.TradeVersions
    (TradeId, VersionNumber, AccountId, InstrumentId, Side, Quantity, Price, TradeDate, AsOfUtc, ReceivedAtUtc, CorrelationId)
SELECT t.Id, s.VersionNumber, a.Id, i.Id, s.Side, s.Quantity, s.Price, s.TradeDate, s.AsOfUtc, s.AsOfUtc, 'seed'
FROM @SeedVersions AS s
INNER JOIN dbo.Trades      AS t ON t.ExternalRef = s.ExternalRef
INNER JOIN dbo.Accounts    AS a ON a.AccountCode = s.AccountCode
INNER JOIN dbo.Instruments AS i ON i.Isin = s.Isin
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.TradeVersions AS v
    WHERE v.TradeId = t.Id AND v.VersionNumber = s.VersionNumber
);

COMMIT TRANSACTION;
GO
