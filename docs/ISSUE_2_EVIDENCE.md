# Issue #2 implementation evidence

## Matchup/date storage and full-season execution — 2026-10-21

User requested removal of the previous local `data/` directory. The resolved deletion target and lack of reparse points were verified before recursively removing its 63 files. Season execution initially wrote to the ignored repository-local `NbaOfficial/PlayByPlay` folder; the user then corrected the destination to `C:\Data\Basketball\NbaOfficial` and delegated endpoint/version folder naming.

Host output now defaults to **`C:\Data\Basketball\NbaOfficial\stats\playbyplayv3`**, with the optional third CLI argument retaining explicit overrides (including for non-Windows environments). Filenames use `AWAY@HOME_yyyyMMdd_{gameId}.json`, for example `HOU@IND_20260202_0022500713.json`. Internal discovery metadata carries game ID, queried NBA date and upstream home/away team codes. Storage validates safe filename components, retains raw response bytes and uses the same atomic, non-overwriting writes and skip policy.

The season run exposed a material legacy discovery limitation: `scoreboardv2` returned no final games for October/November 2025 and incomplete later results. Stopped that attempt and switched to native `scoreboardv3?LeagueID=00&GameDate=yyyy-MM-dd`, parsing `scoreboard.games` (`gameStatus=3`, `gameId`, `awayTeam.teamTricode`, `homeTeam.teamTricode`). A direct v3 opening-night probe returned finals, and the October 2 v3 smoke test downloaded two completed preseason games missed by v2. The nba_api project also documents v2 deprecation/incomplete 2025–26 results: https://github.com/swar/nba_api/blob/master/CHANGELOG.MD . Existing successfully saved game files were retained and skipped on the corrected run.

Final Release build passed with zero warnings/errors; Release NUnit run passed **33 tests**, with zero failures/skips. New coverage includes exact v3 URL/query, different home/away matchups, filtering nonfinal games, date/ID naming, malformed/missing team metadata and unsafe filename components. Existing byte preservation, failure handling, storage and rerun tests continue to pass. No Python or NBA access is required for unit tests.

Season interpreted as the last completed **2025–26** season, from **2025-10-02 through 2026-06-30**, covering preseason through postseason without summer league. Executed the same host month by month to retain per-range summaries. All nine batches exited successfully: **1,400 discovered, 1,308 downloaded, 92 skipped, 0 failures**. The 92 skipped files had already been saved by the earlier attempts/smoke tests; total saved files are **1,400**, with no duplicate game IDs on inspection. Monthly discovered counts: October 151, November 219, December 198, January 233, February 173, March 239, April 144, May 38, June 5.

Migration completed and verified on **2026-10-25** after the season run finished. All **1,400** JSON files were moved to `C:\Data\Basketball\NbaOfficial\stats\playbyplayv3`; SHA256 verification before/after found **0 changed hashes**. The empty repository-local `NbaOfficial` folder was removed. Game-ID prefix counts: 001=71, 002=1,230, 003=7, 004=85, 005=6, 006=1. These are observed source-ID counts, not a claim of full semantic data quality.

Logs now live in `C:\Data\Basketball\NbaOfficial\logs`: `season-2025-26-v3.log` contains the completed run, `season-2025-26.log` the stopped v2 attempt, `scoreboard-v3-probe.json` the diagnostic response, and `migration-sha256.json` the migration hash manifest. No data or logs are committed.

After updating the default, final Release build passed with zero warnings/errors and all **33** tests passed again. `dotnet run --configuration Release --no-build --project src/SportsModels.Data.NbaOfficial.Host -- 2026-02-02 2026-02-02` (no output override) returned **Discovered=4 Downloaded=0 Skipped=4 Failures=0**, exit 0, verifying the migrated files are found at the new default path without recreating the repository-local output directory.

Sequential review: functional checks covered away/home orientation and named source fields, stable-ID deduplication, malformed schema, filename traversal prevention, output override and byte preservation. Performance remains sequential and bounded by the existing 30-second request timeout; only lightweight metadata is retained across discovery. Architecture keeps source parsing and filesystem work in Infrastructure, application orchestration behind internal ports and wiring in Autofac. `NbaGame` is a small internal source metadata record, not a unified basketball domain model.

## NBA Official / native Stats retrieval — 2026-10-21

This section supersedes earlier endpoint/naming and live-access limitations below. Merged the latest `origin/master` conventions and applied the current issue #2 specification: projects, namespaces, paths, friend-assembly attribute, host and tests now use `SportsModels.Data.NbaOfficial`. The repository solution remains `SportsModels.Basketball.sln`. The user consolidated the proposed #7 transport work into #2; #7 is closed as superseded.

