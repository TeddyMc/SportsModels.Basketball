using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;
using SportsModels.Data.Basketball.Application;
using SportsModels.Data.Basketball.Contracts;
using SportsModels.Data.Basketball.Infrastructure;
using SportsModels.Data.NbaOfficial.Application;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Infrastructure;

namespace SportsModels.Data.NbaOfficial.Tests.Unit;

[TestFixture]
public sealed class ConversionTests
{
    private string directory = "";
    private SqliteIdentityRegistry registry = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "basketball-conversion-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        registry = new SqliteIdentityRegistry(Path.Combine(directory, "registry.sqlite"));
    }

    [TearDown]
    public void TearDown()
    {
        registry.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(directory, true);
    }

    [Test]
    public void PersistentIdentityKeysAreTypedAndDoNotCastProviderIds()
    {
        var player = registry.Resolve("Player", "NbaOfficial", "1320", "Verified player fixture", true);
        var coach = registry.Resolve("Coach", "NbaOfficial", "1320", "Verified coach fixture", true);
        Assert.That(player.EntityId, Is.Not.EqualTo(1320));
        Assert.That(coach.EntityId, Is.Not.EqualTo(player.EntityId));
        using var reopened = new SqliteIdentityRegistry(Path.Combine(directory, "registry.sqlite"));
        Assert.That(reopened.Resolve("Player", "NbaOfficial", "1320", "Lookup").EntityId, Is.EqualTo(player.EntityId));
        Assert.That(reopened.RegistryId, Is.EqualTo(registry.RegistryId));
    }

    [Test]
    public void AmbiguousMappingsAreNotAllocatedAndMappingsCannotBeReassigned()
    {
        Assert.That(registry.Resolve("Player", "Other", "ambiguous", "Name-only candidate").EntityId, Is.Null);
        var first = registry.Resolve("Player", "NbaOfficial", "1", "Verified provider key", true);
        var second = registry.Resolve("Player", "NbaOfficial", "2", "Verified provider key", true);
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("Player", "NbaOfficial", "1", "Attempted reassignment", confirmedEntityId: second.EntityId));
        Assert.That(registry.Resolve("Player", "Other", "confirmed", "Manually corroborated mapping", confirmedEntityId: first.EntityId).EntityId, Is.EqualTo(first.EntityId));
    }

    [Test]
    public void RegistryGameTransactionRollsBackOnFailure()
    {
        var revision = registry.Revision;
        Assert.Throws<InvalidDataException>(() => registry.InTransaction<int>(() =>
        {
            registry.Resolve("Player", "NbaOfficial", "1", "Will roll back", true);
            throw new InvalidDataException("Seeded failure");
        }));
        Assert.That(registry.Revision, Is.EqualTo(revision));
        Assert.That(registry.Resolve("Player", "NbaOfficial", "1", "Lookup").EntityId, Is.Null);
    }

    [Test]
    public void EventCorrespondenceRequiresUnchangedRowAndUniqueActionId()
    {
        var first = registry.ResolveEvent("NBA", "game", "snapshot1", "0", "12", "row-hash", true);
        Assert.That(registry.ResolveEvent("NBA", "game", "snapshot1", "0", "12", "row-hash", true).EntityId, Is.EqualTo(first.EntityId));
        Assert.That(registry.ResolveEvent("NBA", "game", "snapshot2", "5", "12", "row-hash", true).EntityId, Is.EqualTo(first.EntityId));
        var changed = registry.ResolveEvent("NBA", "game", "snapshot3", "0", "12", "changed-row", true);
        Assert.That(changed.EntityId, Is.Not.EqualTo(first.EntityId));
        Assert.That(changed.Status, Is.EqualTo("SnapshotScoped"));
        Assert.That(registry.ResolveEvent("NBA", "game", "snapshot4", "0", "12", "row-hash", false).EntityId, Is.Not.EqualTo(first.EntityId));
    }

    [TestCase("0022500673")]
    [TestCase("0022501122")]
    [TestCase("0022501045")]
    [TestCase("0022500304")]
    [TestCase("0022500173")]
    [TestCase("0022501006")]
    [TestCase("0022500732")]
    public void ActualSelectedSourceRowsAreAccountedForAndRerunIdentitiesRemainStable(string gameId)
    {
        var bytes = SourceCase(gameId);
        var converted = Convert(bytes);
        Assert.That(converted.Rows.Select(r => r.RowIndex), Is.EqualTo(Enumerable.Range(0, converted.Rows.Count)));
        Assert.That(converted.Rows.Any(r => r.Disposition == "PendingCredit"), Is.False);
        Assert.That(converted.Rows.Where(r => r.Disposition == "Unresolved").All(r => r.EventIds.Count == 0), Is.True);
        Assert.That(Convert(bytes).Game.Events.Select(e => e.EventId), Is.EqualTo(converted.Game.Events.Select(e => e.EventId)));
        var json = JsonSerializer.SerializeToUtf8Bytes(converted.Game, ConversionWorkspace.JsonOptions);
        Assert.That(JsonSerializer.Deserialize<BasketballGamePlayByPlay>(json, ConversionWorkspace.JsonOptions)!.Events.Select(e => e.GetType()),
            Is.EqualTo(converted.Game.Events.Select(e => e.GetType())));
    }

    [Test]
    public void SourceFreeThrowInterruptionAndGoaltendingHaveTypedLinksWithoutDuplicateScoring()
    {
        var converted = Convert(SourceCase("0022500673"));
        var first = converted.Game.Events.OfType<FreeThrowEvent>().First(e => e.TotalAttempts == 2 && e.AttemptNumber == 1);
        var second = converted.Game.Events.OfType<FreeThrowEvent>().First(e => e.Clock == first.Clock && e.AttemptNumber == 2);
        Assert.That(converted.Game.Events.OfType<SubstitutionEvent>().Any(e => e.SequenceNumber > first.SequenceNumber && e.SequenceNumber < second.SequenceNumber), Is.True);
        Assert.That(first.ParentFoulEventId, Is.Not.Null.And.EqualTo(second.ParentFoulEventId));
        Assert.That(converted.Game.Events.OfType<ViolationEvent>().Single(v => v.ViolationType == ViolationType.DefensiveGoaltending).RelatedFieldGoalEventId, Is.Not.Null);
        Assert.That(converted.Evidence.GroupBy(e => (e.EventId, e.FieldName)).All(g => g.Count() == 1), Is.True);
    }

    [Test]
    public void ActualReferenceResponsesRejectIgnoredPeriodFilterAndParseCoachIds()
    {
        var bundle = new ConversionWorkspace().ReadReferences(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "references"), "0022500173");
        Assert.That(bundle.Coaches.Any(c => c.CoachId == "1320" && c.TeamId == "1610612754"), Is.True);
        var observations = bundle.Observations(s => long.Parse(s), s => long.Parse(s), true);
        Assert.That(observations.Statistics.All(s => s.Period == 0), Is.True);
        Assert.That(bundle.Checks.Any(c => c.Code == "REFERENCE.PERIOD_FILTER" && c.Status == CheckStatus.NotAssessable), Is.True);
        Assert.That(observations.Lineups.All(l => l.Period == 1 && l.Players.Count == 5), Is.True);
    }

    [Test]
    public void DoublePersonalAndDoubleTechnicalKeepBothChargedPlayers()
    {
        foreach (var game in new[] { "0022501122", "0022501045" })
        {
            var foul = Convert(SourceCase(game)).Game.Events.OfType<FoulEvent>()
                .Single(f => f.FoulType is FoulType.DoublePersonal or FoulType.DoubleTechnical);
            Assert.That(foul.CommittingParticipants.Select(p => p.PlayerId).Distinct().Count(), Is.EqualTo(2));
            Assert.That(foul.FouledPlayerId, Is.Null);
        }
    }

    [Test]
    public void ActualSecondOvertimeKeepsFractionalClockAndOneBlockCredit()
    {
        var game = Convert(SourceCase("0022500304")).Game;
        var shot = game.Events.OfType<FieldGoalEvent>().Single(e => e.Clock.OvertimePeriodNumber == 2 && e.Clock.ClockRemaining == TimeSpan.FromSeconds(59.6));
        Assert.That(shot.BlockingPlayerId, Is.Not.Null);
        Assert.That(game.Events.OfType<PeriodBoundaryEvent>().Any(e => e.Clock.OvertimePeriodNumber == 2), Is.True);
    }

    [Test]
    public void ActualFreeThrowGoaltendingLinksTheFreeThrowRatherThanAnEarlierFieldGoal()
    {
        var game = Convert(SourceCase("0022500732")).Game;
        var violation = game.Events.OfType<ViolationEvent>().Single(e => e.ViolationType == ViolationType.DefensiveGoaltending);
        Assert.That(violation.RelatedFieldGoalEventId, Is.Null);
        Assert.That(violation.RelatedFreeThrowEventId, Is.Not.Null);
        var ft = game.Events.OfType<FreeThrowEvent>().Single(e => e.EventId == violation.RelatedFreeThrowEventId);
        Assert.That(ft.Outcome, Is.EqualTo(ShotOutcome.Made));
        Assert.That(ft.AttemptNumber, Is.EqualTo(2));
    }

    [Test]
    public void ActualMadeLongRangeShotAndTeamHeaveAreSeparateCreditedAttempts()
    {
        var game = Convert(SourceCase("0022501006")).Game;
        Assert.That(game.Events.OfType<FieldGoalEvent>().Any(e => e.Outcome == ShotOutcome.Made && e.FieldGoalType == FieldGoalType.ThreePointer), Is.True);
        var heave = game.Events.OfType<FieldGoalEvent>().Single(e => e.AttemptCredit == FieldGoalAttemptCredit.Team);
        Assert.That(heave.Outcome, Is.EqualTo(ShotOutcome.NotSet));
        Assert.That(heave.ShooterPlayerId, Is.Null);
        Assert.That(heave.FieldGoalType, Is.EqualTo(FieldGoalType.NotSet));
        Assert.That(game.Events.OfType<FieldGoalEvent>().Any(e => e.ShotDistanceFeet > 30), Is.True);
    }

    [Test]
    public void CoachRosterResolvesCoachMetadataWhileMissingChargeTeamRemainsNull()
    {
        var references = new ReferenceBundle();
        references.Coaches.Add(("1610612754", "1320", "Rick Carlisle"));
        var game = Convert(SourceCase("0022500173"), references).Game;
        var coach = game.HomeTeam.Coaches.Single(c => c.Name == "Rick Carlisle");
        Assert.That(game.Events.OfType<EjectionEvent>().Single().CoachId, Is.EqualTo(coach.CoachId));
        Assert.That(game.Events.OfType<FoulEvent>().Where(f => f.CommittingParticipants.Any(p => p.CoachId == coach.CoachId))
            .SelectMany(f => f.CommittingParticipants).All(p => p.TeamId is null && p.PlayerId is null), Is.True);
    }

    [Test]
    public async Task BatchRetainsMalformedInputAndContinuesOtherGamesInImmutableRuns()
    {
        var input = Path.Combine(directory, "input");
        Directory.CreateDirectory(input);
        var good = SourceCase("0022501122");
        await File.WriteAllBytesAsync(Path.Combine(input, "good.json"), good);
        var bad = Encoding.UTF8.GetBytes("{broken");
        await File.WriteAllBytesAsync(Path.Combine(input, "bad.json"), bad);
        var service = new NbaConversion(registry, new BasketballValidator(), new ConversionWorkspace());
        var request = new ConversionRequest(input, Path.Combine(directory, "missing-refs"), Path.Combine(directory, "output"));
        var first = await service.RunAsync(request);
        var firstManifest = await File.ReadAllBytesAsync(Path.Combine(first.RunDirectory, "manifest.json"));
        var second = await service.RunAsync(request);
        Assert.That(first.Failed, Is.EqualTo(1));
        Assert.That(first.Converted, Is.EqualTo(1));
        Assert.That(second.RunId, Is.Not.EqualTo(first.RunId));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(first.RunDirectory, "manifest.json")), Is.EqualTo(firstManifest));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(first.RunDirectory, "inputs", SourceJson.Hash(bad) + ".json")), Is.EqualTo(bad));
        Assert.That(await File.ReadAllBytesAsync(Path.Combine(input, "good.json")), Is.EqualTo(good));
        Assert.That(File.Exists(Path.Combine(first.RunDirectory, "registry.sqlite")), Is.True);
    }

    [Test]
    public void ExactDuplicateRowsDoNotDuplicateAttemptsAndConflictingVersionsRemainUnresolved()
    {
        var node = JsonNode.Parse(SourceCase("0022501006"))!;
        var actions = node["game"]!["actions"]!.AsArray();
        var shot = actions.First(a => a!["actionType"]!.GetValue<string>() == "Made Shot")!;
        actions.Add(shot.DeepClone());
        var duplicate = Convert(JsonSerializer.SerializeToUtf8Bytes(node));
        Assert.That(duplicate.Rows.Last().Disposition, Is.EqualTo("Duplicate"));
        var changed = shot.DeepClone();
        changed["description"] = "Conflicting revised description";
        actions.Add(changed);
        var conflicting = Convert(JsonSerializer.SerializeToUtf8Bytes(node));
        var actionId = shot["actionId"]!.ToString();
        Assert.That(conflicting.Rows.Where(r => r.Original.Text("actionId") == actionId)
            .All(r => r.Disposition == "Unresolved" && r.EventIds.Count == 0), Is.True);
    }

    [Test]
    public void UnsupportedCorrectionInstructionStaysOutsideCanonicalStreamWithCoverageGap()
    {
        var node = JsonNode.Parse(SourceCase("0022501006"))!;
        var actions = node["game"]!["actions"]!.AsArray();
        var correction = actions[0]!.DeepClone();
        correction["actionId"] = 99999;
        correction["actionType"] = "Unverified source correction";
        actions.Add(correction);
        var converted = Convert(JsonSerializer.SerializeToUtf8Bytes(node));
        Assert.That(converted.Rows.Last().Disposition, Is.EqualTo("Unresolved"));
        Assert.That(converted.Rows.Last().EventIds, Is.Empty);
        Assert.That(converted.Context.Gaps.Last().Statistics, Does.Contain("PTS"));
    }

    [TestCase("Shooting", FreeThrowCategory.Ordinary, true)]
    [TestCase("Away From Play", FreeThrowCategory.AwayFromPlay, false)]
    public void SingleFreeThrowDoesNotAloneEstablishAnAndOne(string foulType, FreeThrowCategory category, bool linkedShot)
    {
        var node = JsonNode.Parse(SourceCase("0022501122"))!;
        var actions = node["game"]!["actions"]!.AsArray();
        var shot = actions.First(a => a!["actionType"]!.GetValue<string>() == "Made Shot")!.DeepClone();
        var teamId = shot["teamId"]!.ToString();
        var opposing = actions.First(a => a!["teamId"]!.ToString() != teamId && a["teamId"]!.ToString() != "0"
            && a["playerName"]!.ToString() != "")!.DeepClone();
        shot["actionId"] = 90001;
        shot["clock"] = "PT01M00.00S";
        opposing["actionId"] = 90002;
        opposing["clock"] = "PT01M00.00S";
        opposing["actionType"] = "Foul";
        opposing["subType"] = foulType;
        var ft = shot.DeepClone();
        ft["actionId"] = 90003;
        ft["actionType"] = "Free Throw";
        ft["subType"] = "Free Throw 1 of 1";
        ft["description"] = "Fixture Free Throw 1 of 1 (1 PTS)";
        actions.Add(shot);
        actions.Add(opposing);
        actions.Add(ft);
        var game = Convert(JsonSerializer.SerializeToUtf8Bytes(node)).Game;
        var freeThrow = game.Events.OfType<FreeThrowEvent>().Last();
        var foul = game.Events.OfType<FoulEvent>().Single(f => f.EventId == freeThrow.ParentFoulEventId);
        Assert.That(freeThrow.Category, Is.EqualTo(category));
        Assert.That(foul.RelatedFieldGoalEventId is not null, Is.EqualTo(linkedShot));
    }

    private ConvertedGame Convert(byte[] bytes, ReferenceBundle? references = null) =>
        registry.InTransaction(() => new NbaGameConverter(registry).Convert(bytes, SourceJson.Hash(bytes), references ?? new ReferenceBundle()));

    [TestCase(3, true)]
    [TestCase(1, false)]
    public void MatchedReferenceFinalStatusReachesValidation(int status, bool final)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            resultSets = new[] { new { name = "GameSummary", headers = new[] { "GAME_ID", "GAME_STATUS_ID" },
                rowSet = new object[][] { ["0022501122", status] } } }
        });
        var bundle = ReferenceBundle.Parse([("boxscoresummaryv2-p0-fixture.json", bytes)], "0022501122");
        Assert.That(Convert(SourceCase("0022501122"), bundle).Context.Final, Is.EqualTo(final));
    }

    private static byte[] SourceCase(string gameId)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "source-cases.json")));
        var sample = document.RootElement.EnumerateArray().Single(g => g.GetProperty("GameId").GetString() == gameId);
        // These are ordered selections, not complete games; original indices/hash are retained in the fixture.
        return JsonSerializer.SerializeToUtf8Bytes(new { game = new { gameId, actions = sample.GetProperty("Rows").EnumerateArray().Select(r => r.GetProperty("Action")).ToArray() } });
    }

    [TestCase("Jal. Williams","Jalen","Jaylin","Williams",true)]
    [TestCase("Jay. Williams","Jaylin","Jalen","Williams",true)]
    [TestCase("St. Curry","Stephen","Seth","Curry",true)]
    [TestCase("J. Williams","Jalen","Jaylin","Williams",false)]
    [TestCase("S. Curry","Stephen","Seth","Curry",false)]
    [TestCase("Jo. Williams","Jalen","Jaylin","Williams",false)]
    public void LiteralGivenNamePrefixesResolveOnlyUniqueCompatibleGamePlayers(string description,string first,string other,string surname,bool resolved)
    {
        object Player(int id,string given,string family) => new {personId=id,firstName=given,familyName=family,nameI=given[..1]+". "+family,
            position="",statistics=new {minutes="00:00"}};
        var references=new ReferenceBundle();
        references.Boxes.Add((0,"period",JsonSerializer.SerializeToElement(new {gameId="0022500001",
            homeTeam=new {teamId=10,players=new[]{Player(101,"Outgoing","Player"),Player(102,first,surname),Player(103,other,surname)},statistics=new {minutes="240:00"}},
            awayTeam=new {teamId=20,players=new[]{Player(201,"Opponent","Player")},statistics=new {minutes="240:00"}}})));
        var source=JsonSerializer.SerializeToUtf8Bytes(new {game=new {gameId="0022500001",actions=new[]{new {period=1,clock="PT05M00.00S",
            teamId=10,personId=101,location="h",actionType="Substitution",description="SUB: "+description+" FOR Player",actionNumber=10,actionId=1}}}});
        var alternateRows=new[]{
            JsonSerializer.SerializeToElement(new {period=1,clock="PT05M00.00S",teamId=10,personId=101,actionType="substitution",subType="out",actionNumber=10}),
            JsonSerializer.SerializeToElement(new {period=1,clock="PT05M00.00S",teamId=10,personId=102,actionType="substitution",subType="in",actionNumber=11}),
            JsonSerializer.SerializeToElement(new {period=1,clock="PT05M00.00S",teamId=10,personId=103,actionType="substitution",subType="in",actionNumber=12})};
        references.AlternatePlayByPlay.Add(new("prefix-fixture",alternateRows));
        var converted=Convert(source,references);
        Assert.That(converted.Game.Events.OfType<SubstitutionEvent>().Count(),Is.EqualTo(resolved?1:0));
        if(resolved)
        {
            var expected=registry.Resolve("Player","NbaOfficial","102","Lookup").EntityId;
            Assert.That(converted.Game.Events.OfType<SubstitutionEvent>().Single().PlayerInId,Is.EqualTo(expected));
            Assert.That(converted.Evidence.Single(e=>e.FieldName=="PlayerInId").Kind,Is.EqualTo(BasketballEvidenceKind.Linked));
        }
    }

    [TestCase("liveboxscore", """{"game":{"gameId":"0022501122","gameStatus":3}}""")]
    [TestCase("liveplaybyplay", """{"game":{"gameId":"0022501122","actions":[]}}""")]
    public void UnusableOptionalReferenceDoesNotBreakOtherwiseConvertibleGame(string endpoint, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var references = ReferenceBundle.Parse([(endpoint+"-p0-"+SourceJson.Hash(bytes)+".json",bytes)],"0022501122");
        var converted = Convert(SourceCase("0022501122"),references);
        Assert.That(converted.Game.Events, Is.Not.Empty);
        Assert.That(converted.Checks.Any(c => c.Code == "REFERENCE.SCHEMA"), Is.True);
        Assert.That(references.AlternatePlayByPlay, Is.Empty);
        Assert.That(references.Boxes, Is.Empty);
        Assert.That(references.FinalStatusVerified, Is.False);
    }

    [Test]
    public void SavedAlternateIdsResolveIncomingAndExposeMissingBreakRowsWithSnapshotLineage()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "alternate-substitutions.json")));
        var root = fixture.RootElement;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { game = new { gameId = root.Text("GameId"),
            actions = root.GetProperty("PrimaryRows").EnumerateArray().Select(r => r.GetProperty("Action")).ToArray() } });
        var references = new ReferenceBundle();
        var alternateRows = root.GetProperty("AlternateRows").EnumerateArray().Select(r => r.GetProperty("Action").Clone()).ToArray();
        var alternateSnapshot = SourceJson.Hash(JsonSerializer.SerializeToUtf8Bytes(alternateRows));
        references.AlternatePlayByPlay.Add(new(alternateSnapshot, alternateRows));
        var converted = Convert(bytes, references);
        Assert.That(converted.Evidence.Any(e => e.FieldName == "PlayerInId" && e.Kind == BasketballEvidenceKind.Linked
            && e.SourceRows.Count == 3 && e.SourceRows[1].SnapshotId == alternateSnapshot), Is.True);
        Assert.That(converted.Checks.Any(c => c.Code == "REFERENCE.SUBSTITUTION_COVERAGE" && c.Period == 2
            && c.Status == CheckStatus.Fail && c.ReferenceRows!.Count == 1), Is.True);
        Assert.That(converted.Checks.Count(c => c.Code == "REFERENCE.PRIMARY_SUBSTITUTION"),
            Is.EqualTo(converted.Rows.Count(r => r.Original.Text("actionType") == "Substitution")));
        Assert.That(converted.Checks.Any(c => c.Code == "REFERENCE.PRIMARY_SUBSTITUTION" && c.Status == CheckStatus.Pass), Is.True);
        Assert.That(converted.Context.OpeningLineups, Is.Empty, "A selection of rows cannot establish a whole-game lineup replay.");
        var report = new BasketballValidator().Validate(converted.Game, converted.Context);
        var summary = SubstitutionCoverage.Row(root.Text("GameId"), converted, converted.Checks.Concat(report.Checks).ToArray()).Split(',');
        Assert.That(int.Parse(summary[2]), Is.EqualTo(converted.Rows.Count(r => r.Original.Text("actionType") == "Substitution")));
    }
}
