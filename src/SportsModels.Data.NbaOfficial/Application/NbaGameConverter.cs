using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SportsModels.Data.Basketball.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;

namespace SportsModels.Data.NbaOfficial.Application;

internal sealed record ConvertedGame(BasketballGamePlayByPlay Game, List<SourceRowDisposition> Rows,
    List<BasketballFieldEvidence> Evidence, List<CheckResult> Checks, GameValidationContext Context, string Competition);

internal sealed class NbaGameConverter(IIdentityRegistry registry)
{
    private const string Provider = "NbaOfficial";
    private readonly Dictionary<string, long> teams = [];
    private readonly Dictionary<string, Person> players = [];
    private readonly Dictionary<string, Person> coaches = [];
    private readonly List<CheckResult> checks = [];
    private readonly List<BasketballFieldEvidence> evidence = [];
    private JsonElement[] rows = [];
    private string snapshot = "";
    private string homeSource = "";
    private string awaySource = "";

    private sealed record Person(long Id, string SourceId, string Team, string Name, HashSet<string> Aliases);

    public ConvertedGame Convert(byte[] bytes, string snapshotId, ReferenceBundle references)
    {
        snapshot = snapshotId;
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.TryGetProperty("meta", out var meta) && meta.Number("version") != 1)
        { throw new InvalidDataException("Unsupported playbyplayv3 response version."); }
        var sourceGame = doc.RootElement.GetProperty("game");
        var gameId = sourceGame.Text("gameId");
        if (gameId.Length != 10 || gameId.Any(c => !char.IsAsciiDigit(c))) { throw new InvalidDataException("Invalid game identity."); }
        rows = sourceGame.GetProperty("actions").EnumerateArray().Select(x => x.Clone()).ToArray();
        if (rows.Length == 0) { throw new InvalidDataException("Empty action stream cannot establish game structure."); }