Selected native C# `HttpClient` behind the existing internal `INbaSource` port. The only retrieval behavior change is replacing CDN downloads with `https://stats.nba.com/stats/playbyplayv3?GameID={gameId}&StartPeriod=0&EndPeriod=0`, using the browser headers already proven for discovery. No Python subprocess/dependency/configuration was introduced because the native-first option succeeded. The conditional Python-specific requirements are not applicable. C# still validates JSON syntax and atomically preserves raw response bytes; no basketball schema conversion was added.

Validation:

- `dotnet build SportsModels.Basketball.sln --source https://api.nuget.org/v3/index.json`: passed; final `--no-restore` build passed with zero warnings/errors.
- `dotnet test SportsModels.Basketball.sln --no-build`: **24 passed, 0 failed, 0 skipped**. Tests distinguish scoreboard/play-by-play by path, verify the exact Stats host/query, and cover HTTP 403/404/429/500, timeouts/cancellation, invalid JSON, byte equality, safe storage and reruns. No network or Python required.
- `dotnet run --no-build --project src/SportsModels.Data.NbaOfficial.Host -- 2024-01-01 2024-01-01 data/nba-native-stats`: **Discovered=8 Downloaded=8 Skipped=0 Failures=0**, exit 0.
- `dotnet run --no-build --project src/SportsModels.Data.NbaOfficial.Host -- 2024-01-01 2024-01-07 data/nba-week-stats`: **Discovered=55 Downloaded=55 Skipped=0 Failures=0**, exit 0.
- Same week command rerun: **Discovered=55 Downloaded=0 Skipped=55 Failures=0**, exit 0. SHA256 comparison of all 55 saved files before/after rerun found **0 changed files**.

Game 0022300451 was downloaded independently into both fresh directories, demonstrating repeated native success; the saved response identifies that game and contains 488 actions. Multiple other discovered games succeeded in both runs. Saved data lives under ignored `data/` and is excluded from commits. Live access used approved unsandboxed networking on the Windows laptop without adding a VPN configuration. Upstream access and historical coverage outside this week are not guaranteed, and JSON syntax validation is not full basketball data QA.

Sequential review: functional review checked endpoint query and adjusted deterministic fixtures to distinguish two Stats paths; performance review confirmed sequential bounded-timeout requests, no new retries or subprocess complexity; architecture review confirmed Application still depends on interfaces, Autofac owns wiring, names match the latest card, and raw bytes/storage policy remain unchanged. Browser version headers may require future maintenance.

## Windows connectivity update — 2026-10-21

The user reproduced a successful `leaguedashplayerstats` request with Chrome and curl on the Windows laptop without a VPN. The supplied request included a full Chrome 155 user agent, Origin/Referer, language, fetch metadata and client-hint headers. The Stats adapter now uses that header profile for `stats.nba.com` only. The scoreboard query retains its endpoint-specific LeagueID, DayOffset and GameDate parameters; the player-statistics query parameters do not apply to scoreboard discovery. No cookies or credentials are required or stored. Which individual header caused the improvement has not been isolated.

Validation: `dotnet build SportsModels.Basketball.sln --source https://api.nuget.org/v3/index.json` passed with zero warnings/errors; `dotnet test SportsModels.Basketball.sln --no-build` passed all 23 tests. Public NuGet was selected explicitly because the machine's inherited private feed previously returned 401. The added test checks Stats headers and their exclusion from CDN requests.

Live command: `dotnet run --no-build --project src/SportsModels.Data.NbaOfficial.Host -- 2024-01-01 2024-01-01 data/nba-header-smoke`.

Result: **Discovered=8 Downloaded=0 Skipped=0 Failures=8**, exit 1. Scoreboard discovery succeeded and returned IDs 0022300451 through 0022300458. All eight CDN play-by-play requests returned HTTP 403. A separate curl probe for 0022300451 with Chrome User-Agent, Origin, Referer, Accept and language headers also returned 403. No JSON files were saved. This verifies live daily discovery but does not establish reliable end-to-end ingestion or resolve the remaining CDN failure. Week retrieval and successful rerun evidence remain outstanding.

Sequential review: the change is confined to infrastructure request headers; no new domain behavior, retry loops, architecture changes or additional data endpoints. Response validation, byte preservation, cancellation and per-request timeout behavior remain covered by the existing tests. Browser version headers reflect the supplied working request and may need maintenance as upstream requirements change.

Executed on 2026-10-20, on `task/2-nba-play-by-play-json`, based on the repository's default branch `master` at `e30a5b3`. The repository has no `main` branch. No other open PRs were present when implementation began. Issue #2 records **Ready for Implementation (approved)**; its referenced feature #5 and epic #4 were read. There are no issue comments or native parent relationship on #2.

## Build and unit tests

