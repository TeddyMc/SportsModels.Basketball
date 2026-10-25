using System.Text.Json;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Infrastructure;

internal sealed class RawJsonStore : IRawJsonStore
{
    public async Task<bool> ExistsAsync(string directory, NbaGame game, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, game.FileName);

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

    public async Task SaveAsync(string directory, NbaGame game, byte[] bytes, CancellationToken cancellationToken)
    {
        var destination = Path.Combine(directory, game.FileName);
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{game.GameId}.{Guid.NewGuid():N}.tmp");

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
}
