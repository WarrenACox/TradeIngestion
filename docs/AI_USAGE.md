# How AI Assistance Was Used

This repository was produced with an AI coding agent (Claude Code, running Claude Opus 5.5) working directly in a
local development environment, with shell access to the .NET SDK and a local SQL Server 2022 instance. This document
records what was asked, what the agent did, and how its output was verified. Reviewers are entitled to know that.

## Prompts

| # | Prompt (summarised) | Purpose |
|---|---|---|
| 1 | The full assessment brief ("Role: Senior .NET Architect & Lead Software Engineer … Trade Ingestion and Portfolio Snapshot Service …"), including the five-phase working process (analysis, implementation, verification, documentation, presentation), the requirement not to claim unverified results, and the deliverables list. | Single master prompt. All subsequent work was the agent executing that brief. |

No further prompts were used to steer the design. Design decisions (holding-period average cost, key-range locking,
knowledge-time snapshot semantics, 409 on same-AsOf conflicts, and so on) were made by the agent and are justified in
SOLUTION.md. They should be reviewed as design proposals, not taken on authority.

## What the agent did, by phase

1. **Analysis.** Inspected the environment: .NET SDK 9 with the .NET 8 runtime, SQL Server 2022 Developer reachable
   with Windows authentication, Docker engine not running. Identified ambiguities and risks (SOLUTION.md §1) before
   writing code.
2. **Implementation.** Scaffolded the solution with `dotnet new`, wrote all source files, and generated the EF Core
   migration with `dotnet ef migrations add` (not hand-written).
3. **Verification, run rather than assumed.** It built, ran the API, exercised every outcome over HTTP, ran the
   stored procedure directly, ran the test suite and the demo script twice, and inspected execution plans on a
   220k-row dataset.
4. **Documentation and presentation.** Wrote the README, SOLUTION.md and the video script.

## Defects that verification caught (and which the first draft contained)

These are listed because they show why verification against a real environment was mandatory:

| Defect in the first draft | How it was found | Fix |
|---|---|---|
| `InvariantGlobalization=true` crashed `Microsoft.Data.SqlClient` at the first connection | Running the API | Removed the setting (with a comment explaining why) |
| `[Produces("application/json")]` on controllers silently overwrote `application/problem+json` on 409/422/400 responses | Integration test `Same_asof_with_different_content_is_a_409_conflict` failed | Removed the attribute |
| Demo reset invoked `sqlcmd` with **no arguments** (a `$args`→`$sqlArgs` rename missed the `@args` splat), so the "reset" silently did nothing and the demo was not repeatable | Running the demo twice: the second run failed | Fixed the splat. The script now **verifies** the reset by checking that TRX-1001 returns 404. |
| Stored procedure emitted the ANSI "Null value is eliminated by an aggregate" warning | Running the SP in sqlcmd | `ISNULL` inside the aggregates |
| Scope dictionaries rendered as type names in the simple console formatter | Reading the API logs | Message-template scopes |
| Index claims in the docs were initially unproven (a 10-row DB showed a scan) | Inspecting `SHOWPLAN` | Loaded 220k versions, confirmed the seek-based plan, and documented the measured reads and time |

## What was not verified

- `docker compose up` was not executed (the Docker engine was unavailable). The compose file and the
  docker-default connection strings are therefore untested end to end.
- No load or soak testing beyond the 25-way concurrency test and the single-query plan analysis.

## Guidance for reviewers

Treat the code as you would a pull request from a capable colleague. The test suite and the demo are the evidence;
run them (`dotnet test`, `scripts/Demo.ps1`) rather than trusting this document.
