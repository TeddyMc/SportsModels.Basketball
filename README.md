# SportsModels.Basketball — official NBA raw play-by-play ingestion

Unified basketball core contracts and a runnable in-memory example are documented in [docs/BASKETBALL_CORE_CONTRACTS.md](docs/BASKETBALL_CORE_CONTRACTS.md).

Local conversion, reference acquisition, persistent identities and diagnostic report usage are documented in [docs/NBA_CONVERSION.md](docs/NBA_CONVERSION.md). Partial output is not a claim of clean or fully validated data.

Initial implementation of [Issue #2](https://github.com/TeddyMc/SportsModels.Basketball/issues/2).
Requires the .NET 8 SDK and HTTPS access to NuGet and `stats.nba.com`.

The normal acquisition entry point downloads Stats PBP and the three standard validation references in one resumable invocation. It includes pacing, bounded retries and automatic cooldowns; see [acquisition and conversion usage](docs/NBA_CONVERSION.md). Download completion does not establish basketball data quality.

```sh
dotnet build SportsModels.Basketball.sln
dotnet test SportsModels.Basketball.sln --no-build
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- acquire 2025-10-02 2026-06-30
```

`acquire` takes inclusive start/end dates (`yyyy-MM-dd`) and an optional root directory, defaulting to `C:\Data\Basketball\NbaOfficial`. It writes PBP under `stats/playbyplayv3`, supporting snapshots under `references`, and verified discovery metadata, input hashes and run reports under `_acquisition`. Repeating the same command resumes retained data. The end must be before today UTC. Exit codes: `0` complete acquisition, `1` remaining gaps or exhausted recovery budget, `2` invalid arguments/setup, `130` user cancellation. Reports disclose failed discovery dates separately from failed game resources.

The earlier PBP-only invocation remains available: `dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- 2025-10-02 2026-06-30 [pbp-directory]`. It defaults to `C:\Data\Basketball\NbaOfficial\stats\playbyplayv3`, performs syntax-only PBP checks and does not include the integrated retry/cooldown flow.

## Official sources

- Discovery: `https://stats.nba.com/stats/scoreboardv3?LeagueID=00&GameDate=2026-02-02`. One request per date; read `scoreboard.games`, accept only `gameStatus = 3` (final), deduplicate ten-digit `gameId` strings. Read `awayTeam.teamTricode` and `homeTeam.teamTricode` for filenames. Invalid or missing team metadata is a discovery failure. The first discovered date is retained for a repeated stable game ID. No reliance on a rolling current-season schedule and no inferred IDs. An unexpected schema is a reported discovery failure, not an empty successful day. The old v2 scoreboard returned incomplete 2025–26 results; v3 is required for this season.
- Raw play-by-play: `https://stats.nba.com/stats/playbyplayv3?GameID={gameId}&StartPeriod=0&EndPeriod=0`. Requests all periods for completed games from past dates. No live polling. Historical availability and completeness are controlled by the NBA; missing archived responses are failures.

Both endpoints use native C# `HttpClient` with the supplied Chrome request headers, including NBA Origin/Referer. Live native requests succeeded for multiple games; Python and `curl_cffi` are therefore unnecessary and are not dependencies. The existing internal `INbaSource` interface keeps fetching replaceable without exposing HTTP concerns to the application. Browser headers may need maintenance if NBA requirements change; success here does not guarantee access from every network.

New downloads contain the Stats `playbyplayv3` response as returned by the NBA. No CDN schema is assumed or enforced, and raw responses are not converted. Use a fresh output directory when switching endpoints: reruns skip existing valid JSON without checking its source or schema.

The legacy PBP-only command checks downloaded PBP for JSON syntax only. `acquire` additionally verifies game identity and nonempty PBP/reference schemas. The separate local conversion command parses basketball actions and produces diagnostics. A syntactically valid raw response is not evidence of basketball data completeness.

## Storage and reruns

Each successful response body is saved as `<output-directory>/AWAY@HOME_yyyyMMdd_<gameId>.json`, for example `HOU@IND_20260202_0022500713.json`, preserving its bytes, whitespace, encoding and property order. Date is the queried NBA scoreboard date; away/home codes are upstream uppercase alphanumeric abbreviations. Filename components are validated to prevent path traversal. JSON is never reserialized. Writes use a unique temporary file in the same directory followed by a non-overwriting rename, preventing a partially written destination. Temporary files are removed on ordinary errors/cancellation; an abrupt process termination can leave a `.tmp` file, which reruns ignore.

In the legacy PBP-only command, an existing syntactically valid JSON file is skipped without fetching play-by-play again; it is not checked for upstream revisions, game identity or semantic completeness. The normal `acquire` command verifies identity, basic schema and retained hashes. An invalid or unreadable retained file is reported and left untouched. A concurrent destination creation causes a storage failure rather than overwriting another run.

HTTP/transport errors, 30-second request timeouts, invalid JSON and storage failures are reported and counted. The legacy PBP-only command has no automatic retries; `acquire` includes recovery as documented in the usage guide. Downloads are sequential and no scheduler is activated. A discovery failure means the run's discovered count is incomplete. Empty schedules and days with no final games are valid zero-game results.

## Structure

- `SportsModels.Data.NbaOfficial`: raw/reference acquisition and source-specific conversion with Contracts, Application, Interfaces, Infrastructure and Autofac folders. Application depends on I/O interfaces; Autofac connects HTTP, filesystem and shared identities through constructor injection. Reusable unified-event validation lives in `SportsModels.Data.Basketball`.
- `SportsModels.Data.NbaOfficial.Host`: runnable command and summary/exit codes.
- `SportsModels.Data.NbaOfficial.Tests.Unit`: deterministic acquisition, conversion, registry and validator tests using HTTP stubs, small saved fixtures and isolated temporary directories; no Internet required.

Downloaded data lives outside the repository by default; season logs are under `C:\Data\Basketball\NbaOfficial\logs\`. Repository-local output overrides under `NbaOfficial/`, SDK setup scratch files and test artifacts are ignored by Git. No downloaded NBA dataset is included. The old local `data/` folder was removed at the user's request; old bare-ID filenames are not migrated or detected as skips under the new naming scheme.

Execution results and outstanding live verification are recorded in [docs/ISSUE_2_EVIDENCE.md](docs/ISSUE_2_EVIDENCE.md).
