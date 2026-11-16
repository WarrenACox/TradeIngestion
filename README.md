# Trade Ingestion and Portfolio Snapshot Service

ASP.NET Core 8 Web API that ingests trades **idempotently** (duplicates ignored, later-`AsOf` corrections applied as
new versions, stale events ignored, full audit history kept) and produces **point-in-time portfolio snapshots** in
USD using average cost. A set-based stored procedure, `dbo.GetPortfolioSnapshot`, produces the same numbers from SQL,
and an integration test checks that it agrees with the API.

Design rationale, trade-offs and production considerations are in **[SOLUTION.md](SOLUTION.md)**. A shorter walkthrough
of what was built, how the code works and the architecture trade-offs is in
**[docs/BUILD_AND_TRADEOFFS.md](docs/BUILD_AND_TRADEOFFS.md)**.

```
TradeIngestion/
├── src/
│   ├── TradeIngestion.Api/             HTTP: controllers, contracts, correlation id, ProblemDetails, Swagger
│   ├── TradeIngestion.Application/     Use cases, validation, ports (interfaces), feature flag options
│   ├── TradeIngestion.Domain/          Trade aggregate (dup/correction rules), average-cost + valuation rules
│   └── TradeIngestion.Infrastructure/  EF Core 8 + SQL Server: DbContext, locking repository, migrations
├── tests/TradeIngestion.Tests/         xUnit: unit tests + SQL Server integration/E2E/concurrency tests
├── Database/Scripts/
│   ├── GetPortfolioSnapshot.sql        The SQL artifact (deployed by migration from this file)
│   ├── SeedData.sql                    Idempotent reference data + example trades
│   ├── Indexes.sql                     DBA reference copy of all supporting indexes (idempotent)
│   └── ResetDemoData.sql               Demo/dev only: clears ACC-001 trades so the demo is repeatable
├── scripts/Demo.ps1                    Runs and asserts the four assessment scenarios
├── docs/BUILD_AND_TRADEOFFS.md         What was built, code walkthrough, architecture trade-offs
├── docs/AI_USAGE.md                    How AI assistance was used
├── docker-compose.yml                  Local SQL Server 2022
└── TradeIngestion.sln
```

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 8.0+ | Projects target `net8.0`. Any SDK that can target net8.0 works (verified with SDK 9.0.101 + .NET 8 runtime). |
| SQL Server | 2019+ | Docker (`docker compose up -d`) **or** any local/remote instance. Developed and tested on SQL Server 2022. |
| PowerShell | 5.1 or 7+ | For `scripts/Demo.ps1`. |
| sqlcmd | any | Used by the demo to reset data. Not needed with `-UseDocker` (uses the container's sqlcmd). |
| dotnet-ef | 8.x | Optional, only for running migrations manually: `dotnet tool install -g dotnet-ef --version 8.*` |

## 1. Start SQL Server

```powershell
docker compose up -d          # SQL Server 2022 on localhost:1433, sa / TradeIngestion!Dev2025
docker compose ps             # wait for "healthy"
```

Override with `MSSQL_SA_PASSWORD` / `SQL_PORT` environment variables if needed. To use an existing SQL Server instead,
skip this step and point the connection string at it.

## 2. Configure the connection string

The connection string name is `TradeIngestion`. `appsettings.Development.json` points at the docker-compose instance.
Override it without editing files (recommended):

```powershell
# Environment variable (PowerShell)
$env:ConnectionStrings__TradeIngestion = "Server=localhost;Database=TradeIngestion;Integrated Security=True;TrustServerCertificate=True"

# or user-secrets (kept out of source control)
dotnet user-secrets set "ConnectionStrings:TradeIngestion" "<connection string>" --project src/TradeIngestion.Api
```

`appsettings.json` (non-Development) deliberately has an **empty** connection string. The service fails fast at
startup if none is supplied, so production never silently connects to a developer database.

## 3. Apply migrations

**Automatic (Development):** `Database:ApplyMigrationsOnStartup = true` in `appsettings.Development.json`. The API
creates the database, the schema, all indexes and `dbo.GetPortfolioSnapshot` on first start.

**Manual / CI / production:**

```powershell
$env:TRADEINGESTION_CONNECTION = "<connection string>"   # used by the design-time factory
dotnet ef database update --project src/TradeIngestion.Infrastructure

# or generate a reviewable, idempotent script for a DBA / release pipeline:
dotnet ef migrations script --idempotent --project src/TradeIngestion.Infrastructure -o artifacts/migrate.sql
```

## 4. Seed data

**Automatic (Development):** `Database:SeedOnStartup = true` runs `Database/Scripts/SeedData.sql` (idempotent) at startup.

**Manual:**

```powershell
sqlcmd -S localhost,1433 -U sa -P "TradeIngestion!Dev2025" -C -d TradeIngestion -i Database/Scripts/SeedData.sql
```

Seeded data: accounts `ACC-001` (empty demo account) and `ACC-002` (example history), instruments AAPL/MSFT/NVDA,
AAPL close **186.00** on 2025-03-01, and trades `SEED-2001..2007` on ACC-002. These include a round trip, a partial
sell, a correction known only after 2025-03-01, and an instrument with no price.

## 5. Run the API

```powershell
dotnet run --project src/TradeIngestion.Api
```

- Swagger UI: <http://localhost:5080/swagger> (enabled in Development via `Swagger:Enabled`)
- Health: <http://localhost:5080/health>

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/trades` | Submit a trade. 201 Created / 200 DuplicateIgnored, CorrectionApplied, StaleIgnored / 400 / 409 / 422 / 500 |
| GET | `/api/trades/{externalRef}` | Trade with its full version history (audit view) |
| GET | `/api/accounts/{accountId}/snapshot?date=yyyy-MM-dd` | Point-in-time portfolio snapshot (USD) |
| GET | `/health` | Liveness + database connectivity |

Example:

```powershell
curl -X POST http://localhost:5080/api/trades -H "Content-Type: application/json" -d '{
  "externalRef":"TRX-1001","accountId":"ACC-001","isin":"US0378331005","symbol":"AAPL",
  "side":"BUY","quantity":120,"price":185.40,"tradeDate":"2025-03-01","asOf":"2025-03-01T10:15:00Z" }'

