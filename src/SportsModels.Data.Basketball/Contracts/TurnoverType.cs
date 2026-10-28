namespace SportsModels.Data.Basketball.Contracts;

public enum TurnoverType
{
    NotSet = 0,
    BadPass = 1,
    LostBall = 2,
    Traveling = 3,
    DoubleDribble = 4,
    Carrying = 5,
    OffensiveFoul = 6,
    Backcourt = 7,
    ShotClock = 8,
    ThreeSeconds = 9,
    FiveSeconds = 10,
    EightSeconds = 11,
    TenSeconds = 12,
    DiscontinueDribble = 13,
    OffensiveGoaltending = 14,
    OutOfBoundsBadPass = 15,
    OutOfBoundsLostBall = 16,
    StepOutOfBounds = 17,
    Inbound = 18,
    IllegalAssist = 19,
    JumpBallViolation = 20,
    KickedBallViolation = 21,
    LaneViolation = 22,
    ExcessTimeout = 23,
    TooManyPlayers = 24,
    BasketFromBelow = 25,
    PunchedBall = 26,
    Other = 27
}
