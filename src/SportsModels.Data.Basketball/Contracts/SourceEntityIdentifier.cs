namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A provider identity retained separately from internally allocated entity IDs.</summary>
/// <param name="Source">The provider of the identity.</param>
/// <param name="SourceId">The original identifier, preserving leading zeroes.</param>
public sealed record SourceEntityIdentifier(
    BasketballDataSource Source,
    string SourceId);