curl "http://localhost:5080/api/accounts/ACC-001/snapshot?date=2025-03-01"
```

Feature flag: `TradeProcessing:EnableVersionedCorrections` (default `true`). Set
`TradeProcessing__EnableVersionedCorrections=false` for legacy insert-only behaviour; see SOLUTION.md §12.

## 6. Run the automated tests

The integration tests use a **real SQL Server**. Each test class creates its own database
(`TradeIngestionTests_<guid>`), migrates and seeds it through the API's startup path, and drops it afterwards.

```powershell
# Against docker compose (default; nothing to configure):
dotnet test

# Against another server (server-level connection string, no database name):
$env:TRADEINGESTION_TEST_SQLSERVER = "Server=localhost;Integrated Security=True;TrustServerCertificate=True"
dotnet test
```

The login needs `CREATE DATABASE` permission. If no SQL Server is reachable the integration tests **fail** with a
message saying how to start one. They are not silently skipped. The unit tests (`--filter "FullyQualifiedName~Unit"`)
need no database.

## 7. Run the demo

With the API running (step 5):

```powershell
./scripts/Demo.ps1                                   # docker-compose SQL Server, local sqlcmd
./scripts/Demo.ps1 -UseDocker                        # use sqlcmd inside the container
./scripts/Demo.ps1 -SqlServer localhost -IntegratedSecurity   # local SQL Server, Windows auth
```

The script:
1. Resets the demo data (`Database/Scripts/ResetDemoData.sql`). This deletes every trade on `ACC-001` and restores
   the AAPL 186.00 price, so the demo can be repeated any number of times. It verifies the reset actually happened.
2. Scenario 1: submits TRX-1001 (BUY 120 AAPL @ 185.40, AsOf 10:15Z) and expects **201 Created**.
3. Scenario 2: submits the same payload and expects **200 DuplicateIgnored**, with quantity still 120.
4. Scenario 3: submits the correction (150 @ 180.00, AsOf 11:30Z) and expects **200 CorrectionApplied**, with
   version 1 retained.
5. Scenario 4: requests the snapshot and expects quantity **150**, average cost **180.00**, price **186.00**,
   market value **27,900.00**, unrealized P/L **900.00** (USD).
6. Bonus: a stale event (ignored), a conflicting payload (409), and the same snapshot from `dbo.GetPortfolioSnapshot`.

Each expectation prints PASS/FAIL. The exit code is non-zero if any fail. `-SkipReset` keeps existing data.
To reset by hand: `sqlcmd ... -i Database/Scripts/ResetDemoData.sql` (**demo/dev only**: it deletes audit rows).

## Verification status (what was actually executed)

Executed on Windows 11 against **SQL Server 2022 Developer (16.0)** with Windows authentication, .NET SDK 9.0.101
building `net8.0` and running on the .NET 8.0 runtime:

- `dotnet build`: 0 warnings, 0 errors (`TreatWarningsAsErrors` on).
- `dotnet test`: **55 passed, 0 failed** (unit, end-to-end, concurrency, SQL parity, feature flag, API contract).
- `scripts/Demo.ps1 -SqlServer localhost -IntegratedSecurity`: all expectations PASS, run twice in a row.
- `dbo.GetPortfolioSnapshot` was executed directly. The execution plan was inspected on a 220k-version dataset (see
  SOLUTION.md §9).

**Not executed:** `docker compose up` (the Docker engine was not running on the machine used). The compose file uses
the standard `mcr.microsoft.com/mssql/server:2022-latest` image. The docker-based defaults (sa password, port 1433)
are therefore untested end to end.
