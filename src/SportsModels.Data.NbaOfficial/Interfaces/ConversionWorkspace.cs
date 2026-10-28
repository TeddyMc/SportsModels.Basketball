using SportsModels.Data.NbaOfficial.Definitions;

namespace SportsModels.Data.NbaOfficial.Interfaces;

internal interface IConversionWorkspace
{
    IEnumerable<string> EnumerateInputs(string directory);
    Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken);
    ReferenceBundle ReadReferences(string directory, string gameId);
    string CreateRun(string output, string runId);
    Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken);
    Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken);
    Task WriteTextAsync(string path, string text, CancellationToken cancellationToken);
}
