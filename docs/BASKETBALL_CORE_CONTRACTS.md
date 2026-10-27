# Unified basketball core contracts

The approved core slice of [issue #8](https://github.com/TeddyMc/SportsModels.Basketball/issues/8) lives in `SportsModels.Data.Basketball`, under `SportsModels.Data.Basketball.Contracts`. It has no package or provider dependencies. It follows the existing .NET 8, nullable-enabled build configuration.

Game, team, player and event IDs are internally allocated `long` values. Allocation and mapping are future conversion responsibilities. Metadata retains provider identities as source/string pairs, preserving leading zeroes. Events reference internal IDs. Team rosters describe game-specific metadata; presence does not assert participation or active status.

All enums use explicit sequential values beginning with `NotSet = 0`. `NotSet` means unspecified, never a definite negative. `FieldGoalType` is included as an agreed declaration; field-goal events and shot coordinates await specification.

`BasketballClock` supports regulation quarters and arbitrary 1-based overtime numbers, with fractional `TimeSpan` remaining time. Regulation uses a null overtime number. There is no fixed quarter duration in these contracts. Events share internal identity, sequence number, clock and nullable `DateTime` occurrence time. Known times must have `DateTimeKind.Utc`; unavailable times stay null. Conversion must never interpolate occurrence timestamps or confuse response/received times with event times.

The event list is one chronological stream. Sequence numbers distinguish same-clock events; initial conversion may retain source array order. Each free throw is independent, permitting substitutions and other events between attempts. Unknown attempt numbers/counts and parent-foul links remain null. Consumers query `ParentFoulEventId` to find related attempts; no nested authoritative copy exists. Scores, foul counts and cumulative statistics are not duplicated here.

These positional records have init-only properties, but do not validate UTC kind, clock/period consistency, ordering, identity uniqueness or reference resolution. Collections and nested records must be supplied; caller-owned collections are not defensively copied or deeply immutable. Future conversion/public-entry validation must enforce invariants and surface unresolved data. Successful compilation does not establish reconstruction or cleaning correctness.

## Nullable-field audit

The core has five distinct nullable properties. `OccurredAtUtc` is repeated in the concrete event constructors because of record inheritance; its meaning is the same on every event.

| Property | Why null is permitted | Meaning when populated |
| --- | --- | --- |
| `BasketballClock.OvertimePeriodNumber` | Not applicable in regulation. This is not a missing quarter number; `Period` identifies the quarter. | 1-based overtime number. An overtime clock requires this value. |
| `BasketballPlayByPlayEvent.OccurredAtUtc` (also substitution/free throw) | The source may provide no event occurrence timestamp. A game clock alone cannot establish wall-clock time. | A known occurrence time converted to UTC, with `DateTimeKind.Utc`. |
| `FreeThrowEvent.AttemptNumber` | The source may not establish the attempt's position in its award. This represents an unresolved fact, not an attempt with no position. | 1-based attempt number, such as 1 in “1 of 2”. |
| `FreeThrowEvent.TotalAttempts` | The source may not establish the award's attempt count. This represents an unresolved fact, not an award with no count. | The associated award's attempt count, such as 2 in “1 of 2”. |
| `FreeThrowEvent.ParentFoulEventId` | A causal foul may not be established or represented. Matching clock values or a single attempt alone do not prove a link. | An internal foul-event identity supported by conversion evidence. |

Attempt number and total attempts are normally recoverable from descriptions in the inspected NBA samples. Their nullability accommodates unavailable or unresolved information across sources; the samples do not prove a basketball case in which those facts inherently do not exist. Requiring both for a fully validated stream would be a stricter future conversion policy. The approved core retains nullable declarations and does not silently replace missing facts with zero or guessed values.

Null alone cannot distinguish all reasons for missingness. Conversion/QA (#13) must retain the reason and report unresolved values; the pending provenance design determines where that evidence lives. No other core properties are nullable: identities, names, clocks, teams and collections must be supplied. An empty collection represents no supplied entries, rather than a null collection.

## Runnable consumer example

From the repository root:

```sh
dotnet run --project examples/SportsModels.Data.Basketball.Examples
```

[Program.cs](../examples/SportsModels.Data.Basketball.Examples/Program.cs) constructs a synthetic game with independent internal/provider IDs and game-team player metadata. It consumes polymorphic events, querying two free throws linked to foul ID 900 with an opposing-team substitution between attempts at the same clock. A second-overtime substitution demonstrates fractional remaining time and an explicit UTC timestamp; the other timestamps are null.

This is a partial stream illustrating the core API: foul 900 has no corresponding concrete record because the foul schema is not approved. It is not a complete, reference-resolved game. No production foul type is invented to make the example compile. Overtime is synthetic, not verified against live source data. Player lists are illustrative, not complete rosters.

## Deferred scope

The full issue remains incomplete: remaining event types/taxonomies, shot-coordinate conventions, provenance and unsupported/corrected-event behavior require approval. No provider conversion, identity allocation, cleaning, network/storage, serialization framework or ingestion changes are included. Conversion and QA belong to #13; roster/box-score enrichment remains separate follow-up work. Full source-mapped examples and a real overtime fixture remain outstanding.
