/*
================================================================================
 dbo.GetPortfolioSnapshot
================================================================================
 Point-in-time portfolio valuation for one account, in USD.

 Parameters
   @AccountId     Business account code (e.g. 'ACC-001'), matches dbo.Accounts.AccountCode.
   @SnapshotDate  Valuation date. Trades with TradeDate <= @SnapshotDate are included, using the version of each
                  trade that was known by the END of that day (AsOfUtc < @SnapshotDate + 1 day, UTC).

 Result set 1 - one row per open position (NetQuantity <> 0)
   AccountId, SnapshotDate, Isin, Symbol, NetQuantity, AverageUnitCost, MarketPrice, PriceDate,
   MarketValue, CostBasis, UnrealizedPnl, ValuationStatus ('Valued' | 'MissingPrice' | 'ShortPosition')

 Result set 2 - exactly one summary row
   AccountId, SnapshotDate, BaseCurrency, AccountTotalMarketValue, TotalUnrealizedPnl, PortfolioValue,
   PositionCount, UnpricedPositionCount, IsValuationComplete

 Calculation rules (identical to TradeIngestion.Domain.Portfolio.AverageCostCalculator / PositionValuation):
   1. Version selection: per trade, the highest VersionNumber with AsOfUtc < cutoff (ROW_NUMBER = 1).
      Selection happens BEFORE filtering on account and trade date, so a correction that moves a trade to another
      account, or past the snapshot date, can never let an older version leak back in (no double counting).
   2. Ordering: per instrument by (TradeDate, TradeId).
   3. BUY adds quantity and Quantity*Price to the open cost pool. SELL reduces quantity only (cost relieved at the
      average, so the average is unchanged). When the running quantity reaches <= 0 the holding period ends and the
      cost pool resets: only BUYs after the last such point form the average.
   4. AverageUnitCost = ROUND(open buy cost / open buy quantity, 6); NULL for short positions.
   5. MarketPrice = latest close with PriceDate <= @SnapshotDate (carried forward; PriceDate is returned so
      staleness is visible). No price => MarketValue / UnrealizedPnl are NULL and the position is flagged.
   6. MarketValue = ROUND(NetQuantity * MarketPrice, 2); CostBasis = ROUND(NetQuantity * AverageUnitCost, 2);
      UnrealizedPnl = MarketValue - CostBasis. ROUND is half-away-from-zero, as is the C# implementation.
   7. Totals sum the rounded position values, ignoring NULLs. IsValuationComplete = 0 if any price is missing.
      PortfolioValue equals AccountTotalMarketValue today (no cash/accrual ledger is modelled); it is a separate
      column so the contract stays stable when cash balances are added.

 Decimal precision: Quantity decimal(18,6) x Price decimal(19,6) yields decimal(38,12) - exact. Operands are
 CAST to bounded precisions before multiplication/division so SQL Server never has to reduce the result scale
 below the rounding point (see SOLUTION.md, "SQL stored procedure design").

 Supporting indexes (see Indexes.sql):
   UX_Accounts_AccountCode                  - account code -> key seek
   IX_TradeVersions_Account_AsOf            - (AccountId, AsOfUtc) INCLUDE (TradeId): candidate trades, range seek
   UX_TradeVersions_Trade_Version           - (TradeId, VersionNumber) INCLUDE (...): covering seek per trade,
                                              already ordered for ROW_NUMBER (no sort)
   UX_InstrumentPrices_Instrument_PriceDate - (InstrumentId, PriceDate DESC) INCLUDE (ClosePrice): TOP (1) seek
================================================================================
*/
SET ANSI_NULLS ON;
GO
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE dbo.GetPortfolioSnapshot
    @AccountId    varchar(32),
    @SnapshotDate date
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @AccountId IS NULL OR @SnapshotDate IS NULL
        THROW 50001, N'@AccountId and @SnapshotDate are required.', 1;

    DECLARE @AccountKey int =
    (
        SELECT a.Id
        FROM dbo.Accounts AS a
        WHERE a.AccountCode = @AccountId
    );

    IF @AccountKey IS NULL
    BEGIN
        DECLARE @NotFoundMessage nvarchar(200) = CONCAT(N'Account ''', @AccountId, N''' does not exist.');
        THROW 50002, @NotFoundMessage, 1;
    END;

    -- Exclusive upper bound for version knowledge: the end of the snapshot day (UTC).
    -- Computed once into a variable so the AsOfUtc predicates stay SARGable (no function on the column).
    DECLARE @KnowledgeCutoffUtc datetime2(7) = DATEADD(DAY, 1, CAST(@SnapshotDate AS datetime2(7)));

    -- Positions are evaluated once by a single statement (statement-level read consistency) and then returned as
    -- two result sets. Cardinality is bounded by the number of instruments held, so a table variable is appropriate.
    DECLARE @Positions TABLE
    (
        InstrumentId    int           NOT NULL PRIMARY KEY,
        Isin            char(12)      NOT NULL,
        Symbol          varchar(16)   NOT NULL,
        NetQuantity     decimal(24,6) NOT NULL,
        AverageUnitCost decimal(19,6) NULL,
        MarketPrice     decimal(19,6) NULL,
        PriceDate       date          NULL,
        MarketValue     decimal(24,2) NULL,
        CostBasis       decimal(24,2) NULL,
        UnrealizedPnl   decimal(24,2) NULL,
        ValuationStatus varchar(16)   NOT NULL
    );

    WITH CandidateTrades AS
    (
        -- Every trade that had at least one version on this account known before the cutoff.
        SELECT DISTINCT tv.TradeId
        FROM dbo.TradeVersions AS tv
        WHERE tv.AccountId = @AccountKey
          AND tv.AsOfUtc < @KnowledgeCutoffUtc
    ),
    RankedVersions AS
    (
        -- All versions of those trades known before the cutoff (on ANY account), newest first.
        SELECT
            tv.TradeId,
            tv.AccountId,
            tv.InstrumentId,
            tv.Side,
            tv.Quantity,
            tv.Price,
            tv.TradeDate,
            ROW_NUMBER() OVER (PARTITION BY tv.TradeId ORDER BY tv.VersionNumber DESC) AS VersionRank
        FROM CandidateTrades AS ct
        INNER JOIN dbo.TradeVersions AS tv
            ON tv.TradeId = ct.TradeId
        WHERE tv.AsOfUtc < @KnowledgeCutoffUtc
    ),
    EffectiveTrades AS
    (
        -- Exactly one row per trade; account and trade-date filters applied to the effective version only.
        SELECT
            rv.TradeId,
            rv.InstrumentId,
            rv.Side,
            rv.Quantity,
            rv.Price,
            rv.TradeDate,
            CASE rv.Side WHEN 'BUY' THEN rv.Quantity ELSE -rv.Quantity END AS SignedQuantity
        FROM RankedVersions AS rv
        WHERE rv.VersionRank = 1
          AND rv.AccountId = @AccountKey
          AND rv.TradeDate <= @SnapshotDate
    ),
    SequencedTrades AS
    (
        SELECT
            et.TradeId,
            et.InstrumentId,
            et.Side,
            et.Quantity,
            et.Price,
            et.SignedQuantity,
            ROW_NUMBER() OVER (PARTITION BY et.InstrumentId ORDER BY et.TradeDate, et.TradeId) AS Seq,
            SUM(et.SignedQuantity) OVER (PARTITION BY et.InstrumentId ORDER BY et.TradeDate, et.TradeId
                                         ROWS UNBOUNDED PRECEDING) AS RunningQuantity
        FROM EffectiveTrades AS et
    ),
    HoldingPeriods AS
    (
        -- LastFlatSeq: the last trade after which the position was flat or short. Only BUYs after it carry cost.
        SELECT
            st.InstrumentId,
            st.Side,
            st.Quantity,
            st.Price,
            st.SignedQuantity,
            st.Seq,
            MAX(CASE WHEN st.RunningQuantity <= 0 THEN st.Seq ELSE 0 END)
                OVER (PARTITION BY st.InstrumentId) AS LastFlatSeq
        FROM SequencedTrades AS st
    ),
    Positions AS
    (
        SELECT
            hp.InstrumentId,
            CAST(SUM(hp.SignedQuantity) AS decimal(24,6)) AS NetQuantity,
            SUM(CASE WHEN hp.Side = 'BUY' AND hp.Seq > hp.LastFlatSeq THEN hp.Quantity ELSE 0 END) AS OpenBuyQuantity,
            SUM(CASE WHEN hp.Side = 'BUY' AND hp.Seq > hp.LastFlatSeq THEN hp.Quantity * hp.Price ELSE 0 END) AS OpenBuyCost
        FROM HoldingPeriods AS hp
        GROUP BY hp.InstrumentId
        HAVING SUM(hp.SignedQuantity) <> 0
    ),
    CostedPositions AS
    (
        SELECT
            p.InstrumentId,
            p.NetQuantity,
            CASE WHEN p.NetQuantity > 0
                 THEN CAST(ROUND(CAST(p.OpenBuyCost AS decimal(30,12)) / CAST(p.OpenBuyQuantity AS decimal(24,6)), 6)
                           AS decimal(19,6))
            END AS AverageUnitCost
        FROM Positions AS p
    ),
    ValuedPositions AS
    (
        SELECT
            cp.InstrumentId,
            i.Isin,
            i.Symbol,
            cp.NetQuantity,
            cp.AverageUnitCost,
            px.ClosePrice AS MarketPrice,
            px.PriceDate,
            CAST(ROUND(CAST(cp.NetQuantity AS decimal(18,6)) * px.ClosePrice, 2) AS decimal(24,2)) AS MarketValue,
            CAST(ROUND(CAST(cp.NetQuantity AS decimal(18,6)) * cp.AverageUnitCost, 2) AS decimal(24,2)) AS CostBasis
        FROM CostedPositions AS cp
        INNER JOIN dbo.Instruments AS i
            ON i.Id = cp.InstrumentId
        OUTER APPLY
        (
            SELECT TOP (1) ip.ClosePrice, ip.PriceDate
            FROM dbo.InstrumentPrices AS ip
            WHERE ip.InstrumentId = cp.InstrumentId
              AND ip.PriceDate <= @SnapshotDate
            ORDER BY ip.PriceDate DESC
        ) AS px
    )
    INSERT INTO @Positions
        (InstrumentId, Isin, Symbol, NetQuantity, AverageUnitCost, MarketPrice, PriceDate,
         MarketValue, CostBasis, UnrealizedPnl, ValuationStatus)
    SELECT
        vp.InstrumentId,
        vp.Isin,
        vp.Symbol,
        vp.NetQuantity,
        vp.AverageUnitCost,
        vp.MarketPrice,
        vp.PriceDate,
        vp.MarketValue,
        vp.CostBasis,
        vp.MarketValue - vp.CostBasis,
        CASE
            WHEN vp.MarketPrice IS NULL THEN 'MissingPrice'
            WHEN vp.NetQuantity < 0     THEN 'ShortPosition'
            ELSE 'Valued'
        END
    FROM ValuedPositions AS vp;

    -- Result set 1: instrument-level positions.
    SELECT
        @AccountId     AS AccountId,
        @SnapshotDate  AS SnapshotDate,
        p.Isin,
        p.Symbol,
        p.NetQuantity,
        p.AverageUnitCost,
        p.MarketPrice,
        p.PriceDate,
        p.MarketValue,
        p.CostBasis,
        p.UnrealizedPnl,
        p.ValuationStatus
    FROM @Positions AS p
    ORDER BY p.Symbol, p.Isin;

    -- Result set 2: account / portfolio totals (always exactly one row).
    SELECT
        @AccountId                                                   AS AccountId,
        @SnapshotDate                                                AS SnapshotDate,
        'USD'                                                        AS BaseCurrency,
        -- ISNULL inside SUM: NULL (unpriced) values contribute nothing, without the ANSI "Null value is eliminated" warning.
        CAST(ISNULL(SUM(ISNULL(p.MarketValue, 0)), 0) AS decimal(24,2))   AS AccountTotalMarketValue,
        CAST(ISNULL(SUM(ISNULL(p.UnrealizedPnl, 0)), 0) AS decimal(24,2)) AS TotalUnrealizedPnl,
        CAST(ISNULL(SUM(ISNULL(p.MarketValue, 0)), 0) AS decimal(24,2))   AS PortfolioValue,
        COUNT(*)                                                     AS PositionCount,
        ISNULL(SUM(CASE WHEN p.ValuationStatus = 'MissingPrice' THEN 1 ELSE 0 END), 0) AS UnpricedPositionCount,
        CAST(CASE WHEN EXISTS (SELECT 1 FROM @Positions AS m WHERE m.ValuationStatus = 'MissingPrice')
                  THEN 0 ELSE 1 END AS bit)                          AS IsValuationComplete
    FROM @Positions AS p;
END;
GO
