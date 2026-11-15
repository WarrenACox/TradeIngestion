<#
.SYNOPSIS
    End-to-end demo of trade ingestion, duplicate/correction handling and the portfolio snapshot.

.DESCRIPTION
    Runs the four assessment scenarios against a running API and asserts each expected result:
      1. Initial trade      TRX-1001 BUY 120 AAPL @ 185.40, AsOf 10:15Z        -> 201 Created
      2. Duplicate          exactly the same payload                            -> 200 DuplicateIgnored, quantity unchanged
      3. Correction         quantity 150 @ 180.00, AsOf 11:30Z                  -> 200 CorrectionApplied, v1 retained
      4. Snapshot           AAPL price 186.00                                   -> qty 150, avg 180.00, MV 27,900.00, P/L 900.00
    It then shows the audit history, two "bonus" events (stale and conflicting) and the same snapshot computed by
    dbo.GetPortfolioSnapshot.

    Repeatable: by default the script first runs Database/Scripts/ResetDemoData.sql, which deletes all trades on the
    demo account ACC-001 and restores the AAPL 186.00 price. Use -SkipReset to keep existing data.

    Works on Windows PowerShell 5.1 and PowerShell 7+. Exit code is non-zero if any expectation fails.

.EXAMPLE
    # SQL Server from docker compose (default sa password), API started with `dotnet run`:
    ./scripts/Demo.ps1

.EXAMPLE
    # Local SQL Server with Windows authentication:
    ./scripts/Demo.ps1 -SqlServer localhost -IntegratedSecurity

.EXAMPLE
    # Use the sqlcmd inside the docker container (no local sqlcmd needed):
    ./scripts/Demo.ps1 -UseDocker
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://localhost:5080',
    [string] $SqlServer = 'localhost,1433',
    [string] $Database = 'TradeIngestion',
    [string] $SqlUser = 'sa',
    [string] $SqlPassword = $(if ($env:MSSQL_SA_PASSWORD) { $env:MSSQL_SA_PASSWORD } else { 'TradeIngestion!Dev2025' }),
    [switch] $IntegratedSecurity,
    [switch] $UseDocker,
    [switch] $SkipReset
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$scriptsDir = Join-Path $repoRoot 'Database/Scripts'
$script:failures = 0

Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient
$http.BaseAddress = [Uri]$BaseUrl
$http.Timeout = [TimeSpan]::FromSeconds(30)

function Write-Step([string] $text) { Write-Host ''; Write-Host "=== $text ===" -ForegroundColor Cyan }

function Invoke-Api([string] $Method, [string] $Path, $Body) {
    $request = New-Object System.Net.Http.HttpRequestMessage -ArgumentList ([System.Net.Http.HttpMethod]::new($Method)), $Path
    $request.Headers.Add('X-Correlation-ID', 'demo-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Compress
        Write-Host "  -> $Method $Path $json" -ForegroundColor DarkGray
        $request.Content = New-Object System.Net.Http.StringContent $json, ([Text.Encoding]::UTF8), 'application/json'
    }
    else {
        Write-Host "  -> $Method $Path" -ForegroundColor DarkGray
    }

    $response = $http.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Write-Host "  <- $([int]$response.StatusCode) $text" -ForegroundColor DarkGray
    [pscustomobject]@{
        Status = [int]$response.StatusCode
        Body   = if ($text) { $text | ConvertFrom-Json } else { $null }
    }
}

function Assert-That([string] $What, $Expected, $Actual, [switch] $Numeric) {
    $ok = if ($Numeric) { $null -ne $Actual -and ([decimal]$Expected -eq [decimal]$Actual) } else { "$Expected" -eq "$Actual" }
    if ($ok) {
        Write-Host ("  [PASS] {0}: {1}" -f $What, $Actual) -ForegroundColor Green
    }
    else {
        Write-Host ("  [FAIL] {0}: expected {1}, got {2}" -f $What, $Expected, $Actual) -ForegroundColor Red
        $script:failures++
    }
}

function Invoke-SqlScript([string] $FileName, [string] $Query) {
    if ($UseDocker) {
        $sqlArgs = @('compose', 'exec', '-T', 'sqlserver', '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U', 'sa', '-P', $SqlPassword, '-C', '-b', '-d', $Database, '-W')
        $sqlArgs += if ($FileName) { @('-i', "/scripts/$FileName") } else { @('-Q', $Query) }
        Push-Location $repoRoot
        try { & docker @sqlArgs } finally { Pop-Location }
    }
    else {
        if (-not (Get-Command sqlcmd -ErrorAction SilentlyContinue)) {
            throw 'sqlcmd was not found. Install the SQL Server command-line tools, use -UseDocker, or run with -SkipReset.'
        }
        $auth = if ($IntegratedSecurity) { @('-E') } else { @('-U', $SqlUser, '-P', $SqlPassword) }
        $sqlArgs = @('-S', $SqlServer, '-d', $Database, '-C', '-b', '-W') + $auth
        $sqlArgs += if ($FileName) { @('-i', (Join-Path $scriptsDir $FileName)) } else { @('-Q', $Query) }
        & sqlcmd @sqlArgs
    }
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed with exit code $LASTEXITCODE" }
}

# ---------------------------------------------------------------------------------------------------------------
Write-Step "Checking API at $BaseUrl"
try {
    $health = $http.GetStringAsync('/health').GetAwaiter().GetResult()
    Write-Host "  Health: $health"
}
catch {
    throw "API is not reachable at $BaseUrl. Start it with: dotnet run --project src/TradeIngestion.Api"
}

