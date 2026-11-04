namespace SportsModels.Data.Basketball.Contracts;

/// <summary>Player metadata referenced by internal player ID from events.</summary>
public sealed record BasketballGamePlayer(
    long PlayerId,
    string Name,
    IReadOnlyList<SourceEntityIdentifier> SourceIdentifiers)
{
    public PlayerEligibility Eligibility { get; init; } = PlayerEligibility.Unknown;
    public bool? Started { get; init; }
    public bool? Played { get; init; }
    public string? NotPlayingReason { get; init; }
    public string? NotPlayingDescription { get; init; }
    public IReadOnlyList<RosterFieldEvidence> StatusEvidence { get; init; } = [];
}
