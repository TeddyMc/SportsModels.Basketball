# Unified basketball contracts

The source-independent contracts live in the dependency-free .NET 8 `SportsModels.Data.Basketball` project, namespace `SportsModels.Data.Basketball.Contracts`. [Issue #8](https://github.com/TeddyMc/SportsModels.Basketball/issues/8) records the core decisions; [issue #16](https://github.com/TeddyMc/SportsModels.Basketball/issues/16) records the remaining event schema and approval.

## Identities, order and time

Game, team, player and event IDs are internally allocated `long` values. Metadata retains provider identities as source/string pairs, preserving leading zeroes. Events reference internal IDs; never substitute zero or cast provider IDs as an allocation policy. Team rosters are game-specific metadata; listing a player does not assert participation or active status. Populated event participants must resolve to that metadata with consistent team membership.

Events form one flat chronological stream. Sequence numbers distinguish same-clock events. `BasketballClock` supports regulation quarters, arbitrary 1-based overtime numbers and fractional `TimeSpan` remaining time. `OvertimePeriodNumber` is null in regulation and required for overtime. Period duration depends on competition rules.

`OccurredAtUtc` is nullable `DateTime`. Known occurrence times must have `DateTimeKind.Utc`; unavailable times remain null. Never interpolate timestamps or confuse event time with response/received time. Original timestamp precision and evidence belong in companion provenance; minute-resolution descriptions do not establish second precision.

Records have init-only properties and caller-owned collections, without defensive copies. They do not enforce UTC kind, clock consistency, ordering, positive identities, reference resolution or basketball invariants. Conversion/public-entry validation owns those checks. Contract construction does not establish reconstruction correctness.

## Event usage and counting

| Contract | Usage and references |
| --- | --- |
| `FieldGoalEvent` | An official attempt, including make/miss, shooter, optional assist/block credits, distance/location, style/modifiers and court area. Null assist/block means no identified credited player, not proof no credit occurred. |
| `FreeThrowEvent` | One independent attempt; substitutions/timeouts/reviews may intervene. Nullable `ParentFoulEventId` identifies the causative foul. |
| `FoulEvent` | Optional team/committing/fouled actors accommodate team, bench or unresolved cases. `RelatedFieldGoalEventId` identifies the attempt on which the foul occurred, made or missed. |
| `SubstitutionEvent` | Required team, outgoing and incoming players; sequence preserves same-clock order. |
| `ReboundEvent` | Player/team credit and optional offensive/defensive/live-ball classification. At most one missed-FG or missed-FT reference is populated. Team credit uses a null player, never a fictitious player. |
| `TurnoverEvent` | Required team, optional responsible player and steal credit. A supporting steal source row is not a second turnover. |
| `TimeoutEvent` | Team or administrative timeout; team may be null. |
| `ReviewEvent` | Official review/challenge, phase and established outcome. `RelatedEventId` narrowly references the reviewed event. Started reviews normally use `NotSet` outcome. Reviews apply no second statistical effect. |
| `ViolationEvent` | Optional team/player and related attempt references. A violation does not automatically imply a turnover. |
| `JumpBallEvent` | Optional participants and gaining team/player, populated only when established. |
| `PeriodEvent` | Start/end marker; inherited clock identifies regulation/overtime period. |
| `HeaveEvent` | Separately reported heave not established as an official attempt. Implies neither an official miss nor points. Use a field-goal event instead if an official attempt is established; never count both. |
| `AwardedPointsEvent` | Positive, justified scoring award not already represented by a made FG/FT. Optional credited player and related violation. |

A shooting foul linked to a made FG, together with a supported continuation FT, can establish an and-one. A single FT alone cannot; an away-from-play award is a counterexample. No stored and-one boolean, nested attempts, generic relationship graph or duplicated cumulative score/foul/stat totals are included.

`FreeThrowEvent.Category` describes the award's cause, independently of attempt count. Ordinary covers normal shooting/non-shooting awards. `PossessionRule` describes ordinary/retained award treatment; it does not unconditionally identify the next possessing team. Competition/season rules and context remain necessary. Both fields are trailing optional constructor parameters defaulting to `NotSet`, preserving existing construction calls.

For goaltending already represented by a made FG, link the violation to that FG and emit no additional awarded-points event. Alternatively, a standalone positive award can reference its violation without a duplicate made shot. Block and steal auxiliary rows support their shot/turnover rather than creating duplicate statistical events. Personal/team-foul counting and possession reconstruction belong to conversion/validation, with uncertainty preserved.

## Categories and missingness

Every enum has explicit sequential values starting with `NotSet = 0`; existing numeric values are preserved. `NotSet` means unspecified/unresolved, never false or a definite negative. `Other` means a known category outside the supported taxonomy.

Shot style and spatial court area are separate classifications. Modifiers can coexist, such as Driving + Reverse on a Layup. Supply the `ShotModifiers` collection; empty means no positively identified modifiers, not proof of absence. Do not insert `NotSet` into that collection. Neither style nor distance alone establishes court area.

Null means unavailable, unresolved or inapplicable according to the property. Companion findings distinguish the reasons:

| Nullable field group | Meaning |
| --- | --- |
| Overtime number | Inapplicable in regulation; must be known in overtime. |
| UTC occurrence time | No supported occurrence timestamp. |
| FT attempt number/count | Unavailable/unresolved award position or count, not a nonexistent position/count. |
| Actor/team fields | Team/bench/administrative cases, unavailable participants or unresolved identity; distinguish with findings. |
| Assist/block/steal credits | No identified credited participant; absence is not automatically a known negative. |
| Event links | Relationship absent/inapplicable or not established; never infer certainty from proximity alone. |
| Shot distance/location | Unavailable or not verified; zero coordinates can be valid. |
| Rebound live-ball status | Unknown unless supported; false is an established non-live-ball status. |

A fully assessed ordinary FT sequence requires known attempt number/count or an explicit evidence-supported exception. Missing values do not invalidate unrelated checks, but dependent FT/possession checks cannot be labelled passed. Required identities and collections must be supplied; unresolved required identities prevent creating that typed event rather than justify placeholders.

## Canonical coordinates

`BasketballShotLocation(XFeet, YFeet)` is a sealed value record. Origin is the centre of the attacking basket projected onto the court, in feet. Stand at that basket facing centre court: +X is right, +Y is toward centre court. Left/right court areas use this viewpoint. Coordinates stay attack-relative across changes of ends. For example, (-1, 2) is left and toward centre court; (3, 8) is right and toward centre court. Do not clamp coordinates or replace all source zeroes with null.

No `xLegacy`/`yLegacy` transformation is verified or implemented here. [#13](https://github.com/TeddyMc/SportsModels.Basketball/issues/13) must verify provider scaling, origin, axes and orientation before populating locations. Unsupported/ambiguous locations remain null with findings. Reported shot-distance units likewise require verification; distance need not be recomputed from location or silently reconciled. Court-area derivation requires verified coordinates and applicable court dimensions/rules in #13; no NBA geometry constants are embedded here. Unknown court area is `NotSet`.

## Corrected history and companion evidence

Unified events represent understood, corrected effective basketball history. Evidence-supported amendments update the affected play and cancelled plays are omitted. Correction instructions, cancellation records, superseded versions and synthetic adjustment events do not belong in this stream. Ambiguous effective results require partial/uncertain coverage, not a guessed settled play. Previous saved runs remain unchanged. Genuine reviews, violations and standalone scoring awards remain basketball events, with counting boundaries above.

There is no unknown/unclassified placeholder event. Uninterpretable or unrepresentable rows remain in companion output with original evidence, reasons and affected coverage. Their effects cannot be assumed zero; dependent state/checks remain unknown/not assessable while unaffected checks continue.

#13 owns acquisition, conversion, persistent identity/SQLite mappings, companion provenance and JSON/CSV reports. It accounts for every source row as mapped to event(s), supporting another event, superseded/cancelled/non-event metadata, or unresolved. Multiple rows may support one canonical event. Preserve original order, descriptions, both actionId/actionNumber, original fields, snapshot/input hashes and availability, missingness reasons, mappings, derivations and correction evidence outside canonical events. Hashes alone do not preserve input bytes. Raw inputs and prior run outputs remain unchanged by conversion.

#13 also owns supporting roster/box-score acquisition through the shared raw acquisition flow, separate from local conversion. Core player metadata alone is not reference evidence. Source overtime fixtures, provider transformations, timestamp precision and source-mapped and-one versus unrelated-single-FT examples remain #13 verification work. [#15](https://github.com/TeddyMc/SportsModels.Basketball/issues/15) owns substantive speculative repair.

## Runnable synthetic example

From the repository root:

```sh
dotnet run --project examples/SportsModels.Data.Basketball.Examples
```

[Program.cs](../examples/SportsModels.Data.Basketball.Examples/Program.cs) constructs 22 selected ordered events using illustrative rosters and independently allocated internal/provider identities. Every populated event link resolves within the stream. It demonstrates:

- A made FG, shooting foul and ordinary 1/1 FT, contrasted with an away-from-play foul and unrelated 1/1 FT. These constructions are informed by #8's recorded Flagg and CHI-CHA examples; original source fixtures were not re-inspected.
- Two FTs sharing a foul, interrupted by a same-clock substitution; existing constructor calls default the new fields to `NotSet`.
- A missed FG with block credit and linked team rebound; a turnover with steal credit.
- Synthetic second-overtime start/end markers, a jump ball, fractional clock and null/explicit UTC timestamps.
- Administrative timeout, review start/completion, noncounting heave and standalone awarded points linked to a violation.
- Explicit canonical locations with opposite X signs, without a claimed provider transformation or derived zone.

Expected output includes `supported continuation=True` for FT 902, `False` for FT 904, and `FreeThrowEvent -> SubstitutionEvent -> FreeThrowEvent` for the interrupted sequence. The query illustrates these supplied relationships, not a general inference algorithm. No statistics or validation engine is introduced.

This is a partial synthetic stream, not a complete game, validated lineup, actual source overtime fixture or proof of source reconstruction. No placeholder/correction event is constructed. Network access and raw NBA downloads are unnecessary.
