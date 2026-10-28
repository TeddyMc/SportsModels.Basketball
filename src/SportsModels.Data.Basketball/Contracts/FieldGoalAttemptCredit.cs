namespace SportsModels.Data.Basketball.Contracts;

/// <summary>Statistical attempt attribution, separate from the identity of the physical shooter.</summary>
public enum FieldGoalAttemptCredit
{
    NotSet = 0,
    /// <summary>Attributed to a player as well as the team; requires a resolved ShooterPlayerId.</summary>
    Player = 1,
    /// <summary>Attributed to the team only; ShooterPlayerId may still identify who physically shot it.</summary>
    Team = 2
}
