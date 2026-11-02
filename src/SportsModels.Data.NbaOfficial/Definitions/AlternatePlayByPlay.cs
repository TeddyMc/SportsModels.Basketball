using System.Text.Json;

namespace SportsModels.Data.NbaOfficial.Definitions;

internal sealed record AlternatePlayByPlay(string SnapshotId, JsonElement[] Rows);
