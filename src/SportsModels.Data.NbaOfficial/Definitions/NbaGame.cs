using System.Globalization;

namespace SportsModels.Data.NbaOfficial.Definitions;

internal sealed record NbaGame(string GameId, DateOnly Date, string AwayTeam, string HomeTeam)
{
    public int? PeriodCount { get; init; }
    public string? HomeTeamSourceId { get; init; }
    public string? AwayTeamSourceId { get; init; }
    public string FileName
    {
        get
        {
            if (GameId.Length != 10 || !GameId.All(character => character is >= '0' and <= '9')
                || !IsTeamCode(AwayTeam) || !IsTeamCode(HomeTeam) || AwayTeam == HomeTeam)
            {
                throw new InvalidDataException("Expected a ten-digit game ID and distinct uppercase alphanumeric team codes (2–6 characters).");
            }

            return $"{AwayTeam}@{HomeTeam}_{Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}_{GameId}.json";
        }
    }

    private static bool IsTeamCode(string code) => code.Length is >= 2 and <= 6
        && code.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9');
}
