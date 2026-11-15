/*
================================================================================
 ResetDemoData.sql - DEMO/DEV ONLY. Removes every trade (all versions) that touches the demo account so that
 scripts/Demo.ps1 can be re-run from a clean state, then restores the documented AAPL price.

 This deletes audit history and must never be run against a production database; in production, trade versions
 are append-only and never deleted.
================================================================================
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @AccountCode varchar(32) = 'ACC-001';

BEGIN TRANSACTION;

DECLARE @DemoTrades TABLE (TradeId bigint NOT NULL PRIMARY KEY);

INSERT INTO @DemoTrades (TradeId)
SELECT DISTINCT tv.TradeId
FROM dbo.TradeVersions AS tv
INNER JOIN dbo.Accounts AS a ON a.Id = tv.AccountId
WHERE a.AccountCode = @AccountCode;

DELETE tv
FROM dbo.TradeVersions AS tv
INNER JOIN @DemoTrades AS d ON d.TradeId = tv.TradeId;

DELETE t
FROM dbo.Trades AS t
INNER JOIN @DemoTrades AS d ON d.TradeId = t.Id;

UPDATE ip
SET ip.ClosePrice = 186.00
FROM dbo.InstrumentPrices AS ip
INNER JOIN dbo.Instruments AS i ON i.Id = ip.InstrumentId
WHERE i.Isin = 'US0378331005' AND ip.PriceDate = '2025-03-01';

COMMIT TRANSACTION;

SELECT COUNT(*) AS RemovedDemoTrades FROM @DemoTrades;
GO
