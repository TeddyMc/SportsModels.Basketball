using System.Text.Json;
using NbaModel.PlayByPlay.Interfaces;

namespace NbaModel.PlayByPlay.Infrastructure;

internal sealed class RawJsonStore : IRawJsonStore
{
    public async Task<bool> ExistsAsync(string directory, string gameId, CancellationToken cancellationToken)
    {
        var path = GetPath(directory, gameId);

        try
        {
            await using var stream = File.OpenRead(path);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public async Task SaveAsync(string directory, string gameId, byte[] bytes, CancellationToken cancellationToken)
    {
        var destination = GetPath(directory, gameId);
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{gameId}.{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private static string GetPath(string directory, string gameId)
    {
        if (gameId.Length != 10 || !gameId.All(character => character is >= '0' and <= '9'))
        {
            throw new InvalidDataException("Expected a ten-digit NBA game ID.");
        }

        return Path.Combine(directory, gameId + ".json");
    }
}
