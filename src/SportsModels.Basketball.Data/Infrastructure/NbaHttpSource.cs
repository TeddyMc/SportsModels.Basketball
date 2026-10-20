using System.Globalization;
using System.Text.Json;
using SportsModels.Basketball.Data.Interfaces;

namespace SportsModels.Basketball.Data.Infrastructure;

internal sealed class NbaHttpSource(HttpClient client) : INbaSource
{
    public async Task<IReadOnlyList<string>> DiscoverAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var url = "https://stats.nba.com/stats/scoreboardv2?LeagueID=00&DayOffset=0&GameDate="
            + Uri.EscapeDataString(date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture));
        var bytes = await GetAsync(url, cancellationToken);

        using var document = JsonDocument.Parse(bytes);

        try
        {
            var header = document.RootElement.GetProperty("resultSets").EnumerateArray()
                .Single(set => set.GetProperty("name").GetString() == "GameHeader");
            var columns = header.GetProperty("headers").EnumerateArray().Select(value => value.GetString()).ToList();
            var idColumn = columns.IndexOf("GAME_ID");
            var statusColumn = columns.IndexOf("GAME_STATUS_ID");

            if (idColumn < 0 || statusColumn < 0)
            {
                throw new InvalidDataException("GameHeader is missing GAME_ID or GAME_STATUS_ID.");
            }

            var ids = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var row in header.GetProperty("rowSet").EnumerateArray())
            {
                if (row[statusColumn].GetInt32() != 3)
                {
                    continue;
                }

                var gameId = row[idColumn].GetString();

                if (gameId is null || gameId.Length != 10 || !gameId.All(character => character is >= '0' and <= '9'))
                {
                    throw new InvalidDataException("Final game has an invalid stable game ID.");
                }

                ids.Add(gameId);
            }

            return ids.ToArray();
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException
            or IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
        {
            throw new InvalidDataException("Unexpected NBA scoreboard response schema.", exception);
        }
    }

    public Task<byte[]> DownloadAsync(string gameId, CancellationToken cancellationToken) =>
        GetAsync($"https://cdn.nba.com/static/json/liveData/playbyplay/playbyplay_{gameId}.json", cancellationToken);

    private async Task<byte[]> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 SportsModels.Basketball.Data/1.0");
        request.Headers.Referrer = new Uri("https://www.nba.com/");
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