if (-not $SkipReset) {
    Write-Step 'Resetting demo data (ResetDemoData.sql: removes ACC-001 trades, restores AAPL 186.00 on 2025-03-01)'
    Invoke-SqlScript -FileName 'ResetDemoData.sql' | Out-Host
    $existing = Invoke-Api GET '/api/trades/TRX-1001'
    if ($existing.Status -ne 404) { throw 'Reset did not remove TRX-1001; check the SQL connection parameters.' }
}

$trade = [ordered]@{
    externalRef = 'TRX-1001'
    accountId   = 'ACC-001'
    isin        = 'US0378331005'
    symbol      = 'AAPL'
    side        = 'BUY'
    quantity    = 120
    price       = 185.40
    tradeDate   = '2025-03-01'
    asOf        = '2025-03-01T10:15:00Z'
}

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Scenario 1: initial trade'
$r = Invoke-Api POST '/api/trades' $trade
Assert-That 'HTTP status' 201 $r.Status
Assert-That 'Outcome' 'Created' $r.Body.outcome
Assert-That 'Effective version' 1 $r.Body.effectiveVersion

Write-Step 'Scenario 2: exact duplicate'
$r = Invoke-Api POST '/api/trades' $trade
Assert-That 'HTTP status' 200 $r.Status
Assert-That 'Outcome' 'DuplicateIgnored' $r.Body.outcome
$snap = (Invoke-Api GET '/api/accounts/ACC-001/snapshot?date=2025-03-01').Body
Assert-That 'Quantity did not increase' 120 $snap.positions[0].netQuantity -Numeric

Write-Step 'Scenario 3: correction (later AsOf)'
$correction = [ordered]@{} ; foreach ($k in $trade.Keys) { $correction[$k] = $trade[$k] }
$correction.quantity = 150
$correction.price = 180.00
$correction.asOf = '2025-03-01T11:30:00Z'
$r = Invoke-Api POST '/api/trades' $correction
Assert-That 'HTTP status' 200 $r.Status
Assert-That 'Outcome' 'CorrectionApplied' $r.Body.outcome
Assert-That 'Effective version' 2 $r.Body.effectiveVersion

$history = (Invoke-Api GET '/api/trades/TRX-1001').Body
Assert-That 'Versions retained' 2 @($history.versions).Count
Assert-That 'Version 1 quantity preserved' 120 $history.versions[0].quantity -Numeric
Assert-That 'Version 1 price preserved' 185.40 $history.versions[0].price -Numeric
Assert-That 'Version 2 is current' 'True' $history.versions[1].isCurrent

Write-Step 'Scenario 4: portfolio snapshot (AAPL market price 186.00 USD)'
$snap = (Invoke-Api GET '/api/accounts/ACC-001/snapshot?date=2025-03-01').Body
$p = $snap.positions[0]
Assert-That 'Position quantity' 150 $p.netQuantity -Numeric
Assert-That 'Average cost' 180.00 $p.averageUnitCost -Numeric
Assert-That 'Market price' 186.00 $p.marketPrice -Numeric
Assert-That 'Market value (USD)' 27900.00 $p.marketValue -Numeric
Assert-That 'Unrealized P/L (USD)' 900.00 $p.unrealizedPnl -Numeric
Assert-That 'Total portfolio market value (USD)' 27900.00 $snap.totalMarketValue -Numeric
Assert-That 'Valuation complete' 'True' $snap.isValuationComplete

# ---------------------------------------------------------------------------------------------------------------
Write-Step 'Bonus: stale event (AsOf before the accepted correction) is ignored'
$stale = [ordered]@{} ; foreach ($k in $trade.Keys) { $stale[$k] = $trade[$k] }
$stale.quantity = 999
$stale.asOf = '2025-03-01T11:00:00Z'
$r = Invoke-Api POST '/api/trades' $stale
Assert-That 'HTTP status' 200 $r.Status
Assert-That 'Outcome' 'StaleIgnored' $r.Body.outcome

Write-Step 'Bonus: same AsOf as current version but different content is a conflict'
$conflict = [ordered]@{} ; foreach ($k in $correction.Keys) { $conflict[$k] = $correction[$k] }
$conflict.quantity = 151
$r = Invoke-Api POST '/api/trades' $conflict
Assert-That 'HTTP status' 409 $r.Status
Assert-That 'Outcome' 'ConflictingPayload' $r.Body.outcome

$snap = (Invoke-Api GET '/api/accounts/ACC-001/snapshot?date=2025-03-01').Body
Assert-That 'Quantity still 150 after stale/conflict' 150 $snap.positions[0].netQuantity -Numeric

if (-not $SkipReset) {
    Write-Step 'SQL artifact: EXEC dbo.GetPortfolioSnapshot @AccountId = ''ACC-001'', @SnapshotDate = ''2025-03-01'''
    Invoke-SqlScript -Query "EXEC dbo.GetPortfolioSnapshot @AccountId = 'ACC-001', @SnapshotDate = '2025-03-01';" | Out-Host
}

# ---------------------------------------------------------------------------------------------------------------
Write-Host ''
if ($script:failures -eq 0) {
    Write-Host 'DEMO PASSED - all expectations met.' -ForegroundColor Green
    exit 0
}
Write-Host "DEMO FAILED - $script:failures expectation(s) not met." -ForegroundColor Red
exit 1
