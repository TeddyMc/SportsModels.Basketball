namespace SportsModels.Data.Basketball.Contracts;

/// <summary>One charged participant; player and coach identities are mutually exclusive. Team-only charges are permitted.</summary>
public sealed record FoulParticipant(
    long? TeamId,
    long? PlayerId,
    long? CoachId);
