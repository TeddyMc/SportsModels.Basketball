# Unified basketball contracts

The source-independent contracts live in the dependency-free .NET 8 `SportsModels.Data.Basketball` project, namespace `SportsModels.Data.Basketball.Contracts`. [Issue #8](https://github.com/TeddyMc/SportsModels.Basketball/issues/8) owns the core decisions; [issue #16](https://github.com/TeddyMc/SportsModels.Basketball/issues/16) owns the source-backed event schema, evidence review and revision decisions.

## Events, evidence and reconstructed state

Canonical events describe understood basketball history. A populated value may come directly from a field, from description parsing, from a verified coordinate transformation, or from an established relationship between source rows. Parsing/transformation does not automatically make a fact into reconstructed game state.

| Representation | Contents | Responsibility |
| --- | --- | --- |
| Canonical game/events | Shots, fouls, participants, clock, established credits/links and verified shot classifications | These contracts; populated by conversion |
| Companion field evidence | Source snapshot/rows, canonical event/field, method and explanation, including missingness reasons | Small declaration types here; complete production evidence/reporting in #13 |
| Reconstructed state | Running score, possession, lineups, cumulative foul/stat totals, live/dead-ball state | Separate #13 validation/state output, not fields duplicated on canonical events |

For example, a goaltending basket is one made `FieldGoalEvent` and a `ViolationEvent` referring to it. Points come from the made shot, once. Its `ShotLocation` remains null until a transformation is verified; companion evidence explains that unresolved value. Reconstructed score after the play belongs to validation output, not to either event.

## Identity, order and time

All canonical game/team/player/coach/event references are internally allocated `long` identities. Provider identities remain source/string pairs on metadata. Do not cast provider IDs as an allocation policy or substitute zero for missing identities. Populated actors must resolve to the appropriate game metadata. Game-team player/coach lists describe supplied metadata, not proof of participation, active status or complete rosters. Existing game-team constructor calls remain valid; its `Coaches` init property defaults to an empty collection.

Events form one flat chronological stream. Sequence numbers preserve same-clock order. `BasketballClock` supports regulation quarters, arbitrary 1-based overtime numbers and fractional remaining seconds. Overtime number is null in regulation and required in overtime; period length is competition-specific.

`OccurredAtUtc` is nullable `DateTime`. Supported times must have `DateTimeKind.Utc`. Do not interpolate timestamps, substitute response/received times or invent second precision from minute-resolution descriptions. Source date/timezone/precision evidence belongs in companion reporting.

These positional records declare an API, not a validation engine. They do not enforce identity uniqueness, UTC kind, clock consistency, link resolution, participant exclusivity or basketball rules. Collections must be supplied and remain caller-owned; there is no defensive copying or deep-immutability guarantee. #13 enforces invariants and reports unresolved coverage.

## Event contracts and counting

| Contract | Use and important boundaries |
| --- | --- |
| `FieldGoalEvent` | Official 2/3-point attempt, outcome, optional assist/block credit, distance/location, style/modifiers and court area. Defensive-goaltending credit is an ordinary made shot, not a separate points award. |
| `FreeThrowEvent` | One independent attempt, nullable attempt number/count and causative `ParentFoulEventId`. Category describes the award's cause, supported by text or an established parent foul. The category constructor argument is mandatory; callers explicitly pass `NotSet` when unresolved. Subsequent possession is reconstructed separately. |
| `FoulEvent` | A supplied list of charged `FoulParticipant` records; separate optional fouled player and affected FG reference. The second participant in a double foul is also charged, not automatically the fouled player. |
| `SubstitutionEvent` | Team, outgoing and incoming players; same-clock order remains significant. |
| `ReboundEvent` | Player/team credit and supported offensive/defensive classification; at most one missed-FG or missed-FT reference. Team rebounds have no fabricated player. Live/dead-ball state is separate derived output. |
| `TurnoverEvent` | Team, optional responsible player, observed category and optional steal credit. Combined out-of-bounds bad-pass/lost-ball categories preserve the source distinction. |
| `TimeoutEvent` | Team, coach-challenge or official timeout; team can be null. No mandatory-timeout classification is inferred from Regular. |
| `ReviewEvent` | Observed review/ruling, optional requesting team and reviewed event. Supported, RulingStands and Overturned stay distinct. There is no manufactured Started/Completed lifecycle or inferred Inconclusive result. |
| `ViolationEvent` | Observed violation and optional attempt links. Defensive goaltending can refer to a credited FG or FT; the violation applies no additional score. A violation does not automatically imply a turnover. |
| `JumpBallEvent` | Supported participants and gaining player/team; unknown identities remain null. |
| `PeriodBoundaryEvent` | Start/end marker; inherited clock identifies regulation/overtime period. |
| `HeaveEvent` | Separately reported team heave, with player identity where supplied. The inspected source labels these Team Field Goal Attempt while isFieldGoal=0. Do not assume zero team-stat effect; #13 must verify treatment against reference statistics. |
| `EjectionEvent` | Player or coach ejection, with optional charged team; never place a coach into a player ID. |

`FoulParticipant(TeamId, PlayerId, CoachId)` has nullable typed identities. Player and coach are mutually exclusive; team-only entries represent bench/administrative charges. Ordinary known fouls have one charged entry, established double fouls two. Unknown participants/teams require companion findings, not invented actors. Player/coach metadata can establish membership while the charged team on an individual source row remains unresolved.

Foul categories preserve observed personal-take versus transition-take, offensive-charge, defensive-three-second, flopping and technical distinctions. Defensive-three-second rows are represented as fouls. Turnovers include offensive goaltending, inbound, illegal-assist, timed and other observed violations. `Carrying` normalizes the observed Palming Turnover label; DiscontinueDribble remains distinct. No separate basket-interference or points-award type is introduced without source evidence.

A foul's FG reference identifies the attempt on which the foul occurred, made or missed. A supported shooting foul linked to a made shot and continuation FT can establish an and-one; one FT alone cannot. There is no stored and-one flag, nested authoritative FT list, generic relationship graph or duplicated cumulative score/foul/stat totals.

Auxiliary block/steal source rows support the corresponding shot/turnover, without duplicate statistical events. Review rulings themselves do not enact another score or adjustment event. All correction evidence stays outside the canonical stream.

## Classification and missingness

A **heave** is a long-distance desperation shot near the end of a period, often launched from around half court or farther away to beat the buzzer. The local feed explicitly reports rows such as `NETS Heave` with subtype `Team Field Goal Attempt`, separately from ordinary made/missed player shots. NBA's [2025-26 explanation](https://www.nba.com/news/nba-heaves-rule-change-early-results) describes the team-versus-player statistical distinction. `HeaveEvent` preserves the observed source classification; #13 must reconcile actual counting against season rules and box scores rather than assume it is statistically irrelevant.

All enums start with explicit `NotSet = 0` and explicit sequential values. The four core enums from #8 retain their numeric values. Enums introduced in the unmerged #16 draft were revised before publication; downstream users must rebuild against the final schema. `NotSet` means unresolved/unspecified, never a definite negative; `Other` means an understood category outside the named taxonomy.

Shot style is JumpShot, Layup, Dunk or HookShot, with supported modifiers such as Driving, Reverse, Cutting and Tip. Tip dunks retain Dunk + Tip; Tip layups retain Layup + Tip. Modifiers can coexist. Supply the collection; empty means none positively identified, not proof of absence. Do not insert NotSet into that collection. Style and court area are separate; distance alone does not establish a zone.

Null means unavailable, unresolved or inapplicable according to the field. Companion evidence distinguishes the reason. Optional assist/block/steal credit means no identified credited participant, not proof that no credit occurred. Event links remain null where absent or unestablished. Missing FT numbers/counts represent unresolved facts, not zero attempts; fully assessed dependent checks need known values or an evidenced exception. Unrelated checks can continue.

The inspected FT rows all have blank shotResult; make/miss comes from descriptions. Most subtype labels supply attempt numbers/counts, but unnumbered technical FTs also occur. Rebound subType Unknown does not establish that offensive/defensive classification cannot be derived: original totals and sequence may support it, with evidence.

## Companion evidence usage

These three types currently have one executable consumer: [Program.cs](../examples/SportsModels.Data.Basketball.Examples/Program.cs). Each example keeps a separate `Scenario.Evidence` list, the `Row(...)` helper constructs source references, and the console prints each field's evidence kind and explanation. There is no production converter or report writer using them yet. #13 will populate and export this companion information during conversion/validation; it is not required by scoring or modelling consumers of the canonical events.

| Type | Useful purpose | Example |
| --- | --- | --- |
| `BasketballSourceRowReference` | Locate the exact input row supporting a value, even after another download changes the source file. | Follow a snapshot hash and zero-based row index to the Brown shot or Daniels violation; retain both original provider identifiers. |
| `BasketballEvidenceKind` | Explain whether a value was read, parsed, transformed or linked, or why it was not populated. These are methods/reasons, not confidence scores. | Assist is Parsed from description; shot/violation relationship is Linked; unverified coordinates are Unresolved. |
| `BasketballFieldEvidence` | Connect that method and source row(s) to a specific internal event field, making conversion results auditable. | If an assist looks wrong, find that event's AssistingPlayerId evidence and inspect the original description instead of searching the whole game. |

`BasketballFieldEvidence` references an internal `EventId`, a canonical `FieldName`, an evidence kind, source rows and an explanation. Prefer `nameof(FieldGoalEvent.Outcome)` over hard-coded property names. Paths into nested participants can be described in the explanation; no reflection framework is introduced.

Kinds are Direct, Parsed, Transformed, Linked, Unresolved and NotApplicable (plus NotSet). Direct/Parsed/Transformed/Linked describe how a populated canonical fact was established. Unresolved/NotApplicable explain null or NotSet without substituting zero, false or fabricated certainty. They do not wrap values in Fact<T> or duplicate canonical values.

Each `BasketballSourceRowReference` identifies a source, immutable snapshot, zero-based source-array row index, and nullable original actionId/actionNumber strings. Snapshot IDs resolve through #13's source manifests; the examples use SHA-256 identifiers of inspected files and separately print filenames. Equal actionNumber values are not unique identities. A content hash alone does not preserve source bytes: production manifests must disclose input availability and identify retained snapshots.

```csharp
var evidence = new BasketballFieldEvidence(
    shot.EventId,
    nameof(FieldGoalEvent.ShotLocation),
    BasketballEvidenceKind.Unresolved,
    [sourceRow],
    "Provider coordinate transformation is not verified; canonical location remains null.");
```

The snippet assumes `shot` and `sourceRow` have already been supplied; the runnable example below constructs them. These minimal declarations do not constitute complete provenance or coverage. #13 still owns original rows/descriptions/fields, every-row disposition, correction history, reference acquisition, timestamp precision, identity registry, missingness findings, complete field evidence and JSON/CSV reporting. Unmapped source rows have no canonical EventId and must be retained in companion row-accounting reports, not forced into this event-field evidence list. No new derived-state contract or validation engine is imposed here.

## Coordinates

`BasketballShotLocation(XFeet, YFeet)` uses feet from the centre of the attacking basket projected onto the court. Stand at that basket facing centre court: +X is right, +Y toward centre court; court-area left/right use the same viewpoint. Coordinates remain attack-relative across changes of ends. Do not clamp coordinates or replace all zeroes with null.

The source supplies xLegacy/yLegacy and reported distances, but no transformation is implemented or claimed verified here. #13 must verify source scaling/origin/axes/orientation, distance units and applicable court boundaries before populating transformed locations or derived CourtArea. Unverified locations remain null and zones NotSet with companion reasons. Court area can be a canonical classification once established; it need not become reconstructed game state merely because calculation was necessary.

## Corrected history and task boundaries

Canonical events contain understood, corrected effective history. Apply evidence-supported source amendments to the affected play and omit cancelled plays. No unknown-placeholder events, correction instructions, cancellation records, superseded versions or synthetic score-adjustment events belong in the unified stream. Ambiguous effective results require explicit partial/uncertain coverage; dependent checks remain not assessable while independent work can continue. Previous run outputs remain unchanged.

[#13](https://github.com/TeddyMc/SportsModels.Basketball/issues/13) owns acquisition, conversion, identity/SQLite persistence, complete companion evidence and reconstructed state/validation. It accounts for every source row as mapped, supporting another event, superseded/cancelled/non-event metadata, or unresolved, with reasons and affected coverage. Supporting roster/box-score acquisition follows the shared raw acquisition flow, separate from local conversion. Core metadata and manually curated example identities are not acquired reference evidence. [#15](https://github.com/TeddyMc/SportsModels.Basketball/issues/15) owns substantive speculative repair.

## Runnable source-derived selections

```sh
dotnet run --project examples/SportsModels.Data.Basketball.Examples
```

[Program.cs](../examples/SportsModels.Data.Basketball.Examples/Program.cs) manually constructs selected events from five inspected snapshots, with independent internal IDs and separate field evidence:

| Local source file | Representative action IDs |
| --- | --- |
| ATL@BOS_20260128_0022500673.json | 33–36: shooting foul and two FTs interrupted by substitution; 329: Joe Mazzulla technical; 417/418: Brown made 2 and Daniels defensive goaltending |
| ATL@BKN_20260403_0022501122.json | 300: Clowney/Gueye double technical; 306/307: Clowney technical and ejection |
| BKN@POR_20260323_0022501045.json | 331: Cissoko/Minott double personal |
| ATL@PHI_20251130_0022500304.json | 596: second-OT start; 632/633: missed shot/block at 59.60 seconds; 634/635: team rebound/timeout; 636: made shot; 651: end |
| BKN@IND_20251105_0022500173.json | 270: Rick Carlisle coach ejection |

The consumer checks the selected goaltending link/one made basket, interrupted FT order, double-charge structure, coach identity and fractional second overtime. It prints Direct, Parsed, Linked and Unresolved evidence. No transformed coordinates are invented, and no example claims complete field evidence, UTC conversion or full-game reconstruction. Game/team/name associations are curated for the example, not produced by a tested production resolver. Source-row charged teams remain null where unsupported even when metadata associates a coach with a team.

No raw downloads or live NBA access are needed to run these constructions. The original snapshot hashes tie the examples to the inspected inputs, but those bulk inputs are not committed. The local inventory covered 1,400 files/707,440 rows; its full evidence, sampling scope, observed categories and limitations are recorded in #16. These selections are contract demonstrations, not validated complete games or proof of source parsing, corrected-history reconciliation, team-heave counts or reference-stat agreement.
