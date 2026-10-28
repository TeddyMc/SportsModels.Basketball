using SportsModels.Data.NbaOfficial.Contracts;

namespace SportsModels.Data.NbaOfficial.Definitions;

internal sealed record LocalReferenceGame(string GameId, int Periods, string[] TeamIds);
internal sealed record LocalGameInventory(bool DirectoryExists, IReadOnlyList<LocalReferenceGame> Games, IReadOnlyList<IngestionFailure> Failures);
