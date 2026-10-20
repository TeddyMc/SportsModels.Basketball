# NbaModel — official NBA raw play-by-play ingestion

Initial implementation of [Issue #2](https://github.com/TeddyMc/SportsModels.Basketball/issues/2).
Requires the .NET 8 SDK and HTTPS access to NuGet, `stats.nba.com` and `cdn.nba.com`.

```sh
dotnet build NbaModel.sln
dotnet test NbaModel.sln --no-build
dotnet run --project src/NbaModel.PlayByPlay.Host -- 2024-01-01 2024-01-07 /path/to/nba-json
```

The three positional arguments are start date, end date (both inclusive, `yyyy-MM-dd`), and output directory. The end must be before today in UTC. Discovery uses the NBA's game date, rather than converting dates to the machine's time zone. Exit codes: `0` complete without failures, `1` one or more discovery/download/storage failures, `2` invalid arguments/setup, `130` user cancellation. Each failure identifies its date or game ID and category. The final summary counts unique discovered final games, downloaded files, skipped files, and failures. Failures include discovery dates as well as games, so they are not a count of failed games alone.

## Official sources

- Discovery: `https://stats.nba.com/stats/scoreboardv2?LeagueID=00&DayOffset=0&GameDate=01%2F01%2F2024`. One request per date; read `GameHeader` by column name, accept only `GAME_STATUS_ID = 3` (final), deduplicate ten-digit `GAME_ID` strings. No reliance on a rolling current-season schedule and no inferred IDs. An unexpected schema is a reported discovery failure, not an empty successful day.
- Raw play-by-play: `https://cdn.nba.com/static/json/liveData/playbyplay/playbyplay_{gameId}.json`. Despite the upstream directory name, this tool requests only completed games from past dates. No live polling. Historical availability and completeness are controlled by the NBA; missing archived responses are failures.

The only parsed basketball fields are discovery metadata needed to select completed games. Play-by-play is checked for JSON syntax only, without basketball schema validation, action parsing, conversion, or cleaning. A syntactically valid JSON response is not evidence of basketball data completeness.

## Storage and reruns

Each successful response body is saved as `<output-directory>/<gameId>.json`, preserving its bytes, whitespace, encoding and property order. JSON is never reserialized. Writes use a unique temporary file in the same directory followed by a non-overwriting rename, preventing a partially written destination. Temporary files are removed on ordinary errors/cancellation; an abrupt process termination can leave a `.tmp` file, which reruns ignore.

An existing syntactically valid JSON file is skipped without fetching play-by-play again. An invalid or unreadable existing file produces a failure and is left untouched. Inspect and explicitly move/delete it before retrying. A concurrent destination creation causes a storage failure rather than overwriting another run. Existing files are not checked for upstream revisions, correct game identity or semantic completeness.

HTTP/transport errors, 30-second request timeouts, invalid JSON and storage failures are reported and counted. Processing continues for independent dates/games; caller cancellation propagates. There are no automatic retries, parallel downloads or scheduled runs. Rerun to retry failed items; already successful items will be skipped. A discovery failure means the run's discovered count is incomplete. Empty schedules and days with no final games are valid zero-game results.

## Structure

- `NbaModel.PlayByPlay`: one pragmatic onion with Contracts, Application, Interfaces, Infrastructure and Autofac folders. There is no basketball domain model in this transport-only task. Application depends on I/O interfaces; Autofac connects HTTP and filesystem implementations via constructor injection.
- `NbaModel.PlayByPlay.Host`: runnable command and summary/exit codes.
- `NbaModel.PlayByPlay.Tests.Unit`: fast deterministic NUnit tests, using an HTTP handler stub and isolated temporary directories; no Internet required.

Generated output, SDK setup scratch files and test artifacts are ignored by Git. Use an output directory outside the repository or under ignored `work/` or `data/`. No downloaded NBA dataset is included.

Execution results and outstanding live verification are recorded in [docs/ISSUE_2_EVIDENCE.md](docs/ISSUE_2_EVIDENCE.md).
