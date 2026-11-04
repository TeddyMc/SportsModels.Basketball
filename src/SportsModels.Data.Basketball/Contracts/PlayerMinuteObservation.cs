namespace SportsModels.Data.Basketball.Contracts;

public enum MinuteScope { Period, Regulation, FullGame }
public enum MinuteAvailability { PlausibleUncorroborated, WithComparisonWarnings, Contradictory, Unestablished, ConfirmedUnused, Inactive, Corroborated }
public enum MinuteEvidenceKind { Unknown, SupportingFeed, InferredBreakChanges, ExplicitWithinPeriod }

public sealed record OpeningPlayerMembership(int Period, long TeamId, long PlayerId, bool? OnCourt,
    MinuteEvidenceKind Origin, string Evidence, long? FirstEventId = null);

/// <summary>Seconds is a complete reconstructed total only. EstablishedSeconds retains available intervals when the total is unknown.</summary>
public sealed record PlayerMinuteObservation(long TeamId, long PlayerId, MinuteScope Scope, int? Period,
    decimal? Seconds, decimal EstablishedSeconds, MinuteAvailability Availability, IReadOnlyList<string> Reasons)
{
    public IReadOnlyList<MinuteEvidenceKind> EvidenceKinds { get; init; } = [];
}