        // PBP location h/v is game-side metadata. Filenames never decide home/away identities.
        homeSource = Side("h", references, "homeTeam");
        awaySource = Side("v", references, "awayTeam");
        if (homeSource == awaySource) { throw new InvalidDataException("Home and away identity conflict."); }
        foreach (var team in new[] { homeSource, awaySource })
        {
            teams[team] = Resolve("Team", team, $"Game {gameId} side metadata in {snapshot}");
        }
        var internalGame = Resolve("Game", gameId, $"NBA game key; home={homeSource}; away={awaySource}; snapshot={snapshot}");
        BuildPeople(references);
        var events = new List<BasketballPlayByPlayEvent>();
        var dispositions = new List<SourceRowDisposition>();
        var actionCounts = rows.GroupBy(x => x.Key("actionId")).ToDictionary(g => g.Key, g => g.Count());
        var conflictingActions = rows.Where(r => r.Key("actionId") != "").GroupBy(r => r.Key("actionId"))
            .Where(g => g.Count() > 1 && g.Select(SourceJson.Fingerprint).Distinct().Count() > 1).Select(g => g.Key).ToHashSet();
        var repeatedRows = new Dictionary<string, int>();

        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (conflictingActions.Contains(row.Key("actionId")))
            {
                dispositions.Add(new(index, row, "Unresolved", [], "Conflicting rows share actionId; effective correspondence is not guessed."));
                Add("INPUT.CONFLICTING_ACTION_ID", CheckStatus.NotAssessable, index, "Duplicated actionId with different row contents; all conflicting versions retained outside canonical data.");
                continue;
            }
            var fingerprint = SourceJson.Fingerprint(row);
            if (row.Key("actionId") != "" && repeatedRows.TryGetValue(fingerprint, out var earlier))
            {
                dispositions.Add(new(index, row, "Duplicate", dispositions[earlier].EventIds, $"Identical source row including actionId already retained at row {earlier}; no second statistical event."));
                Add("INPUT.DUPLICATE_ROW", CheckStatus.Fail, index, $"Exact repeated source row {earlier}; duplicate effect excluded.");
                continue;
            }
            repeatedRows[fingerprint] = index;
            if (row.Text("actionType") == "" && Regex.IsMatch(row.Text("description"), @"\b(STEAL|BLOCK)\b"))
            {
                dispositions.Add(new(index, row, "PendingCredit", [], "Auxiliary statistical credit; matched after primary conversion."));
                continue;
            }
            try
            {
                var period = row.Number("period");
                var seconds = SourceJson.Remaining(row.Text("clock"));
                if (period < 1 || seconds < 0) { throw new InvalidDataException("Invalid period or clock."); }
                var clock = new BasketballClock(period <= 4 ? (BasketballPeriod)period : BasketballPeriod.Overtime,
                    period <= 4 ? null : period - 4, TimeSpan.FromSeconds(seconds));
                var action = row.Key("actionId");
                var identity = registry.ResolveEvent(Provider, gameId, snapshot, index.ToString(CultureInfo.InvariantCulture),
                    action == "" ? null : action, SourceJson.Fingerprint(row), action != "" && actionCounts[action] == 1);
                var id = identity.EntityId!.Value;
                if (identity.Status == "SnapshotScoped") { Add("IDENTITY.EVENT_CORRESPONDENCE", CheckStatus.NotAssessable, index, identity.Evidence, id); }
                var converted = Map(row, index, id, clock);
                if (converted is null)
                {
                    dispositions.Add(new(index, row, "Unresolved", [], "Unsupported or insufficiently resolved basketball row; statistical effects are not assumed zero."));
                    Add("MAPPING.ROW", CheckStatus.NotAssessable, index, "Row remains in companion evidence; no placeholder event emitted.");
                    continue;
                }
                events.Add(converted);
                dispositions.Add(new(index, row, "Mapped", [id], "Effective event mapped from this row; original values retained."));
            }
            catch (Exception exception) when (exception is FormatException or InvalidDataException or InvalidOperationException or OverflowException)
            {
                dispositions.Add(new(index, row, "Unresolved", [], exception.Message));
                Add("MAPPING.ROW", CheckStatus.NotAssessable, index, exception.Message);
            }
        }
        AttachCredits(events, dispositions);
        LinkEvents(events, dispositions.Where(r => r.Disposition == "Unresolved").Select(r => r.RowIndex + 1).ToHashSet());
        var explainedFields = evidence.Select(e => (e.EventId, e.FieldName)).ToHashSet();
        foreach (var item in events) { ExplainFields(item, item.SequenceNumber - 1, explainedFields); }
        var home = Team(homeSource);
        var away = Team(awaySource);
        var game = new BasketballGamePlayByPlay(internalGame, [Source(gameId)], home, away, events);
        var ordinary = gameId.StartsWith("002", StringComparison.Ordinal) || gameId.StartsWith("004", StringComparison.Ordinal)
            || gameId.StartsWith("001", StringComparison.Ordinal) || gameId.StartsWith("005", StringComparison.Ordinal);
        var competition = gameId[..3] switch
        {
            "002" => "NBA regular season (provider prefix)", "004" => "NBA playoffs (provider prefix)",
            "001" => "NBA preseason (provider prefix)", "005" => "NBA play-in (provider prefix)",
            _ => "Special format or unclassified; ordinary rule checks unsupported"
        };
        var (statistics, lineups) = references.Observations(s => teams.GetValueOrDefault(s) is var t && t != 0 ? t : null,
            s => players.GetValueOrDefault(s)?.Id, ordinary);
        checks.AddRange(references.Checks);
        var scoreObservations = rows.Select((r, i) => new ScoreObservation(i + 1,
            int.TryParse(r.Text("scoreHome"), out var h) ? h : null,
            int.TryParse(r.Text("scoreAway"), out var a) ? a : null, i)).Where(x => x.Home is not null || x.Away is not null).ToArray();
        var unresolved = dispositions.Where(x => x.Disposition == "Unresolved").Select(x => x.RowIndex + 1).ToArray();
        var context = new GameValidationContext(ordinary, false, lineups, statistics, scoreObservations, unresolved,
            dispositions.Any(x => x.Disposition == "Unresolved" && x.Original.Text("actionType") == ""),
            "Reference interval/minute precision must be verified; no reference means not assessable. Final status unavailable unless independently supplied.")
        {
            Gaps = dispositions.Where(x => x.Disposition == "Unresolved").Select(x => Gap(x.Original, x.RowIndex + 1))
                .Concat(checks.Where(c => c.Code == "IDENTITY.FOUL_ACTOR" && c.SourceRows is not null)
                    .Select(c => new CoverageGap(c.SourceRows![0] + 1, ["PF"], false, false, "Unresolved charged actor")))
                .Concat(checks.Where(c => c.Code == "IDENTITY.NAME" && c.EventId is not null)
                    .Select(c => new CoverageGap(events.Single(e => e.EventId == c.EventId).SequenceNumber, ["AST"], false, false, "Unresolved described assist credit"))).ToArray(),
            FoulCounts = FoulCounts(events)
        };
        checks.Add(new("TIME.UTC", CheckStatus.NotAssessable,
            "Description wall clocks are minute precision; game date/timezone/midnight evidence is not verified. OccurredAtUtc remains null."));
        checks.Add(new("SHOT.LOCATION", CheckStatus.NotAssessable,
            "xLegacy/yLegacy axes/orientation and court areas are unverified; retained raw, canonical location/distance/area unset."));
        checks.Add(new("GAME.STATUS", CheckStatus.NotAssessable,
            "Period-end rows alone do not independently establish official final/suspended status."));
        var mappedIds = events.Select(e => e.EventId).ToHashSet();
        for (var i = 0; i < checks.Count; i++)
        {
            if (checks[i].EventId is long eventId && !mappedIds.Contains(eventId)) { checks[i] = checks[i] with { EventId = null }; }
        }
        return new(game, dispositions, evidence, checks, context, competition);
    }

    private string Side(string location, ReferenceBundle references, string side)
    {
        var fromReference = references.Boxes.Where(x => x.Period == 0).Select(x => x.Box.GetProperty(side).Text("teamId")).Distinct().ToArray();
        var observed = rows.Where(r => r.Text("location") == location && r.Key("teamId") != "")
            .Select(r => r.Key("teamId")).Distinct().ToArray();
        if (fromReference.Length == 1 && fromReference[0] != "")
        {
            if (observed.Any(x => x != fromReference[0])) { throw new InvalidDataException("Reference/PBP side conflict."); }
            return fromReference[0];
        }
        if (observed.Length != 1) { throw new InvalidDataException("Game side identity unavailable or contradictory."); }
        return observed[0];
    }

    private void BuildPeople(ReferenceBundle references)
    {
        foreach (var team in references.Teams())
        {
            foreach (var player in team.GetProperty("players").EnumerateArray())
            {
                RegisterPlayer(player.Text("personId"), team.Text("teamId"),
                    (player.Text("firstName") + " " + player.Text("familyName")).Trim(),
                    [player.Text("familyName"), player.Text("nameI")]);
            }
        }
        foreach (var row in rows)
        {
            // Coaches cannot be inferred from a small personId, a name, or a technical foul alone.
            if (row.Text("actionType") is "Made Shot" or "Missed Shot" or "Free Throw" or "Substitution" or "Turnover" or "Rebound" or "Jump Ball" or ""
                && row.Key("personId") != "" && teams.ContainsKey(row.Key("teamId")) && !teams.ContainsKey(row.Key("personId")))
            {
                RegisterPlayer(row.Key("personId"), row.Key("teamId"), row.Text("playerNameI"), [row.Text("playerName"), row.Text("playerNameI")]);
            }
        }
        foreach (var (team, key, name) in references.RosterPlayers)
        {
            if (players.TryGetValue(key, out var known) && known.Team == team)
            {
                known.Aliases.Add(SourceJson.Normalize(name));
                continue;
            }
            // Season roster membership alone is not historical game membership (trades occur).
            // A named incoming substitution for this team supplies the additional game evidence.
            var incomingRows = rows.Where(r => r.Text("actionType") == "Substitution" && r.Key("teamId") == team
                && SourceJson.Normalize(r.Text("description").Split(" FOR ")[0].Replace("SUB: ", "", StringComparison.Ordinal)) == SourceJson.Normalize(name.Split(' ')[^1])).ToArray();
            var sameName = references.RosterPlayers.Count(p => p.TeamId == team && p.Name.Split(' ')[^1] == name.Split(' ')[^1]);
            if (!players.ContainsKey(key) && incomingRows.Length > 0 && sameName == 1)
            { RegisterPlayer(key, team, name, [name.Split(' ')[^1]]); }
        }
        foreach (var (team, key, name) in references.Coaches.Where(c => teams.ContainsKey(c.TeamId)))
        {
            var observed = rows.Any(r => r.Key("personId") == key && r.Text("actionType") is "Foul" or "Ejection");
            if (!observed) { continue; }
            var id = Resolve("Coach", key, $"NBA season coach roster and matching game source actor {key}; snapshot {snapshot}.");
            coaches[key] = new(id, key, team, name, [SourceJson.Normalize(name)]);
        }
        // Coach rows observed with no team/player metadata cannot safely be attached to a game team.
        // Resolve registry identity with role evidence from the paired named coach ejection, retain missing team.
        foreach (var row in rows.Where(r => r.Text("actionType") == "Ejection" && r.Text("playerName") == ""
            && r.Key("personId") != "" && !teams.ContainsKey(r.Key("personId")) && !players.ContainsKey(r.Key("personId"))))
        {
            var name = row.Text("description").Split(" Ejection:")[0].Trim();
            if (name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2) { continue; }
            var team = row.Key("teamId");
            var id = Resolve("Coach", row.Key("personId"), $"Named non-player ejection {snapshot}: {row.GetRawText()}");
            coaches.TryAdd(row.Key("personId"), new(id, row.Key("personId"), team, name, [SourceJson.Normalize(name)]));
        }
    }

    private void RegisterPlayer(string key, string team, string name, IEnumerable<string> aliases)
    {
        if (key == "" || key == "0" || !teams.ContainsKey(team)) { return; }
        if (players.TryGetValue(key, out var existing))
        {
            if (existing.Team != team) { throw new InvalidDataException("Same player assigned to both teams in one game."); }
            foreach (var alias in aliases.Append(name).Where(x => x != "")) { existing.Aliases.Add(SourceJson.Normalize(alias)); }
            return;
        }
        var id = Resolve("Player", key, $"NBA person identity with team {team} in {snapshot}; name alone is not a cross-provider match.");
        players[key] = new(id, key, team, name, aliases.Append(name).Where(x => x != "").Select(SourceJson.Normalize).ToHashSet());
    }

    private BasketballGameTeam Team(string key) => new(teams[key],
        rows.FirstOrDefault(r => r.Key("teamId") == key && r.Text("teamTricode") != "").Text("teamTricode"),
        [Source(key)], players.Values.Where(p => p.Team == key).OrderBy(p => p.Id)
            .Select(p => new BasketballGamePlayer(p.Id, p.Name, [Source(p.SourceId)])).ToArray())
    {
        Coaches = coaches.Values.Where(p => p.Team == key).Select(p => new BasketballGameCoach(p.Id, p.Name, [Source(p.SourceId)])).ToArray()
    };

    private BasketballPlayByPlayEvent? Map(JsonElement row, int index, long id, BasketballClock clock)
    {
        var type = row.Text("actionType");
        var subtype = row.Text("subType");
        var description = row.Text("description");
        var team = TeamId(row);
        var person = players.GetValueOrDefault(row.Key("personId"));
        var player = person?.Id;
        var coach = coaches.GetValueOrDefault(row.Key("personId"));
        var sequence = index + 1;
        if (type is "Made Shot" or "Missed Shot" or "Heave")
        {
            if (team is null || (type != "Heave" && player is null)) { return null; }
            var outcome = row.Text("shotResult") switch { "Made" => ShotOutcome.Made, "Missed" => ShotOutcome.Missed, _ => ShotOutcome.NotSet };
            var value = row.Number("shotValue") switch { 2 => FieldGoalType.TwoPointer, 3 => FieldGoalType.ThreePointer, _ => FieldGoalType.NotSet };
            var assistMatch = Regex.Match(description, @"\(([^()]+) \d+ AST\)");
            var assist = assistMatch.Success ? ByName(assistMatch.Groups[1].Value, person?.Team, index)?.Id : null;
            if (assistMatch.Success && assist is null) { Add("IDENTITY.NAME", CheckStatus.NotAssessable, index, "Assist participant unresolved.", id); }
            var style = subtype.Contains("Dunk", StringComparison.OrdinalIgnoreCase) ? ShotStyle.Dunk
                : subtype.Contains("Layup", StringComparison.OrdinalIgnoreCase) ? ShotStyle.Layup
                : subtype.Contains("Hook", StringComparison.OrdinalIgnoreCase) ? ShotStyle.HookShot
                : type == "Heave" ? ShotStyle.NotSet : ShotStyle.JumpShot;
            var modifiers = Enum.GetValues<ShotModifier>().Where(m => m is not (ShotModifier.NotSet or ShotModifier.Other)
                && SourceJson.Normalize(subtype + " " + type).Contains(SourceJson.Normalize(m.ToString()), StringComparison.Ordinal)).ToArray();
            if (outcome == ShotOutcome.NotSet || value == FieldGoalType.NotSet) { Add("SHOT.UNRESOLVED", CheckStatus.NotAssessable, index, "Shot value/result unknown; placeholder zero is not a missed three.", id); }
            return new FieldGoalEvent(id, sequence, clock, null, team.Value, player, value, outcome,
                assist, null, null, null, style, modifiers, CourtArea.NotSet,
                type == "Heave" ? FieldGoalAttemptCredit.Team : FieldGoalAttemptCredit.Player);
        }
        if (type == "Free Throw")
        {
            if (team is null || player is null) { return null; }
            var match = Regex.Match(subtype, @"(\d+) of (\d+)");
            var category = subtype.Contains("Technical", StringComparison.Ordinal) ? FreeThrowCategory.Technical
                : subtype.Contains("Flagrant", StringComparison.Ordinal) ? FreeThrowCategory.Flagrant
                : subtype.Contains("Clear Path", StringComparison.Ordinal) ? FreeThrowCategory.ClearPath : FreeThrowCategory.Ordinary;
            var outcome = description.StartsWith("MISS ", StringComparison.Ordinal) ? ShotOutcome.Missed
                : Regex.IsMatch(description, @"\(\d+ PTS\)") ? ShotOutcome.Made : ShotOutcome.NotSet;
            return new FreeThrowEvent(id, sequence, clock, null, team.Value, player.Value, outcome,
                match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                match.Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null, null, category);
        }
        if (type == "Substitution")
        {
            var match = Regex.Match(description, @"^SUB: (.+) FOR (.+)$");
            var incoming = match.Success ? ByName(match.Groups[1].Value, person?.Team, index) : null;
            if (team is null || player is null || incoming is null) { return null; }
            return new SubstitutionEvent(id, sequence, clock, null, team.Value, player.Value, incoming.Id);
        }
        if (type == "Foul")
        {
            var foul = Foul(subtype);
            var participants = new List<FoulParticipant>();
            if (coach is not null && !teams.ContainsKey(coach.Team))
            {
                Add("IDENTITY.COACH_TEAM", CheckStatus.NotAssessable, index, $"Coach {coach.Name} ({coach.Id}) resolved in registry; charged team and game-team metadata unavailable.", id);
                return null;
            }
            participants.Add(new(team, player, coach?.Id));
            if (foul is FoulType.DoublePersonal or FoulType.DoubleTechnical)
            {
                var names = description[(description.IndexOf(" - ", StringComparison.Ordinal) + 3)..].Split(',');
                if (names.Length != 2) { return null; }
                var resolved = names.Select(n => ByName(Regex.Replace(n, @"\s*\(.*", "").Trim(), null, index)).ToArray();
                if (resolved.Any(p => p is null)) { return null; }
                participants = resolved.Select(p => new FoulParticipant(teams[p!.Team], p.Id, null)).ToList();
            }
            if (player is null && coach is null && row.Key("personId") != "" && !teams.ContainsKey(row.Key("personId")))
            {
                Add("IDENTITY.FOUL_ACTOR", CheckStatus.NotAssessable, index, "Unresolved charged actor; not fabricated as a player.", id);
            }
            return new FoulEvent(id, sequence, clock, null, participants, null, foul, null);
        }
        if (type == "Rebound")
        {
            return new ReboundEvent(id, sequence, clock, null, team, player, ReboundType.NotSet,
                player is not null ? ReboundCredit.Player : team is not null ? ReboundCredit.Team : ReboundCredit.NotSet, null, null);
        }
        if (type == "Turnover" && team is not null)
        {
            return new TurnoverEvent(id, sequence, clock, null, team.Value, player, Turnover(subtype), null);
        }
        if (type == "period" && subtype is "start" or "end")
        {
            return new PeriodBoundaryEvent(id, sequence, clock, null, subtype == "start" ? PeriodMarker.Start : PeriodMarker.End);
        }
        if (type == "Timeout")
        {
            return new TimeoutEvent(id, sequence, clock, null, team, subtype switch
            { "Regular" => TimeoutType.Team, "Coach Challenge" => TimeoutType.CoachChallenge, "Official" => TimeoutType.Official, _ => TimeoutType.Other });
        }
        if (type == "Instant Replay")
        {
            return new ReviewEvent(id, sequence, clock, null, team,
                subtype.Contains("Coach Challenge", StringComparison.Ordinal) ? ReviewType.CoachChallenge
                : subtype == "Replay Center" ? ReviewType.ReplayCenter : subtype == "Altercation Ruling" ? ReviewType.Altercation : ReviewType.OfficialReview,
                subtype.Contains("Overturn", StringComparison.Ordinal) ? ReviewOutcome.Overturned
                : subtype.Contains("Support", StringComparison.Ordinal) ? ReviewOutcome.Supported
                : subtype.Contains("Stands", StringComparison.Ordinal) ? ReviewOutcome.RulingStands : ReviewOutcome.NotSet, null);
        }
        if (type == "Violation")
        {
            return new ViolationEvent(id, sequence, clock, null, team, player, subtype switch
            { "Defensive Goaltending" => ViolationType.DefensiveGoaltending, "Lane" => ViolationType.Lane,
                "Double Lane" => ViolationType.DoubleLane, "Jump Ball" => ViolationType.JumpBall,
                "Kicked Ball" => ViolationType.KickedBall, "Delay Of Game" => ViolationType.DelayOfGame, _ => ViolationType.Other }, null, null);
        }
        if (type == "Ejection")
        {
            if (coach is not null && !teams.ContainsKey(coach.Team)) { return null; }
            if (player is null && coach is null) { return null; }
            return new EjectionEvent(id, sequence, clock, null, team, player, coach?.Id);
        }
        if (type == "Jump Ball")
        {
            var match = Regex.Match(description, @"^Jump Ball (?:\(CC\) )?(.+) vs\. (.+): Tip to (.+)$");
            var second = match.Success ? ByName(match.Groups[2].Value, null, index) : null;
            var winner = match.Success ? ByName(match.Groups[3].Value, null, index) : null;
            return new JumpBallEvent(id, sequence, clock, null, player, second?.Id,
                winner is null ? null : teams[winner.Team], winner?.Id);
        }
        return null;
    }

    private void AttachCredits(List<BasketballPlayByPlayEvent> events, List<SourceRowDisposition> dispositions)
    {
        foreach (var disposition in dispositions.Where(x => x.Disposition == "PendingCredit").ToArray())
        {
            var row = disposition.Original;
            BasketballClock creditClock;
            try { creditClock = Clock(row); }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                dispositions[disposition.RowIndex] = disposition with { Disposition = "Unresolved", Explanation = exception.Message };
                Add("CREDIT.UNRESOLVED", CheckStatus.NotAssessable, disposition.RowIndex, "Auxiliary row has an invalid clock.");
                continue;
            }
            var block = row.Text("description").Contains("BLOCK", StringComparison.Ordinal);
            var actor = players.GetValueOrDefault(row.Key("personId"));
            var candidates = events.Where(e => e.Clock == creditClock && rows[e.SequenceNumber - 1].Text("actionNumber") == row.Text("actionNumber")
                && (block ? e is FieldGoalEvent { Outcome: ShotOutcome.Missed or ShotOutcome.NotSet } : e is TurnoverEvent)).ToArray();
            if (actor is not null && candidates.Length == 1)
            {
                var target = candidates[0];
                var targetTeam = target is FieldGoalEvent fg ? fg.TeamId : ((TurnoverEvent)target).TeamId;
                var creditAlreadySet = target is FieldGoalEvent f ? f.BlockingPlayerId is not null : ((TurnoverEvent)target).StealingPlayerId is not null;
                if (targetTeam != teams[actor.Team] && !creditAlreadySet)
                {
                    var position = events.IndexOf(target);
                    events[position] = target is FieldGoalEvent shot ? shot with { BlockingPlayerId = actor.Id }
                        : ((TurnoverEvent)target) with { StealingPlayerId = actor.Id };
                    dispositions[disposition.RowIndex] = disposition with { Disposition = "Supporting", EventIds = [target.EventId], Explanation = "Unique opposing primary event at same clock/actionNumber; single statistical credit." };
                    evidence.Add(new(target.EventId, block ? "BlockingPlayerId" : "StealingPlayerId", BasketballEvidenceKind.Linked,
                        [Row(target.SequenceNumber - 1), Row(disposition.RowIndex)], "Same-clock actionNumber grouping plus opposite-team actor and unique compatible primary event."));
                    continue;
                }
            }
            dispositions[disposition.RowIndex] = disposition with { Disposition = "Unresolved", Explanation = "No unique compatible primary event or duplicate/conflicting credit; no second event generated." };
            Add("CREDIT.UNRESOLVED", CheckStatus.NotAssessable, disposition.RowIndex, "Auxiliary credit unresolved; affected credit reconciliation cannot pass.");
        }
    }

    private void LinkEvents(List<BasketballPlayByPlayEvent> events, HashSet<int> unresolved)
    {
        var reboundCounts = new Dictionary<long, (int Off, int Def)>();
        for (var i = 0; i < events.Count; i++)
        {
            var current = events[i];
            var sameClock = events.Take(i).Reverse().TakeWhile(e => e.Clock == current.Clock
                && !unresolved.Any(s => s > e.SequenceNumber && s < current.SequenceNumber)).ToArray();
            if (current is FreeThrowEvent ft)
            {
                // A generic numbered FT label does not exclude a retained-possession award.
                var special = sameClock.OfType<FoulEvent>().Where(f => f.CommittingParticipants.All(p => p.TeamId is not null && p.TeamId != ft.TeamId)
                    && f.FoulType is FoulType.TransitionTake or FoulType.AwayFromPlay or FoulType.DefensiveThreeSeconds).ToArray();
                if (ft.Category == FreeThrowCategory.Ordinary && special.Length == 1)
                {
                    ft = ft with { Category = special[0].FoulType switch
                    { FoulType.TransitionTake => FreeThrowCategory.TransitionTake, FoulType.AwayFromPlay => FreeThrowCategory.AwayFromPlay, _ => FreeThrowCategory.DefensiveThreeSeconds } };
                    events[i] = ft;
                    Link(ft.EventId, "Category", ft.SequenceNumber - 1, special[0].SequenceNumber - 1);
                }
                var fouls = sameClock.OfType<FoulEvent>().Where(f => f.CommittingParticipants.All(p => p.TeamId is not null && p.TeamId != ft.TeamId)
                    && Compatible(f.FoulType, ft.Category)).ToArray();
                if (fouls.Length == 1) { events[i] = ft with { ParentFoulEventId = fouls[0].EventId }; Link(ft.EventId, "ParentFoulEventId", ft.SequenceNumber - 1, fouls[0].SequenceNumber - 1); }
            }
            if (current is FoulEvent { FoulType: FoulType.Shooting } foul)
            {
                var attempts = sameClock.OfType<FieldGoalEvent>().Where(s => foul.CommittingParticipants.All(p => p.TeamId is not null && p.TeamId != s.TeamId)).ToArray();
                if (attempts.Length == 1) { events[i] = foul with { RelatedFieldGoalEventId = attempts[0].EventId }; Link(foul.EventId, "RelatedFieldGoalEventId", foul.SequenceNumber - 1, attempts[0].SequenceNumber - 1); }
            }
            if (current is ReboundEvent rebound)
            {
                var prior = events.Take(i).LastOrDefault(e => e is FieldGoalEvent or FreeThrowEvent or ReboundEvent or TurnoverEvent or PeriodBoundaryEvent);
                var fg = prior as FieldGoalEvent;
                var ftPrior = prior as FreeThrowEvent;
                var missed = fg?.Outcome == ShotOutcome.Missed || ftPrior?.Outcome == ShotOutcome.Missed && ftPrior.Category == FreeThrowCategory.Ordinary && ftPrior.AttemptNumber == ftPrior.TotalAttempts && ftPrior.TotalAttempts is not null;
                var subtype = ReboundType.NotSet;
                var match = Regex.Match(rows[current.SequenceNumber - 1].Text("description"), @"Off:(\d+) Def:(\d+)");
                if (rebound.ReboundingPlayerId is long player && match.Success)
                {
                    var off = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    var def = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                    var previous = reboundCounts.GetValueOrDefault(player);
                    if (off == previous.Off + 1 && def == previous.Def) { subtype = ReboundType.Offensive; }
                    if (def == previous.Def + 1 && off == previous.Off) { subtype = ReboundType.Defensive; }
                    reboundCounts[player] = (off, def);
                }
                if (missed && !unresolved.Any(s => s > prior!.SequenceNumber && s < current.SequenceNumber)
                    && prior!.Clock.Period == current.Clock.Period && prior.Clock.OvertimePeriodNumber == current.Clock.OvertimePeriodNumber)
                {
                    events[i] = rebound with { ReboundType = subtype, RelatedFieldGoalEventId = fg?.EventId, RelatedFreeThrowEventId = ftPrior?.EventId };
                    Link(rebound.EventId, fg is null ? "RelatedFreeThrowEventId" : "RelatedFieldGoalEventId", rebound.SequenceNumber - 1, prior.SequenceNumber - 1);
                }
                else { events[i] = rebound with { ReboundType = subtype }; }
                if (subtype != ReboundType.NotSet) { evidence.Add(new(rebound.EventId, "ReboundType", BasketballEvidenceKind.Parsed, [Row(rebound.SequenceNumber - 1)], "Off/Def description counter delta; raw Unknown subtype is not decisive.")); }
            }
            if (current is ViolationEvent { ViolationType: ViolationType.DefensiveGoaltending } violation)
            {
                // Prefer the single compatible made attempt in the uninterrupted same-clock award.
                // Including FTs is essential; a nearby FG alone is not a universal matching rule.
                var segment = events.Take(i).Reverse().TakeWhile(e => e.Clock == current.Clock && e is not (ReboundEvent or TurnoverEvent or PeriodBoundaryEvent or JumpBallEvent)).ToArray();
                var latestAttempt = segment.FirstOrDefault(e => e is FreeThrowEvent or FieldGoalEvent);
                var targetTeam = latestAttempt is FieldGoalEvent s ? s.TeamId : (latestAttempt as FreeThrowEvent)?.TeamId;
                var made = latestAttempt is FieldGoalEvent { Outcome: ShotOutcome.Made } or FreeThrowEvent { Outcome: ShotOutcome.Made };
                if (made && latestAttempt!.SequenceNumber == violation.SequenceNumber - 1
                    && targetTeam is not null && violation.TeamId is not null && targetTeam != violation.TeamId
                    && rows[latestAttempt!.SequenceNumber - 1].Text("description").Contains("PTS", StringComparison.Ordinal))
                {
                    events[i] = violation with { RelatedFieldGoalEventId = (latestAttempt as FieldGoalEvent)?.EventId,
                        RelatedFreeThrowEventId = (latestAttempt as FreeThrowEvent)?.EventId };
                    Link(violation.EventId, latestAttempt is FreeThrowEvent ? "RelatedFreeThrowEventId" : "RelatedFieldGoalEventId",
                        violation.SequenceNumber - 1, latestAttempt.SequenceNumber - 1);
                }
                else { Add("LINK.GOALTENDING", CheckStatus.NotAssessable, violation.SequenceNumber - 1, "No supported credited made attempt in the same uninterrupted clock group.", violation.EventId); }
            }
        }
    }

    private static bool Compatible(FoulType foul, FreeThrowCategory category) => category switch
    {
        FreeThrowCategory.Technical => foul is FoulType.Technical or FoulType.DoubleTechnical or FoulType.DefensiveThreeSeconds or FoulType.DelayTechnical or FoulType.Flopping or FoulType.Bench,
        FreeThrowCategory.Flagrant => foul is FoulType.FlagrantOne or FoulType.FlagrantTwo,
        FreeThrowCategory.ClearPath => foul == FoulType.ClearPath,
        FreeThrowCategory.TransitionTake => foul == FoulType.TransitionTake,
        FreeThrowCategory.AwayFromPlay => foul == FoulType.AwayFromPlay,
        FreeThrowCategory.DefensiveThreeSeconds => foul == FoulType.DefensiveThreeSeconds,
        FreeThrowCategory.Ordinary => foul is FoulType.Shooting or FoulType.Personal or FoulType.LooseBall or FoulType.PersonalTake,
        _ => false
    };

    private void ExplainFields(BasketballPlayByPlayEvent converted, int index, HashSet<(long, string)> explained)
    {
        foreach (var property in converted.GetType().GetProperties().Where(p => p.Name is not ("EventId" or "SequenceNumber")))
        {
            if (!explained.Add((converted.EventId, property.Name))) { continue; }
            var value = property.GetValue(converted);
            var missing = value is null || value is Enum e && System.Convert.ToInt32(e, CultureInfo.InvariantCulture) == 0;
            evidence.Add(new(converted.EventId, property.Name, missing ? BasketballEvidenceKind.Unresolved
                : property.Name == "Clock" ? BasketballEvidenceKind.Transformed : BasketballEvidenceKind.Parsed,
                [Row(index)], missing ? "Unavailable, unresolved or inapplicable; original row retained. No default fact inferred."
                    : "Source fields/description interpreted with typed NBA semantics; identities resolve through the versioned registry."));
        }
    }

    private FoulCountObservation[] FoulCounts(List<BasketballPlayByPlayEvent> events)
    {
        var result = new List<FoulCountObservation>();
        foreach (var foul in events.OfType<FoulEvent>())
        {
            var row = rows[foul.SequenceNumber - 1];
            if (foul.FoulType == FoulType.DoublePersonal)
            {
                var matches = Regex.Matches(row.Text("description"), @"([^,\-]+) \((\d+) PF\)");
                foreach (Match match in matches)
                {
                    var person = ByName(match.Groups[1].Value.Trim(), null, foul.SequenceNumber - 1);
                    if (person is not null) { result.Add(new(foul.SequenceNumber, person.Id, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), foul.SequenceNumber - 1)); }
                }
            }
            else
            {
                var match = Regex.Match(row.Text("description"), @"\(P(\d+)(?:[.)])");
                var person = players.GetValueOrDefault(row.Key("personId"));
                if (match.Success && person is not null) { result.Add(new(foul.SequenceNumber, person.Id, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), foul.SequenceNumber - 1)); }
            }
        }
        return result.ToArray();
    }

    private void Link(long id, string field, int row, int supporting) => evidence.Add(new(id, field,
        BasketballEvidenceKind.Linked, [Row(row), Row(supporting)], "Compatible source event types, same-clock context and team relationships; ambiguity leaves link unset."));

    private BasketballSourceRowReference Row(int index) => new(BasketballDataSource.NbaOfficial, snapshot, index, rows[index].Key("actionId"), rows[index].Key("actionNumber"));
    private static SourceEntityIdentifier Source(string id) => new(BasketballDataSource.NbaOfficial, id);
    private long Resolve(string type, string key, string reason) => registry.Resolve(type, Provider, key, reason, establishedNew: true).EntityId!.Value;
    private long? TeamId(JsonElement row) => teams.TryGetValue(row.Key("teamId"), out var team) ? team
        : teams.TryGetValue(row.Key("personId"), out team) ? team
        : row.Text("location") == "h" ? teams[homeSource] : row.Text("location") == "v" ? teams[awaySource] : null;

    private Person? ByName(string name, string? team, int row)
    {
        var normalized = SourceJson.Normalize(name);
        var matches = players.Values.Where(p => (team is null || p.Team == team) && p.Aliases.Contains(normalized)).ToArray();
        if (matches.Length == 1) { return matches[0]; }
        Add("IDENTITY.NAME", CheckStatus.NotAssessable, row, $"Name '{name}' has {matches.Length} compatible game-roster matches; not guessed.");
        return null;
    }

    private void Add(string code, CheckStatus status, int row, string reason, long? id = null) =>
        checks.Add(new(code, status, reason, id, Severity: status == CheckStatus.Fail ? FindingSeverity.Warning : null, SourceRows: [row]));

    private static BasketballClock Clock(JsonElement row) => new(row.Number("period") <= 4 ? (BasketballPeriod)row.Number("period") : BasketballPeriod.Overtime,
        row.Number("period") <= 4 ? null : row.Number("period") - 4, TimeSpan.FromSeconds(SourceJson.Remaining(row.Text("clock"))));

    private static FoulType Foul(string subtype) => subtype switch
    {
        "Personal" => FoulType.Personal, "Shooting" => FoulType.Shooting, "Offensive" => FoulType.Offensive,
        "Offensive Charge" => FoulType.OffensiveCharge, "Loose Ball" => FoulType.LooseBall, "Technical" => FoulType.Technical,
        "Flagrant Type 1" => FoulType.FlagrantOne, "Flagrant Type 2" => FoulType.FlagrantTwo, "Clear Path" => FoulType.ClearPath,
        "Away From Play" => FoulType.AwayFromPlay, "Transition Take" => FoulType.TransitionTake, "Personal Take" => FoulType.PersonalTake,
        "Double Personal" => FoulType.DoublePersonal, "Double Technical" => FoulType.DoubleTechnical,
        "Defense 3 Second" => FoulType.DefensiveThreeSeconds, "Bench" => FoulType.Bench, "Delay Technical" => FoulType.DelayTechnical,
        "Excess Timeout Technical" => FoulType.ExcessTimeoutTechnical, "Flopping" => FoulType.Flopping,
        "Hanging Technical" => FoulType.HangingTechnical, "Non-Unsportsmanlike Technical" => FoulType.NonUnsportsmanlikeTechnical,
        "Too Many Players Technical" => FoulType.TooManyPlayersTechnical, _ => FoulType.Other
    };

    private static TurnoverType Turnover(string subtype) => subtype switch
    {
        "Bad Pass" => TurnoverType.BadPass, "Lost Ball" => TurnoverType.LostBall, "Traveling" => TurnoverType.Traveling,
        "Double Dribble" => TurnoverType.DoubleDribble, "Palming Turnover" => TurnoverType.Carrying,
        "Offensive Foul Turnover" => TurnoverType.OffensiveFoul, "Backcourt Turnover" => TurnoverType.Backcourt,
        "Shot Clock Turnover" => TurnoverType.ShotClock, "3 Second Violation" => TurnoverType.ThreeSeconds,
        "5 Second Violation" => TurnoverType.FiveSeconds, "8 Second Violation" => TurnoverType.EightSeconds,
        "10 Second Violaton" => TurnoverType.TenSeconds, "Discontinue Dribble" => TurnoverType.DiscontinueDribble,
        "Offensive Goaltending" => TurnoverType.OffensiveGoaltending, "Out of Bounds - Bad Pass Turnover" => TurnoverType.OutOfBoundsBadPass,
        "Out of Bounds Lost Ball Turnover" => TurnoverType.OutOfBoundsLostBall, "Step Out of Bounds Turnover" => TurnoverType.StepOutOfBounds,
        "Inbound Turnover" => TurnoverType.Inbound, "Illegal Assist Turnover" => TurnoverType.IllegalAssist,
        "Jump Ball Violation" => TurnoverType.JumpBallViolation, "Kicked Ball Violation" => TurnoverType.KickedBallViolation,
        "Lane Violation" => TurnoverType.LaneViolation, "Excess Timeout Turnover" => TurnoverType.ExcessTimeout,
        "Too Many Players Turnover" => TurnoverType.TooManyPlayers, "Basket from Below Turnover" => TurnoverType.BasketFromBelow,
        "Punched Ball Turnover" => TurnoverType.PunchedBall, _ => TurnoverType.Other
    };

    private static CoverageGap Gap(JsonElement row, int sequence) => row.Text("actionType") switch
    {
        "Substitution" => new(sequence, ["MIN", "+/-"], true, false, "Unresolved substitution"),
        "Foul" or "Ejection" => new(sequence, ["PF"], false, true, "Unresolved charged/ejected actor"),
        "" => new(sequence, ["STL", "BLK"], false, true, "Unresolved auxiliary credit"),
        "Timeout" or "Instant Replay" => new(sequence, [], false, true, "Unresolved administrative context"),
        _ => new(sequence, ["PTS", "FGA", "FGM", "3PA", "3PM", "FTA", "FTM", "OREB", "DREB", "REB", "AST", "STL", "BLK", "TOV", "PF", "MIN", "+/-"], true, true, "Unresolved basketball history")
    };
}
