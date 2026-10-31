using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Infrastructure;

internal sealed class NbaReferenceSource(HttpClient client) : INbaReferenceSource
{
    public Task<byte[]> DownloadAsync(string endpoint, string gameId, int period, CancellationToken cancellationToken)
    {
        if (endpoint == "gamerotation")
        {
            return new NbaHttpSource(client).GetAsync($"https://stats.nba.com/stats/gamerotation?GameID={gameId}&LeagueID=00", cancellationToken);
        }
        if (endpoint == "liveplaybyplay")
        {
            return new NbaHttpSource(client).GetAsync($"https://cdn.nba.com/static/json/liveData/playbyplay/playbyplay_{gameId}.json", cancellationToken);
        }
        if (endpoint == "liveboxscore")
        {
            return new NbaHttpSource(client).GetAsync($"https://cdn.nba.com/static/json/liveData/boxscore/boxscore_{gameId}.json", cancellationToken);
        }
        if (endpoint.StartsWith("commonteamroster-", StringComparison.Ordinal))
        {
            var teamId = endpoint["commonteamroster-".Length..];
            if (teamId.Length > 20 || !long.TryParse(teamId, out var teamKey) || teamKey <= 0) { throw new ArgumentException("Invalid team ID."); }
            var year = 2000 + int.Parse(gameId.Substring(3, 2), System.Globalization.CultureInfo.InvariantCulture);
            return new NbaHttpSource(client).GetAsync($"https://stats.nba.com/stats/commonteamroster?TeamID={teamId}&Season={year}-{(year + 1) % 100:D2}&LeagueID=00", cancellationToken);
        }
        if (endpoint is not ("boxscoretraditionalv3" or "boxscoretraditionalv3-range" or "boxscoretraditionalv3-opening" or "boxscoresummaryv2"))
        {
            throw new ArgumentException("Unsupported reference endpoint.", nameof(endpoint));
        }
        var query = endpoint == "boxscoretraditionalv3"
            ? $"&StartPeriod={period}&EndPeriod={period}&StartRange=0&EndRange=0&RangeType=0"
            : "";
        if (endpoint is "boxscoretraditionalv3-range" or "boxscoretraditionalv3-opening")
        {
            var start = period <= 4 ? (period - 1) * 7200 : 28800 + (period - 5) * 3000;
            var end = endpoint == "boxscoretraditionalv3-opening" ? start + 10 : start + (period <= 4 ? 7200 : 3000);
            query = $"&StartPeriod={period}&EndPeriod={period}&StartRange={start}&EndRange={end}&RangeType=2";
        }
        var resource = endpoint.Split('-')[0];
        return new NbaHttpSource(client).GetAsync($"https://stats.nba.com/stats/{resource}?GameID={gameId}{query}", cancellationToken);
    }
}
