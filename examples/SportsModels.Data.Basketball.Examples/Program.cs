using SportsModels.Data.Basketball.Contracts;

// Synthetic identities are independently allocated, never cast from provider IDs.
var shooter = new BasketballGamePlayer(101, "Home Shooter",
    [new(BasketballDataSource.NbaOfficial, "900001")]);

var outgoing = new BasketballGamePlayer(201, "Away Outgoing",
    [new(BasketballDataSource.NbaOfficial, "900002")]);

var incoming = new BasketballGamePlayer(202, "Away Incoming",
    [new(BasketballDataSource.NbaOfficial, "900003")]);

var home = new BasketballGameTeam(11, "Home",
    [new(BasketballDataSource.NbaOfficial, "800001")], [shooter]);

var away = new BasketballGameTeam(12, "Away",
    [new(BasketballDataSource.NbaOfficial, "800002")], [outgoing, incoming]);

var clock = new BasketballClock(BasketballPeriod.FourthQuarter, null, TimeSpan.FromSeconds(42.5));

// A partial stream: foul 900 is outside this core slice, whose foul schema is not approved.
// This demonstrates the typed reference without inventing a production foul record.
const long parentFoulEventId = 900;

BasketballPlayByPlayEvent[] events =
[
    new FreeThrowEvent(901, 1, clock, null, home.TeamId, shooter.PlayerId,
        ShotOutcome.Made, 1, 2, parentFoulEventId),
    new SubstitutionEvent(902, 2, clock, null, away.TeamId, outgoing.PlayerId, incoming.PlayerId),
    new FreeThrowEvent(903, 3, clock, null, home.TeamId, shooter.PlayerId,
        ShotOutcome.Made, 2, 2, parentFoulEventId),
    // Synthetic known time; it is supplied explicitly, not inferred from neighbouring events.
    new SubstitutionEvent(904, 4,
        new(BasketballPeriod.Overtime, 2, TimeSpan.FromSeconds(0.7)),
        new DateTime(2026, 1, 2, 2, 15, 0, DateTimeKind.Utc),
        away.TeamId, incoming.PlayerId, outgoing.PlayerId)
];

var game = new BasketballGamePlayByPlay(1,
    [new(BasketballDataSource.NbaOfficial, "0022500001")], home, away, events);

Console.WriteLine($"Game {game.GameId} (source {game.SourceIdentifiers[0].SourceId}): {game.HomeTeam.Name} vs {game.AwayTeam.Name}");

foreach (var play in game.Events)
{
    var detail = play switch
    {
        FreeThrowEvent attempt => $"Free throw {attempt.AttemptNumber}/{attempt.TotalAttempts}, parent foul {attempt.ParentFoulEventId}",
        SubstitutionEvent substitution => $"Substitution {substitution.PlayerOutId} -> {substitution.PlayerInId}",
        _ => throw new InvalidOperationException("Unexpected example event type.")
    };

    var occurredAt = play.OccurredAtUtc?.ToString("O") ?? "unknown";

    Console.WriteLine($"{play.SequenceNumber}: {play.Clock.Period} OT={play.Clock.OvertimePeriodNumber} remaining={play.Clock.ClockRemaining:c}; {detail}; UTC={occurredAt}");
}

var relatedAttempts = game.Events.OfType<FreeThrowEvent>()
    .Where(attempt => attempt.ParentFoulEventId == parentFoulEventId);

Console.WriteLine($"Attempts referencing foul {parentFoulEventId}: {relatedAttempts.Count()}");
