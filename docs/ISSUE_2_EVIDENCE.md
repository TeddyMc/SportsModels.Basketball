# Issue #2 implementation evidence

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
dotnet run --no-build --project src/SportsModels.Basketball.Data.Host -- 2024-01-01 2024-01-07 work/nba-2024-01-01_07
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

Merged the updated standards from `origin/master` at `12d4ba6`. The solution is now `SportsModels.Basketball.sln`; projects and assemblies are `SportsModels.Basketball.Data`, `SportsModels.Basketball.Data.Host`, and `SportsModels.Basketball.Data.Tests.Unit`. All ingestion layer namespaces use `SportsModels.Basketball.Data.*`. Project references, the test friend-assembly attribute, host usage, user agent, test temporary-directory prefix and documentation were updated consistently. There are no old namespace roots in source or tests.

The commands above now show the corrected names and were rerun after the rename: `dotnet build SportsModels.Basketball.sln` passed with 0 warnings/errors, and `dotnet test SportsModels.Basketball.sln --no-build` passed all 22 tests (0 failures/skips). The renamed host's seven-day attempt again exited 1 with Discovered=0, Downloaded=0, Skipped=0, Failures=7 due to the same proxy CONNECT 403 restrictions. This rename changes no ingestion behavior. Functional, performance and architecture review found no changes beyond naming/references/documentation; the live retrieval acceptance limitation remains open.
