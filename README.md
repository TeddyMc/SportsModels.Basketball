# SportsModels.Basketball — official NBA raw play-by-play ingestion

Unified basketball core contracts and a runnable in-memory example are documented in [docs/BASKETBALL_CORE_CONTRACTS.md](docs/BASKETBALL_CORE_CONTRACTS.md).

Initial implementation of [Issue #2](https://github.com/TeddyMc/SportsModels.Basketball/issues/2).
Requires the .NET 8 SDK and HTTPS access to NuGet and `stats.nba.com`.

```sh
dotnet build SportsModels.Basketball.sln
dotnet test SportsModels.Basketball.sln --no-build
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- 2025-10-02 2026-06-30
```

The positional arguments are start date, end date (both inclusive, `yyyy-MM-dd`), and an optional output directory. Output defaults to `C:\Data\Basketball\NbaOfficial\stats\playbyplayv3\`, named after the upstream API endpoint and version. Supply a third argument to override it. The end must be before today in UTC. Discovery uses the NBA's game date, rather than converting dates to the machine's time zone. Exit codes: `0` complete without failures, `1` one or more discovery/download/storage failures, `2` invalid arguments/setup, `130` user cancellation. Each failure identifies its date or game ID and category. The final summary counts unique discovered final games, downloaded files, skipped files, and failures. Failures include discovery dates as well as games, so they are not a count of failed games alone.

## Official sources

- Discovery: `https://stats.nba.com/stats/scoreboardv3?LeagueID=00&GameDate=2026-02-02`. One request per date; read `scoreboard.games`, accept only `gameStatus = 3` (final), deduplicate ten-digit `gameId` strings. Read `awayTeam.teamTricode` and `homeTeam.teamTricode` for filenames. Invalid or missing team metadata is a discovery failure. The first discovered date is retained for a repeated stable game ID. No reliance on a rolling current-season schedule and no inferred IDs. An unexpected schema is a reported discovery failure, not an empty successful day. The old v2 scoreboard returned incomplete 2025–26 results; v3 is required for this season.
- Raw play-by-play: `https://stats.nba.com/stats/playbyplayv3?GameID={gameId}&StartPeriod=0&EndPeriod=0`. Requests all periods for completed games from past dates. No live polling. Historical availability and completeness are controlled by the NBA; missing archived responses are failures.

Both endpoints use native C# `HttpClient` with the supplied Chrome request headers, including NBA Origin/Referer. Live native requests succeeded for multiple games; Python and `curl_cffi` are therefore unnecessary and are not dependencies. The existing internal `INbaSource` interface keeps fetching replaceable without exposing HTTP concerns to the application. Browser headers may need maintenance if NBA requirements change; success here does not guarantee access from every network.

New downloads contain the Stats `playbyplayv3` response as returned by the NBA. No CDN schema is assumed or enforced, and raw responses are not converted. Use a fresh output directory when switching endpoints: reruns skip existing valid JSON without checking its source or schema.

The only parsed basketball fields are discovery metadata needed to select completed games. Play-by-play is checked for JSON syntax only, without basketball schema validation, action parsing, conversion, or cleaning. A syntactically valid JSON response is not evidence of basketball data completeness.

## Storage and reruns

Each successful response body is saved as `<output-directory>/AWAY@HOME_yyyyMMdd_<gameId>.json`, for example `HOU@IND_20260202_0022500713.json`, preserving its bytes, whitespace, encoding and property order. Date is the queried NBA scoreboard date; away/home codes are upstream uppercase alphanumeric abbreviations. Filename components are validated to prevent path traversal. JSON is never reserialized. Writes use a unique temporary file in the same directory followed by a non-overwriting rename, preventing a partially written destination. Temporary files are removed on ordinary errors/cancellation; an abrupt process termination can leave a `.tmp` file, which reruns ignore.

An existing syntactically valid JSON file is skipped without fetching play-by-play again. An invalid or unreadable existing file produces a failure and is left untouched. Inspect and explicitly move/delete it before retrying. A concurrent destination creation causes a storage failure rather than overwriting another run. Existing files are not checked for upstream revisions, correct game identity or semantic completeness.

HTTP/transport errors, 30-second request timeouts, invalid JSON and storage failures are reported and counted. Processing continues for independent dates/games; caller cancellation propagates. There are no automatic retries, parallel downloads or scheduled runs. Rerun to retry failed items; already successful items will be skipped. A discovery failure means the run's discovered count is incomplete. Empty schedules and days with no final games are valid zero-game results.

## Structure

- `SportsModels.Data.NbaOfficial`: one pragmatic onion with Contracts, Application, Interfaces, Infrastructure and Autofac folders. There is no basketball domain model in this transport-only task. Application depends on I/O interfaces; Autofac connects HTTP and filesystem implementations via constructor injection.
- `SportsModels.Data.NbaOfficial.Host`: runnable command and summary/exit codes.
- `SportsModels.Data.NbaOfficial.Tests.Unit`: fast deterministic NUnit tests, using an HTTP handler stub and isolated temporary directories; no Internet required.

Downloaded data lives outside the repository by default; season logs are under `C:\Data\Basketball\NbaOfficial\logs\`. Repository-local output overrides under `NbaOfficial/`, SDK setup scratch files and test artifacts are ignored by Git. No downloaded NBA dataset is included. The old local `data/` folder was removed at the user's request; old bare-ID filenames are not migrated or detected as skips under the new naming scheme.

Execution results and outstanding live verification are recorded in [docs/ISSUE_2_EVIDENCE.md](docs/ISSUE_2_EVIDENCE.md).
