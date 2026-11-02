using System.Text.Json;
using NUnit.Framework;
using SportsModels.Data.Basketball.Contracts;
using SportsModels.Data.NbaOfficial.Application;
using SportsModels.Data.NbaOfficial.Definitions;

namespace SportsModels.Data.NbaOfficial.Tests.Unit;

[TestFixture]
public sealed class AlternateLineupTests
{
    [TestCase(false, 6)]
    [TestCase(true, 3)]
    public void LaterAnchorsRequireCompleteReplayAndEveryPlayerMinuteToAgree(bool wrongMinutes, int expectedAnchors)
    {
        var references = Bundle(wrongMinutes);
        var result = AlternateLineupReplay.Build(references, true, s => long.Parse(s), s => long.Parse(s));
        Assert.That(result.Lineups, Has.Count.EqualTo(expectedAnchors));
        Assert.That(result.Lineups.All(l => l.AfterOpeningClockSubstitutions), Is.True);
        Assert.That(result.Checks.Count(c => c.Code == "REFERENCE.ALTERNATE_MINUTES" && c.Status == CheckStatus.Fail), Is.EqualTo(wrongMinutes ? 1 : 0));
    }

    [Test]
    public void MissingBoundaryWithholdsAnchorsEvenWhenTotalMinutesAgree()
    {
        var references = Bundle(false);
        var original = references.AlternatePlayByPlay[0];
        references.AlternatePlayByPlay[0] = original with { Rows = original.Rows.Skip(1).ToArray() };
        var result = AlternateLineupReplay.Build(references, true, s => long.Parse(s), s => long.Parse(s));
        Assert.That(result.Lineups, Is.Empty);
        Assert.That(result.Checks.Where(c => c.Code == "REFERENCE.ALTERNATE_MINUTES").All(c => c.Status == CheckStatus.NotAssessable), Is.True);
    }

    [Test]
    public void ExplicitStarterFlagTakesPrecedenceOverBenchPosition()
    {
        using var player = JsonDocument.Parse("""{"starter":"0","position":"SF"}""");
        Assert.That(ReferenceBundle.IsStarter(player.RootElement), Is.False);
    }

    [Test]
    public void MultipleAlternateRevisionsCannotSupplyLineups()
    {
        var references = Bundle(false);
        references.AlternatePlayByPlay.Add(references.AlternatePlayByPlay[0] with { SnapshotId = "different" });
        Assert.That(AlternateLineupReplay.Build(references, true, s => long.Parse(s), s => long.Parse(s)).Lineups, Is.Empty);
    }

    [Test]
    public void ContradictoryCompleteStarterSetsCannotSupplyTrustedLineupAnchors()
    {
        var references = Bundle(false);
        var original = references.Boxes[0].Box;
        var node = System.Text.Json.Nodes.JsonNode.Parse(original.GetRawText())!;
        node["homeTeam"]!["players"]![0]!["personId"] = "99";
        references.Boxes.Add((0,"liveboxscore",JsonSerializer.SerializeToElement(node)));
        Assert.That(references.StarterAgreement("10"), Is.False);
        var result = AlternateLineupReplay.Build(references,true,s => long.Parse(s),s => long.Parse(s));
        Assert.That(result.Lineups.Any(l => l.TeamId == 10), Is.False);
    }

    private static ReferenceBundle Bundle(bool wrongMinutes)
    {
        object Team(int first) => new
        {
            teamId = first.ToString(),
            players = Enumerable.Range(first, 5).Select(p => new
            {
                personId = p.ToString(), position = "G", statistics = new { minutes = wrongMinutes && p == first && first == 10 ? "47:59" : "48:00" }
            }).ToArray()
        };
        var box = JsonSerializer.SerializeToElement(new { homeTeam = Team(10), awayTeam = Team(20) });
        var references = new ReferenceBundle();
        references.Boxes.Add((0, "period", box));
        var rows = Enumerable.Range(1, 4).SelectMany(p => new[]
        {
            JsonSerializer.SerializeToElement(new { period = p, clock = "PT12M00.00S", actionType = "period", subType = "start" }),
            JsonSerializer.SerializeToElement(new { period = p, clock = "PT00M00.00S", actionType = "period", subType = "end" })
        }).ToArray();
        references.AlternatePlayByPlay.Add(new("alternate-fixture", rows));
        return references;
    }

    [TestCase(false,CheckStatus.Fail)]
    [TestCase(true,CheckStatus.NotAssessable)]
    public void FinalFiveMismatchRequiresReliableReplay(bool missingBoundary, CheckStatus expected)
    {
        var references = Bundle(false);
        var node = System.Text.Json.Nodes.JsonNode.Parse(references.Boxes[0].Box.GetRawText())!;
        node["gameStatus"] = 3;
        node["gameClock"] = "PT00M00.00S";
        foreach(var side in new[]{"homeTeam","awayTeam"})
        {
            foreach(var player in node[side]!["players"]!.AsArray()) { player!["oncourt"]="1"; player["starter"]="0"; }
        }
        node["homeTeam"]!["players"]![0]!["personId"]="99";
        references.Boxes.Add((0,"liveboxscore",JsonSerializer.SerializeToElement(node)));
        if(missingBoundary)
        {
            var original=references.AlternatePlayByPlay[0];
            references.AlternatePlayByPlay[0]=original with { Rows=original.Rows.Skip(1).ToArray() };
        }
        var result=AlternateLineupReplay.Build(references,true,s=>long.Parse(s),s=>long.Parse(s));
        Assert.That(result.Checks.Single(c=>c.Code=="REFERENCE.ALTERNATE_FINAL_FIVE" && c.EntityId==10).Status,Is.EqualTo(expected));
        Assert.That(result.Lineups.Any(l=>l.TeamId==10),Is.False);
    }

    [Test]
    public void AlternateActorContradictionDoesNotCascadeIntoMoreDefiniteFailures()
    {
        var references=Bundle(false);
        var original=references.AlternatePlayByPlay[0];
        var rows=original.Rows.ToList();
        rows.InsertRange(1,new[]{
            JsonSerializer.SerializeToElement(new {period=1,clock="PT11M00.00S",teamId=10,personId=99,actionType="2pt",actionNumber=10}),
            JsonSerializer.SerializeToElement(new {period=1,clock="PT10M00.00S",teamId=10,personId=99,actionType="2pt",actionNumber=11})});
        references.AlternatePlayByPlay[0]=original with {Rows=rows.ToArray()};
        var result=AlternateLineupReplay.Build(references,true,s=>long.Parse(s),s=>long.Parse(s));
        var actors=result.Checks.Where(c=>c.Code=="REFERENCE.ALTERNATE_ACTOR").ToArray();
        Assert.That(actors.Select(c=>c.Status),Is.EqualTo(new[]{CheckStatus.Fail,CheckStatus.NotAssessable}));
        Assert.That(result.Checks.Where(c=>c.Code=="REFERENCE.ALTERNATE_MINUTES" && c.EntityId is >=10 and <20)
            .All(c=>c.Status==CheckStatus.NotAssessable),Is.True);
    }
}