.NET SDK 8.0.426 was installed in `/workspace/dotnet` (outside Git). CLI state and NuGet packages were directed to ignored `work/` because the environment's home is read-only.

Commands, with `dotnet` resolving to `/workspace/dotnet/dotnet`:

```sh
dotnet build SportsModels.Basketball.sln --no-restore
dotnet test SportsModels.Basketball.sln --no-build --logger 'trx;LogFileName=unit.trx' --results-directory work/test-results
```

Build: succeeded, 0 warnings and 0 errors. Final NUnit result: 22 passed, 0 failed, 0 skipped. The initial run exposed an overly strict cancellation assertion (NUnit required the exact base exception type); it was corrected to accept derived cancellation exceptions, then the complete build and tests were rerun. TRX for the initial run is under ignored `work/test-results/unit.trx`; the naming correction rerun is under `work/test-results/naming-unit.trx`.

Coverage includes:

- Inclusive seven-day discovery; final-game filtering and stable ID deduplication; actual requested dates and URL paths.
- End-to-end application/HTTP adapter/filesystem execution using deterministic transport responses, including UTF-8/whitespace byte equality and a rerun that avoids repeat downloads.
- HTTP 404/429/500, transport errors, discovery and download timeouts, invalid downloaded JSON, schema errors and continuation after failures.
- Empty days, invalid inputs, unsafe game IDs, caller cancellation, corrupt existing files, filesystem errors, non-overwrite behavior, temporary-file cleanup and Autofac resolution.

These are lightweight transport/storage tests, not basketball data quality assurance. Stub payloads are synthetic; they do not verify today's upstream schema or historical coverage.

## Manual seven-day live attempt

```sh
dotnet run --no-build --project src/SportsModels.Data.NbaOfficial.Host -- 2024-01-01 2024-01-07 work/nba-2024-01-01_07
```

Observed output: seven date-specific `[Http]` failures, each reporting that the configured proxy's CONNECT tunnel failed with HTTP 403; final summary:

```text
Discovered=0 Downloaded=0 Skipped=0 Failures=7
```

Host exit code: **1**. No game files were created. Direct connectivity probes through the configured proxy to both `stats.nba.com` and `cdn.nba.com` also returned CONNECT 403. The environment's destination allowlist excludes both hosts. The policy was not bypassed or modified.

**Outstanding acceptance criterion:** successful manual retrieval of approximately one week of official data has not been demonstrated. Run the command above from an environment permitting both NBA hosts, record actual game/download/failure counts, then rerun to verify skips against real responses. NBA anti-bot restrictions, historical CDN availability and the live discovery schema remain unverified here. The zero discovered count in this blocked run does not mean no games occurred.

## Sequential review

1. Functional/QA: checked inclusive dates, final-only filtering, discovery schema failures, per-item continuation, cancellation, syntax-only validation, stable IDs, byte preservation and non-overwriting reruns. Fixed the cancellation test assertion and added explicit per-game timeout coverage.
2. Performance: sequential network requests, one discovery response and one play-by-play body at a time; deduplicated IDs, bounded per-request timeout, no bulk retained payloads. Responses are buffered in memory; very large or malicious upstream bodies are not size-limited. No performance or heavy domain QA claims.
3. Architecture/readability: verified public entry contracts and Autofac wiring, internal ports/implementations, no Application-to-Infrastructure dependency, no speculative domain model, no basketball-specific action parsing, no downloaded data or credentials tracked.

The pull request references #2 and remains unmerged. This implementation evidence does not mark all acceptance criteria complete; the live retrieval limitation above remains explicit.

## Naming correction after specification update

Merged the updated standards from `origin/master` at `12d4ba6`. The solution is now `SportsModels.Basketball.sln`; projects and assemblies are `SportsModels.Data.NbaOfficial`, `SportsModels.Data.NbaOfficial.Host`, and `SportsModels.Data.NbaOfficial.Tests.Unit`. All ingestion layer namespaces use `SportsModels.Data.NbaOfficial.*`. Project references, the test friend-assembly attribute, host usage, user agent, test temporary-directory prefix and documentation were updated consistently. There are no old namespace roots in source or tests.

The commands above now show the corrected names and were rerun after the rename: `dotnet build SportsModels.Basketball.sln` passed with 0 warnings/errors, and `dotnet test SportsModels.Basketball.sln --no-build` passed all 22 tests (0 failures/skips). The renamed host's seven-day attempt again exited 1 with Discovered=0, Downloaded=0, Skipped=0, Failures=7 due to the same proxy CONNECT 403 restrictions. This rename changes no ingestion behavior. Functional, performance and architecture review found no changes beyond naming/references/documentation; the live retrieval acceptance limitation remains open.
