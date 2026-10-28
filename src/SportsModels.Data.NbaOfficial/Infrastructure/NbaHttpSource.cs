using System.Globalization;
using System.Text.Json;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Infrastructure;

internal sealed class NbaHttpSource(HttpClient client) : INbaSource
{
    public async Task<IReadOnlyList<NbaGame>> DiscoverAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var url = "https://stats.nba.com/stats/scoreboardv3?LeagueID=00&GameDate="
            + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var bytes = await GetAsync(url, cancellationToken);

        using var document = JsonDocument.Parse(bytes);

        try
        {
            var games = new Dictionary<string, NbaGame>(StringComparer.Ordinal);

            foreach (var row in document.RootElement.GetProperty("scoreboard").GetProperty("games").EnumerateArray())
            {
                if (row.GetProperty("gameStatus").GetInt32() != 3)
                {
                    continue;
                }

                var gameId = row.GetProperty("gameId").GetString();
                var home = row.GetProperty("homeTeam").GetProperty("teamTricode").GetString();
                var away = row.GetProperty("awayTeam").GetProperty("teamTricode").GetString();

                if (gameId is null || home is null || away is null)
                {
                    throw new InvalidDataException("Final game has missing identity or team metadata.");
                }

                var game = new NbaGame(gameId, date, away, home);
                _ = game.FileName;

                if (games.TryGetValue(gameId, out var previous) && previous != game)
                {
                    throw new InvalidDataException("Conflicting metadata for a repeated game ID.");
                }

                games[gameId] = game;
            }

            return games.Values.ToArray();
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException
            or IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
        {
            throw new InvalidDataException("Unexpected NBA scoreboard response schema.", exception);
        }
    }

    public Task<byte[]> DownloadAsync(string gameId, CancellationToken cancellationToken) =>
        GetAsync($"https://stats.nba.com/stats/playbyplayv3?GameID={gameId}&StartPeriod=0&EndPeriod=0", cancellationToken);

    internal async Task<byte[]> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://www.nba.com/");

        // Match the browser request reproduced successfully with curl on Windows.
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.0.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptLanguage.ParseAdd("en-IE,en-GB;q=0.9,en-US;q=0.8,en;q=0.7");
        request.Headers.Connection.Add("keep-alive");
        request.Headers.Add("Origin", "https://www.nba.com");
        request.Headers.Add("Sec-Fetch-Dest", "empty");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Site", "same-site");
        request.Headers.Add("sec-ch-ua", "\"Google Chrome\";v=\"155\", \"Chromium\";v=\"155\", \"Not(A:Brand\";v=\"24\"");
        request.Headers.Add("sec-ch-ua-mobile", "?0");
        request.Headers.Add("sec-ch-ua-platform", "\"Windows\"");

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
