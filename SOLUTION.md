# SOLUTION.md: Design, Decisions and Trade-offs

## Contents
1. [Requirements analysis and assumptions](#1-requirements-analysis-and-assumptions)
2. [Overall architecture](#2-overall-architecture)
3. [Database design](#3-database-design)
4. [Idempotency strategy](#4-idempotency-strategy)
5. [Correction and audit strategy](#5-correction-and-audit-strategy)
6. [Concurrency handling](#6-concurrency-handling)
7. [Cost-basis assumptions](#7-cost-basis-assumptions)
8. [Snapshot calculation](#8-snapshot-calculation)
9. [SQL stored procedure design](#9-sql-stored-procedure-design)
10. [Indexing, performance and SARGability](#10-indexing-performance-and-sargability)
11. [Partitioning and archival at higher volumes](#11-partitioning-and-archival-at-higher-volumes)
12. [Feature flag strategy and safe rollout](#12-feature-flag-strategy-and-safe-rollout)
13. [Logging and monitoring](#13-logging-and-monitoring)
14. [Error handling](#14-error-handling)
15. [Testing approach](#15-testing-approach)
16. [Trade-offs and limitations](#16-trade-offs-and-limitations)
17. [.NET Framework 4.8 versus .NET 8 integration](#17-net-framework-48-versus-net-8-integration)
18. [Evolving this into a production financial platform](#18-evolving-this-into-a-production-financial-platform)

---

## 1. Requirements analysis and assumptions

### Ambiguities and the decisions taken

| Ambiguity | Decision | Why |
|---|---|---|
| What is a "duplicate"? | Same `ExternalRef` **and** same `AsOf` **and** same economic content (account, instrument, side, quantity, price, trade date). | ExternalRef alone cannot tell a retry from a correction. |
| Same `ExternalRef` + same `AsOf` but **different** content? | **409 ConflictingPayload**; nothing applied. | The source has contradicted itself. Picking a winner silently is how books break. This needs a human. |
| Later `AsOf` but identical content? | Treated as a duplicate (`DuplicateIgnored`). The `AsOf` watermark advances, but **no new version** is written. | Avoids audit noise. Advancing the watermark still makes any older in-between event stale. |
| Older `AsOf` than the latest seen | `StaleIgnored`, unless it exactly replays a stored version (then `DuplicateIgnored`). | Out-of-order delivery must never overwrite newer knowledge. |
| "Snapshot as of date" | Trades with `TradeDate <= date`, using for each trade the version known by the **end of that day (UTC)** (`AsOf < date + 1`). | A historical snapshot is reproducible: asking for 2025-03-01 next year returns what the book said on 2025-03-01. See §8. |
| Can a correction change the account or instrument? | Yes, all economic fields live on the version. | Allocation fixes are real. The SQL and C# both select the version **before** filtering by account, so a moved trade is never double counted (tested). |
| Prices: which one? | Latest close with `PriceDate <= snapshot date` (carried forward). `PriceDate` is returned so staleness is visible. | Weekends and holidays have no price. Carry-forward is standard, but hiding its age is misleading. |
| Missing price | Position is still returned (quantity, cost) with `valuationStatus = MissingPrice`. Market value and P/L are **null, not zero**, and excluded from totals. `isValuationComplete = false`. | Requirement: never silently produce misleading valuations. |
| Currency | USD only. A `CHECK (Currency = 'USD')` constraint on prices prevents mixed-currency totals until an FX source exists. | No FX data is in scope. A constraint is better than a silent assumption. |
| Unknown account/instrument on ingestion | **422** with field errors. Reference data is never auto-created. | In a mature platform the instrument master is owned elsewhere. Auto-creating "AAPLL" is a data-quality incident. |
| `AsOf` without a UTC offset | **400**. | The default JSON converter assumes server local time, which would order corrections differently per server. |
| Average cost method | Holding-period weighted average cost (§7). | Exact SQL/C# parity with set-based SQL, and it handles the most misleading case (flat then re-buy). |

### Key technical risks identified up front, and how each is addressed

| Risk | Mitigation | Evidence |
|---|---|---|
| Double application under concurrent retries | Key-range lock plus unique constraints plus bounded retry (§6) | `ConcurrencyTests`: 25 parallel identical submissions give 1 Created and 24 DuplicateIgnored, with 1 row |
| Out-of-order corrections overwriting newer data | AsOf watermark; versions append-only | 10 concurrent corrections fired in reverse order converge on the latest AsOf with a gapless history |
| Historical versions double counted in the snapshot | Exactly one version per trade via `ROW_NUMBER() = 1` / `NOT EXISTS` before filtering | `SnapshotSqlParityTests`, including the "moved account" case |
| SQL and C# disagree | Identical rules, rounding mode and ordering; parity test on 4 dates of a non-trivial portfolio | `Stored_procedure_matches_api_snapshot` |
| Silent decimal rounding / float use | `decimal` everywhere; validated max 6 dp before persistence; explicit precisions | Validator tests; schema `decimal(18,6)` / `decimal(19,6)` |
| Time-zone bugs | Offset mandatory on input; everything stored UTC (`datetime2(7)`); materialised as `DateTimeKind.Utc` | Validator + domain guards + API test |

---

## 2. Overall architecture

Pragmatic Clean Architecture with dependencies pointing inwards:

```
TradeIngestion.Api ───────────────┐
  Controllers, contracts, JSON,    │ references
  correlation middleware,          ▼
  exception handler, Swagger   TradeIngestion.Infrastructure ──► TradeIngestion.Application ──► TradeIngestion.Domain
                                 EF Core, SQL Server,            use cases, validation,          Trade aggregate,
                                 locking repository,             ports (interfaces),             AverageCostCalculator,
                                 migrations, scripts             options, logging events         PositionValuation
```

| Project | Responsibility | Deliberately does **not** contain |
|---|---|---|
| **Domain** | Business rules with no dependencies. `Trade.Evaluate()` classifies a submission (duplicate / correction / stale / conflict). `AverageCostCalculator` and `PositionValuation` define the money maths. | EF attributes, logging, I/O |
| **Application** | `TradeIngestionService` (validate, resolve reference data, then classify and persist in one transaction), `PortfolioSnapshotService`, validation, `TradeProcessingOptions` (feature flag), narrow ports (`ITradeRepository`, `IUnitOfWork`, `IPortfolioReadStore`, …) | EF Core, SQL, HTTP |
| **Infrastructure** | `TradeIngestionDbContext`, fluent configurations (all constraints/indexes), `TradeRepository` (the locking read), `EfUnitOfWork` (transaction + execution strategy + exception translation), read stores, migrations, embedded SQL scripts | Business decisions |
| **Api** | Thin controllers that map outcomes to HTTP codes, request/response contracts, strict timestamp converter, correlation id, ProblemDetails, health checks, Swagger | Business logic |

**Repository vs direct EF Core.** I used narrow, purpose-built ports rather than either a generic `IRepository<T>`
or EF Core directly in the Application layer:

- The single most important write operation is a **locking read** (`GetForUpdateAsync`, using `UPDLOCK, HOLDLOCK`).
  That is a persistence-specific concern. Naming it explicitly in a port documents the concurrency contract, and keeps
  SQL hints out of the use case.
- Generic repositories would hide exactly the details (locking, projections, `NOT EXISTS` queries) that matter here.
- Read paths (`IPortfolioReadStore`, `ITradeHistoryReader`) are query objects that return projections, not entities.
  This is effectively a light CQRS split without the ceremony.
- The Application layer stays unit-testable without a database. The domain rules carry most of the logic, and they
  are pure.

**Service-layer orchestration** stays small. `TradeIngestionService.SubmitAsync` is about 60 lines: validate, look up,
then a transactional decision `switch` that delegates to the aggregate. Classification lives in `Trade`, not the service.

---

## 3. Database design

```
Accounts (Id PK, AccountCode UQ varchar(32), Name, BaseCurrency char(3))
Instruments (Id PK, Isin UQ char(12), Symbol varchar(16), Name, Currency char(3))
InstrumentPrices (Id PK, InstrumentId FK, PriceDate date, ClosePrice decimal(19,6) CK>0, Currency char(3) CK='USD',
                  UQ (InstrumentId, PriceDate DESC) INCLUDE ClosePrice)
Trades (Id PK bigint, ExternalRef UQ varchar(64), CurrentVersionNumber CK>=1, LatestAsOfUtc datetime2(7),
        CreatedAtUtc, UpdatedAtUtc, RowVersion rowversion)
TradeVersions (Id PK bigint, TradeId FK, VersionNumber, AccountId FK, InstrumentId FK, Side varchar(4) CK IN('BUY','SELL'),
               Quantity decimal(18,6) CK>0, Price decimal(19,6) CK>0, TradeDate date, AsOfUtc datetime2(7),
               ReceivedAtUtc datetime2(7), CorrelationId varchar(64),
               UQ (TradeId, VersionNumber) INCLUDE (...), UQ (TradeId, AsOfUtc), IX (AccountId, AsOfUtc) INCLUDE TradeId)
```

Design points:

- **Trades vs TradeVersions.** `Trades` is the idempotency anchor: one row per `ExternalRef`, holding the pointer to
  the current version, the `AsOf` watermark and a `rowversion`. `TradeVersions` is **append-only**. Every economic
  version is kept forever, so the audit trail *is* the data model rather than a side log that can drift.
- **Surrogate keys** (`int`/`bigint` identity) for narrow foreign keys and clustered indexes. Business keys (`AccountCode`,
  `Isin`, `ExternalRef`) have unique indexes. `bigint` on the high-volume tables.
- **`varchar` for ASCII identifiers.** This halves storage and index size versus `nvarchar`, and (see §10) the
  parameter type must match the column type to stay SARGable.
- **Decimal precision.** `Quantity decimal(18,6)` supports fractional shares up to 10^12. `Price decimal(19,6)` covers
  sub-penny prices. Their product is exactly representable in `decimal(38,12)`. Money outputs are `decimal(24,2)`.
  No floating point anywhere. The API rejects more than 6 decimal places instead of letting SQL Server round silently.
- **UTC.** All instants are `datetime2(7)` holding UTC (normalised from the mandatory input offset). A model-wide EF
  value converter marks them `DateTimeKind.Utc` on read. Dates without time (`TradeDate`, `PriceDate`) are `date` / `DateOnly`.
- **Defence in depth.** CHECK constraints for side, positive quantity/price, version ≥ 1 and USD prices. Foreign keys
  with `RESTRICT`, because nothing cascades-deletes audit data.
- **Why not SQL Server temporal tables?** Temporal tables record *system* time (when the row changed in this database).
  Corrections are ordered by *business/source* time (`AsOf`), which can arrive out of order. Explicit versions keyed by
  `AsOf` model that correctly. Temporal tables would also make "ignore a stale event" impossible to express.
- **Migrations.** `InitialSchema` (tables, constraints, indexes) and `AddGetPortfolioSnapshotProcedure` (runs the
  embedded `Database/Scripts/GetPortfolioSnapshot.sql`, so there is one source of truth). Future changes to the
  procedure ship as a **new** migration that re-runs the (CREATE OR ALTER) script.

---

## 4. Idempotency strategy

The idempotency key is `ExternalRef`. It is enforced **by the database** (`UX_Trades_ExternalRef`), not just by
application checks.

Decision table (`Trade.Evaluate`, pure domain logic, unit tested):

| Incoming AsOf vs latest accepted | Content vs current version | Decision | HTTP | Effect |
|---|---|---|---|---|
| ExternalRef unseen | n/a | Created | **201** + `Location` | Trade + version 1 |
| equal | equal | Duplicate | **200** `DuplicateIgnored` | none |
| equal | different | ConflictingPayload | **409** | none (alertable) |
| later | different | Correction | **200** `CorrectionApplied` | version n+1, watermark moves |
| later | equal | NoEconomicChange | **200** `DuplicateIgnored` | watermark moves, no version |
| earlier | equals a stored version at that AsOf | Duplicate (replay) | **200** `DuplicateIgnored` | none |
| earlier | anything else | Stale | **200** `StaleIgnored` | none |

Why 200 for duplicate/stale rather than 4xx? An upstream system delivering at least once *will* retry, and replays
are normal in that world. Returning 2xx with an explicit `outcome` means retries are safe and don't page anyone. The
body still makes the outcome unambiguous. Real anomalies (409 conflict, 422 bad reference data) are 4xx.

Content comparison uses `TradeDetails` record equality. `decimal` equality is numeric, so `185.40` and `185.4` are
equal. A payload hash was rejected because it would be sensitive to formatting and scale.

---

## 5. Correction and audit strategy

- A correction **never updates a version**. It appends version n+1 and moves `Trades.CurrentVersionNumber` and
  `LatestAsOfUtc` in the **same transaction**.
- Each version stores `AsOfUtc` (source knowledge time), `ReceivedAtUtc` (our processing time) and the request
  `CorrelationId`. You can answer "what did the source say, when did we learn it, and which request delivered it?"
- `GET /api/trades/{externalRef}` returns the full history with `isCurrent` flags. The end-to-end test asserts that
  v1 (120 @ 185.40) is preserved byte for byte after the correction.
- Unique `(TradeId, VersionNumber)` and `(TradeId, AsOfUtc)` guarantee a gapless, strictly ordered history, even if
  application logic were bypassed.
- Historical snapshots read the version effective at the time (§8). Audit and reporting therefore use the same rows.

---

## 6. Concurrency handling

The race to prevent: two requests for the same `ExternalRef` both read "not found" (or "version 1") and both write.

1. **Pessimistic key lock.** Inside a transaction, the repository runs:
   ```sql
   SELECT * FROM dbo.Trades WITH (UPDLOCK, HOLDLOCK) WHERE ExternalRef = @externalRef   -- @externalRef varchar(64)
   ```
   `UPDLOCK` allows only one holder of the intent-to-modify lock on the key. `HOLDLOCK` gives serializable semantics
   for this predicate only. When the row does not exist yet, SQL Server takes a **key-range lock** on the gap in
   `UX_Trades_ExternalRef`, so a concurrent first insert of the same ref **waits** instead of racing. After the first
   commits, the waiter reads the committed row and classifies itself as a duplicate. Requests for *different* refs
   lock different keys and do not block each other.
2. **Isolation level: READ COMMITTED, not SERIALIZABLE.** The only read that needs serializable semantics gets them
   through the hint. A blanket SERIALIZABLE transaction would range-lock every read (including reference data) and
   materially increase deadlock risk. The design also works under READ_COMMITTED_SNAPSHOT, because `UPDLOCK` forces a
   locking read of the latest committed row.
3. **Database backstops.** If application logic is ever bypassed or wrong, `UX_Trades_ExternalRef`,
   `UX_TradeVersions_Trade_Version` and `UX_TradeVersions_Trade_AsOf` reject the second write, and `rowversion` on
   `Trades` catches lost updates. `EfUnitOfWork` translates errors 2601/2627 and `DbUpdateConcurrencyException` into
   `PersistenceConflictException`.
4. **Bounded re-evaluation.** `TradeIngestionService` retries a `PersistenceConflictException` up to
   `TradeProcessing:MaxConflictRetries` times. Each retry re-reads the committed state, so it normally resolves to
   `DuplicateIgnored`. If retries are exhausted, the client gets **409** with "safe to retry".
5. **Transient faults.** EF Core's `SqlServerRetryingExecutionStrategy` wraps the whole unit of work, including
   deadlock victims (1205). The change tracker is cleared on every attempt. This is safe because the operation is
   idempotent by construction.

Verified by `ConcurrencyTests`, which runs over real HTTP against real SQL Server. 25 simultaneous identical first
submissions produce exactly one `Created` and one row. Ten simultaneous corrections fired in reverse `AsOf` order end
with the latest `AsOf` current, a gapless version sequence, and `AsOf` strictly increasing with version number.

---

## 7. Cost-basis assumptions

**Method: holding-period weighted average cost** (implemented once in `AverageCostCalculator`, mirrored in SQL):

- Per instrument, effective trades are ordered by `(TradeDate, TradeId)`. `TradeId` is first-seen order, which
  breaks ties within a day deterministically.
- **BUY**: quantity += q, and q × price is added to the open cost pool.
- **SELL**: quantity −= q. Cost is relieved at the current average, so **the average does not change**. Realised P/L
  is not reported (out of scope).
- When running quantity reaches **≤ 0**, the holding period ends and the pool **resets**. Only BUYs after the last
  flat or short point make up the average.
- `AverageUnitCost = round(open buy cost / open buy quantity, 6)`, half away from zero. It is `null` for a net short
  position (there is no long cost basis; the status is `ShortPosition`).
- Prices are assumed to be USD. There are no fees, commissions or corporate actions.

**Why this rather than a strict moving average?** Within a holding period, a strict moving average is path-dependent:
`BUY 100 @ 10, SELL 50, BUY 50 @ 20` gives 15.00 under a strict moving average and 13.33 under this method. A strict
moving average needs a sequential (recursive) computation. In SQL that means a recursive CTE over a materialised
ordered set, plus per-step rounding that must match C# exactly. The holding-period method:

- is computable with ordinary set-based window functions, giving exact parity with C#;
- is deterministic and independent of order within a holding period, which makes corrections and back-dated trades
  cheap to re-evaluate;
- still handles the most misleading case (going flat and re-entering at a new price, where a naive all-time average
  would be badly wrong).

This is a documented simplification. In production I would maintain a persisted position ledger, updated
incrementally on ingestion, where the true moving average is cheap to compute sequentially (§18).

---

## 8. Snapshot calculation

`GET /api/accounts/{accountId}/snapshot?date=D`:

1. **Knowledge cutoff** = `D + 1 day 00:00 UTC` (exclusive). Default `D` = today (UTC). Future dates are rejected (400),
   because they would present today's partial knowledge as final.
2. **Version selection (one row per trade).** For every trade with any version on the account before the cutoff,
   take the version with the highest `VersionNumber` among those with `AsOfUtc < cutoff`. Only *then* keep it if its
   `AccountId` is this account and `TradeDate <= D`. Filtering before selecting would let an older version leak back in
   when a correction moved the trade to another account or a later date. That is the classic double-count bug, and
   there is a test for it.
3. **Positions** per §7. Closed positions (net 0) are omitted.
4. **Price** = latest `ClosePrice` with `PriceDate <= D`. `priceDate` is returned.
5. **Valuation:** `MarketValue = round(NetQty × Price, 2)`, `CostBasis = round(NetQty × AvgCost, 2)`,
   `UnrealizedPnl = MarketValue − CostBasis`, so components always reconcile to the cent.
6. **Totals** sum the rounded position values and ignore nulls. `unpricedPositionCount` / `isValuationComplete`
   tell the consumer whether the total is complete.

Effective-dating example from the seed data (ACC-002, MSFT): `SEED-2006` was booked as 20 shares and corrected to 25
with `AsOf 2025-03-05`. The snapshot for **2025-03-01** shows 50 MSFT (avg 406.666667). The snapshot for **2025-03-10**
shows 55 (avg 407.692308). Both are asserted in tests, in C# and in SQL.

---

## 9. SQL stored procedure design

`Database/Scripts/GetPortfolioSnapshot.sql` defines `dbo.GetPortfolioSnapshot @AccountId varchar(32), @SnapshotDate date`.

Pipeline (one set-based statement, CTEs):

| CTE | Purpose |
|---|---|
| `CandidateTrades` | `DISTINCT TradeId` with any version on the account before the cutoff (index range seek) |
| `RankedVersions` | All versions of those trades before the cutoff, on **any** account: `ROW_NUMBER() OVER (PARTITION BY TradeId ORDER BY VersionNumber DESC)` |
| `EffectiveTrades` | `VersionRank = 1`, **then** `AccountId = @AccountKey AND TradeDate <= @SnapshotDate`; signed quantity |
| `SequencedTrades` | `ROW_NUMBER()` and running `SUM(SignedQuantity) OVER (… ORDER BY TradeDate, TradeId ROWS UNBOUNDED PRECEDING)` |
| `HoldingPeriods` | `LastFlatSeq = MAX(CASE WHEN RunningQuantity <= 0 THEN Seq ELSE 0 END) OVER (PARTITION BY InstrumentId)` |
| `Positions` | Net quantity, open buy quantity and cost (buys after `LastFlatSeq`), `HAVING net <> 0` |
| `CostedPositions` | Average unit cost, rounded to 6 dp; `NULL` when short |
| `ValuedPositions` | `OUTER APPLY (SELECT TOP (1) … ORDER BY PriceDate DESC)` for the carried-forward price; rounded money values |

Then two result sets: (1) positions, (2) exactly one summary row with `AccountTotalMarketValue`, `TotalUnrealizedPnl`,
`PortfolioValue`, `PositionCount`, `UnpricedPositionCount`, `IsValuationComplete`. `PortfolioValue` equals
`AccountTotalMarketValue` today because there is no cash ledger. It is a separate column so the contract stays stable
when cash balances are added.

Design notes:

- **Why one table variable?** The procedure returns two result sets from **one** evaluation. Materialising positions
  once (cardinality is the number of instruments held, which is small) gives statement-level read consistency. The two
  result sets cannot disagree. Re-running the CTE chain twice would cost double, and could see different data under
  READ COMMITTED. No other temp objects, no cursors, no loops, no dynamic SQL.
- **Parameters** are strongly typed (`varchar(32)`, `date`). The cutoff is computed once into a `datetime2(7)` variable.
- **Errors:** `THROW 50001` for missing parameters and `50002` for an unknown account. The C# API maps the same
  condition to 404.
- **Decimal arithmetic.** Operands are cast to bounded precisions so SQL Server never has to cut the result scale
  below the rounding point. `CAST(qty AS decimal(18,6)) * price(19,6)` is `decimal(38,12)`, which is exact.
  `CAST(cost AS decimal(30,12)) / CAST(qty AS decimal(24,6))` keeps 14 dp before `ROUND(…, 6)`. SQL `ROUND` and C#
  `MidpointRounding.AwayFromZero` round the same way. Overflow raises an error rather than truncating silently.
- **Agreement with the application** is proven, not asserted. `SnapshotSqlParityTests` compares every field of every
  position and every total on four dates of the ACC-002 portfolio (round trip, partial sell, late correction, missing
  price, carried-forward price). It also covers the moved-account case.

---

## 10. Indexing, performance and SARGability

| Index | Access path it serves |
|---|---|
| `UX_Trades_ExternalRef` (unique) | Ingestion: equality seek under `UPDLOCK, HOLDLOCK`, so the key-range lock covers exactly one key or gap. History endpoint. |
| `IX_TradeVersions_Account_AsOf (AccountId, AsOfUtc) INCLUDE (TradeId)` | Snapshot entry: **range seek** `AccountId = @a AND AsOfUtc < @cutoff`, yielding candidate trade ids without touching the base table |
| `UX_TradeVersions_Trade_Version (TradeId, VersionNumber) INCLUDE (AsOfUtc, AccountId, InstrumentId, Side, Quantity, Price, TradeDate)` | Snapshot: one **covering seek** per candidate trade, rows already ordered by version (no sort for `ROW_NUMBER`). Also uniqueness of version numbers. |
| `UX_TradeVersions_Trade_AsOf` (unique) | Integrity: one version per AsOf |
| `UX_InstrumentPrices_Instrument_PriceDate (InstrumentId, PriceDate DESC) INCLUDE (ClosePrice)` | "Latest price on or before date" is a single-row `TOP (1)` seek |
| `UX_Accounts_AccountCode`, `UX_Instruments_Isin` | Business-key equality seeks |

**Measured** (SQL Server 2022, 220,008 trade versions across 2,000 accounts, 10% with corrections, statistics
updated): the actual plan for an account with about 100 trades is an index seek on `UX_Accounts_AccountCode`, then a
range seek on `IX_TradeVersions_Account_AsOf`, then a nested-loop seek into `UX_TradeVersions_Trade_Version` per
candidate trade, a PK seek on `Instruments`, and a `TOP 1` seek on the price index. **314 logical reads on
`TradeVersions`, 6 on `InstrumentPrices`, about 9 ms elapsed.** On the 10-row seed database the optimiser instead scans
the covering index, which is the correct choice at that size. Plans must be judged at realistic volume.

The only sort left is `(InstrumentId, TradeDate, TradeId)` for the running sum. It is bounded by the account's trade
count. At very high per-account volumes this is where a persisted position table pays off (§11, §18).

**SARGability rules applied:**
- No functions on indexed columns. The cutoff is `DATEADD(DAY, 1, @SnapshotDate)` computed **once into a variable**,
  and the predicate is `AsOfUtc < @KnowledgeCutoffUtc`. Writing `CAST(AsOfUtc AS date) <= @SnapshotDate` would force a
  scan.
- **Parameter types match column types.** The idempotency lookup passes `@externalRef` explicitly as `varchar(64)`.
  An `nvarchar` parameter (the .NET default for strings) against a `varchar` column causes `CONVERT_IMPLICIT` on the
  column side, which degrades the seek and widens the key-range lock. EF Core LINQ queries get this for free because
  the model declares `IsUnicode(false)`. The raw SQL path does it by hand. The stored procedure parameters are typed
  `varchar(32)` / `date` for the same reason.
- Range predicates (`<`, `<=`) on leading or second index keys after equality predicates.
- `EXISTS`/`NOT EXISTS` instead of `IN` with nullable subqueries (EF read path).

---

## 11. Partitioning and archival at higher volumes

At the scale of tens or hundreds of millions of versions:

1. **Persisted end-of-day positions** (`AccountPositionsDaily`: account, instrument, date, quantity, open buy qty/cost)
   maintained by a nightly job, or incrementally on ingestion. A snapshot then reads the last persisted day before `D`
   and applies only the delta trades. That turns O(trades in history) into O(trades since last close). A correction
   back-dated before a persisted day invalidates that day forward. A small "dirty from date" queue handles this.
2. **Partition `TradeVersions` by `TradeDate` (monthly)**, with aligned indexes on a partition scheme. Snapshot queries
   on recent dates touch recent partitions, and old partitions can be switched out with metadata-only operations.
   `TradeDate` rather than `AsOfUtc`, because retention and regulatory archival are defined by trade date. The
   `(AccountId, AsOfUtc)` index would become partition-aligned with `TradeDate` added as a key or include column.
3. **Archival.** Audit versions are never deleted inside the retention period (typically 7+ years for trade records).
   Closed periods move to an archive filegroup or table with a **clustered columnstore index** (10x compression,
   excellent for audit and analytics scans). They stay queryable through a view, and the archived partitions can be
   made read-only.
4. **Hot/cold split.** `Trades` (one narrow row per trade) stays small and hot for the idempotency lookup.
   `TradeVersions` history can live on cheaper storage.
5. **Read scale-out.** Snapshot traffic goes to an Always On readable secondary (`ApplicationIntent=ReadOnly`), so
   reporting load never contends with ingestion locks.

---

## 12. Feature flag strategy and safe rollout

`TradeProcessing:EnableVersionedCorrections` (bound to `TradeProcessingOptions`, read via `IOptionsMonitor` per
request, so a configuration reload flips it without a restart).

| Flag | Behaviour |
|---|---|
| `true` (default) | Full behaviour: duplicates ignored, corrections versioned, stale ignored, conflicts rejected. |
| `false` (legacy) | First submission wins. **Any** re-submission of an existing ExternalRef returns **409 `ExternalRefAlreadyExists`** and nothing is applied. The decision the new logic *would* have taken is logged (event 1004, `ShadowDecision`). |

Safety properties that hold **in both modes**: the unique constraints and the key lock still apply, so nothing can be
double counted whichever way the flag is set (tested in `FeatureFlagTests`).

Rollout into an existing production system (expand, then shadow, then enable, then contract):

1. **Expand schema first.** Deploy the migration (new tables and indexes are additive) with an idempotent script
   reviewed by the DBA (`dotnet ef migrations script --idempotent`). Back-fill existing trades as version 1 rows.
2. **Deploy code with the flag OFF.** Behaviour is identical to legacy. Shadow logs show the classification for every
   re-submission. Compare them against operations' expectations for a week: how many would have been corrections?
   Any `ConflictingPayload`?
3. **Enable for a canary.** Enable on one instance, or for one upstream source (with `Microsoft.FeatureManagement`
   filters, a natural next step), with dashboards on outcome rates. Mixed-mode instances are safe: the database
   invariants hold either way. The worst case is a correction rejected by a legacy-mode instance, which the upstream
   retries.
4. **Enable everywhere.** Keep the flag as a **kill switch** for one release cycle, then remove the legacy branch.
5. **Rollback** is a configuration change, not a deployment. Versions written while enabled remain valid audit data.

---

## 13. Logging and monitoring

- `Microsoft.Extensions.Logging` with **source-generated `[LoggerMessage]` events** (no boxing or allocation when
  disabled, and stable `EventId`s to alert on):

  | EventId | Level | Event | Structured properties |
  |---|---|---|---|
  | 1000 | Info | Submission processed | `ExternalRef`, `ProcessingOutcome`, `TradeId`, `TradeVersion`, `Decision` |
  | 1001 | Warn | Validation failed | `ExternalRef`, `InvalidFields` |
  | 1002 | Warn | Reference data rejected | `ExternalRef`, `AccountId`, `Isin` |
  | 1003 | Warn | Concurrency conflict, retrying | `ExternalRef`, `Attempt` |
  | 1004 | Info | Shadow decision (flag off) | `ExternalRef`, `ShadowDecision` |
  | 2000 | Warn | Snapshot has unpriced positions | `AccountId`, `SnapshotDate`, `UnpricedPositionCount` |
  | 9000/9001 | Error/Warn | Unhandled / handled exception | `HttpMethod`, `Path` |

- **Scopes**: `CorrelationId` (from `X-Correlation-ID`, validated against `^[A-Za-z0-9\-_.]{1,64}$` to prevent log
  injection, generated if absent, echoed on the response, stored on each version), plus `ExternalRef`, `AccountId`
  and `InstrumentId` for the ingestion unit of work.
- **Format**: JSON console in non-Development (`IncludeScopes`, UTC timestamps). This is ready for any log shipper.
  The single-line simple formatter is used in Development.
- **Sensitive data**: quantities, prices and payloads are **never logged**. Identifiers and outcomes are enough to
  trace a trade, and the amounts live in the audit table behind access control.
- **Health**: `/health` includes a database connectivity check.
- **What to alert on in production**: any `ConflictingPayload` (data quality), the `RetryingAfterConflict` rate
  (contention), 5xx rate, unpriced positions in end-of-day snapshots, and p99 latency. Next step: OpenTelemetry
  metrics (`System.Diagnostics.Metrics` counter per `ProcessingOutcome`) and traces. ASP.NET Core already emits W3C
  trace ids, which appear in ProblemDetails as `traceId`.

---

## 14. Error handling

- **Expected outcomes are results, not exceptions.** Validation failures, unknown reference data, duplicates, stale
  events and conflicts are values of `TradeSubmissionOutcome`, and the controller maps them to HTTP. This avoids
  exception-driven control flow.
- **Unexpected failures** go to a single `IExceptionHandler` (`GlobalExceptionHandler`). It logs once with the full
  exception and returns RFC 7807 `ProblemDetails`, with no stack trace or internals.
- **Every** error response (validation, 404, 409, 422, 500) is `application/problem+json` and includes `correlationId`
  and `traceId` (via `CustomizeProblemDetails`), so a support ticket maps straight to the logs.

| Status | When |
|---|---|
| 201 | New trade |
| 200 | Duplicate ignored, correction applied, stale ignored (body `outcome` says which) |
| 400 | Malformed JSON, missing/invalid fields, timestamp without offset, future snapshot date |
| 404 | Unknown account (snapshot) or ExternalRef (history) |
| 409 | Conflicting payload; ExternalRef exists while the flag is off; concurrency retries exhausted (safe to retry) |
| 422 | Unknown account/instrument, symbol does not match ISIN |
| 499 | Client aborted the request (logged at Information, no body) |
| 500 | Anything unexpected |

---

## 15. Testing approach

55 xUnit tests, all executed and passing (see README "Verification status").

| Layer | Tests | What they prove |
|---|---|---|
| Domain unit | `TradeAggregateTests` (8), `AverageCostCalculatorTests` (9), `PositionValuationTests` (4) | Every row of the idempotency decision table; cost rules (weighting, sells, flat reset, short, ordering, rounding); valuation and rounding mode identical to SQL `ROUND` |
| Application unit | `TradeSubmissionValidatorTests` (12) | Normalisation, UTC conversion, all-errors-at-once, decimal places, ISIN check digit |
| **End-to-end** (required) | `TradeLifecycleEndToEndTests` | Over HTTP against SQL Server: new (201) → duplicate (200, quantity still 120, 1 version) → correction (200, v2) → snapshot (150 / 180.00 / 186.00 / 27,900.00 / 900.00) → history preserved (v1 intact) → **stored procedure returns the same numbers** → point-in-time (day before is empty) |
| Concurrency | `ConcurrencyTests` | 25 parallel identical submissions; 10 parallel out-of-order corrections |
| SQL parity | `SnapshotSqlParityTests` | SP equals API on 4 dates; effective-dated correction; moved-account double count; SP error codes; `Indexes.sql` is a no-op against the migrated schema (cannot drift) |
| Feature flag | `FeatureFlagTests` | Legacy mode rejects re-submissions with 409 and never applies them |
| API contract | `ApiBehaviourTests` | Stale, conflict (problem+json), offset-less timestamp, 400 with all errors, 422, malformed JSON, correlation id round trip and persistence, future date, 404 |

**Why real SQL Server, and not SQLite or the EF in-memory provider?** The behaviours that matter most do not exist in
substitutes. SQLite has no `UPDLOCK`/`HOLDLOCK` and no key-range locking (it locks the whole database file), no
`rowversion`, different `decimal` semantics (it stores REAL/TEXT, so `decimal(38,12)` arithmetic can't be verified),
no T-SQL (the stored procedure can't run at all) and different unique-violation error codes. The in-memory provider
has no transactions or constraints. Tests on those would pass while production double counts.

The fixture creates a uniquely named database per test class through the **real startup path** (migrations and seed),
so migrations are tested too, then drops it. Classes run in parallel on separate databases. For CI: run SQL Server as
a service container (`mcr.microsoft.com/mssql/server:2022-latest`) and set `TRADEINGESTION_TEST_SQLSERVER`.
Testcontainers is a drop-in alternative if per-run containers are preferred.

---

## 16. Trade-offs and limitations

- **Average cost is holding-period weighted, not a strict moving average** (§7). It is documented, tested, and has a
  clear evolution path.
- **USD only**; a CHECK constraint blocks non-USD prices. No FX conversion.
- **No cancellations.** A trade can be corrected but not cancelled. The natural extension is a version with
  `Status = Cancelled` (quantity contributes zero), selected by the same `ROW_NUMBER` logic.
- **No realised P/L, fees, commissions, accrued interest, cash or corporate actions** (splits would need a
  quantity/price adjustment factor table).
- **Same-day ordering** of trades uses first-seen `TradeId`. Execution timestamps would be better if upstream
  provided them.
- **Snapshot computed on read.** That is fine for interactive use and the measured cost is small (§10), but end-of-day
  reporting across thousands of accounts should use persisted positions (§11).
- **Reference data** is seeded, not managed. There is no instrument or account API by design.
- **Migration embeds the SP script.** If the script file later changes, the old migration would deploy the new text
  on a fresh database. This is mitigated by the convention that procedure changes always ship as a new migration,
  and the script is `CREATE OR ALTER`.
- **Authentication/authorisation, rate limiting and HTTPS termination** are not implemented. They are assumed to sit
  at the gateway or in platform middleware, and are listed in §18.
- **Validation is hand-written** rather than FluentValidation. With a single command, a dependency was not justified.
  It would be with more commands.
- **docker-compose was not executed** in my verification environment (Docker engine unavailable). Everything else was
  verified against SQL Server 2022.

---

## 17. .NET Framework 4.8 versus .NET 8 integration

Many financial platforms still run large .NET Framework 4.8 estates. How this service fits:

**Option A (recommended): run it as a separate .NET 8 service.** Legacy 4.8 applications call the HTTP API, or better,
publish trade events to a queue (MSMQ, RabbitMQ or Azure Service Bus) that this service consumes with the same
idempotency logic. The shared contract is the API or message schema plus the database. This is a strangler-fig
migration: no big bang, independent deployment, and the new service gets .NET 8 performance and long-term support.
The stored procedure is consumable *as is* from 4.8 code (`System.Data.SqlClient` / Dapper / EF6
`Database.SqlQuery`), so legacy reporting can adopt it immediately.

**Option B: host the logic inside a 4.8 application.** What would change:

| Concern | .NET 8 (this solution) | .NET Framework 4.8 equivalent |
|---|---|---|
| Web framework | ASP.NET Core controllers, built-in DI | ASP.NET Web API 2 / MVC 5 + Autofac/Unity/SimpleInjector |
| ORM | EF Core 8 (`FromSqlRaw` composable, `ExecuteUpdate`, DateOnly) | EF6: the locking read becomes `Database.SqlQuery<Trade>("… WITH (UPDLOCK, HOLDLOCK)…")` or Dapper. No `DateOnly` (use `DateTime` with `.Date`). Migrations via EF6 `Add-Migration`. |
| Config & flags | `IOptionsMonitor<T>`, JSON + env vars, reload on change | `ConfigurationManager.AppSettings` / `web.config` transforms. Live reload needs a custom provider or a `Microsoft.Extensions.Configuration` back-port (it supports net461+). |
| Logging | `Microsoft.Extensions.Logging` + source generators | Serilog / log4net / NLog. `Microsoft.Extensions.Logging` also runs on 4.8 (netstandard2.0), but without the source generator. |
| Error handling | `IExceptionHandler` + ProblemDetails | Web API 2 `IExceptionHandler`/`ExceptionFilterAttribute`, ProblemDetails built by hand |
| JSON | System.Text.Json + custom strict converter | Newtonsoft.Json with `DateParseHandling.DateTimeOffset` and a custom converter for the offset rule |
| Async | Async end to end, `CancellationToken` everywhere | Same patterns work, but watch `SynchronizationContext` deadlocks (`ConfigureAwait(false)` in libraries) |
| Testing | `WebApplicationFactory` in-process | OWIN `TestServer` (Microsoft.Owin.Testing) or an IIS Express-hosted test run |

**Sharing code:** Domain and Application could multi-target `netstandard2.0;net8.0` to be shared with 4.8. That needs
`DateOnly` replaced (or polyfilled), and the `[GeneratedRegex]` / `[LoggerMessage]` generators replaced on the
netstandard target. The domain rules have no dependencies precisely so this stays possible.

---

## 18. Evolving this into a production financial platform

1. **Event-driven ingestion.** Consume trades from Kafka or Service Bus with the same `ExternalRef` + `AsOf`
   idempotency, so the HTTP endpoint becomes one adapter among several. Write an **outbox** row in the same transaction
   as each version and publish `TradeBooked` / `TradeCorrected` events reliably to downstream risk and settlement.
2. **Persisted position ledger** updated incrementally from the outbox. This enables a true moving average, realised
   P/L, daily snapshots and fast reporting (§11). The on-read calculation then becomes a reconciliation check.
3. **Trade lifecycle:** cancellations, amendments with reason codes, allocations, fees, settlement status, and
   corporate actions via an adjustment-factor table.
4. **Multi-currency:** FX rates table (date, pair, rate, source), instrument currency, valuation in base currency with
   the FX date exposed like `priceDate`. Remove the USD CHECK constraint.
5. **Price governance:** multiple sources with priority, a staleness threshold per asset class (flag `StalePrice`
   instead of carrying forward forever), and an official end-of-day price lock.
6. **Security:** OAuth2/OIDC with per-account authorisation (a user sees only entitled accounts), mTLS for
   system-to-system ingestion, rate limiting, payload size limits and secrets in Key Vault.
7. **Observability:** OpenTelemetry traces and metrics (outcome counters, ingestion latency, lock waits), SLOs, and
   dashboards for conflict and stale rates per upstream source. These are a direct measure of upstream data quality.
8. **Data management:** partitioning and columnstore archival (§11), RCSI for reader/writer isolation, readable
   secondaries for reporting, and retention policies aligned with regulatory requirements (e.g. MiFID II record keeping).
9. **Operational tooling:** a reviewed workflow to resolve `ConflictingPayload` (accept one side as a new version with
   an operator correlation id, never by editing rows), plus replay tooling that re-feeds an upstream day safely,
   which idempotency already makes possible.
