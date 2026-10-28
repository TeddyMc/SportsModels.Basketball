using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SportsModels.Data.Basketball.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Infrastructure;

internal sealed class ConversionWorkspace : IConversionWorkspace
{
    internal static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public IEnumerable<string> EnumerateInputs(string directory) => Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal);
    public Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken) => File.ReadAllBytesAsync(path, cancellationToken);
    public bool Exists(string path) => File.Exists(path);
    public ReferenceBundle ReadReferences(string directory, string gameId)
    {
        var folder = Path.Combine(directory, gameId);
        var inputs = new List<(string Path, byte[] Bytes)>();
        var errors = new List<string>();
        if (Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.json").Where(p => !p.EndsWith(".retrieval.json", StringComparison.Ordinal)))
            {
                try { inputs.Add((path, File.ReadAllBytes(path))); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { errors.Add($"{path}: {exception.Message}"); }
            }
        }
        var bundle = ReferenceBundle.Parse(inputs, gameId);
        bundle.Checks.AddRange(errors.Select(e => new CheckResult("REFERENCE.READ", CheckStatus.NotAssessable, e)));
        return bundle;
    }

    public string CreateRun(string output, string runId)
    {
        var directory = Path.Combine(Path.GetFullPath(output), runId);
        if (Directory.Exists(directory)) { throw new IOException("Run already exists; immutable outputs cannot be overwritten."); }
        Directory.CreateDirectory(directory);
        return directory;
    }

    public async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    public Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        WriteBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), cancellationToken);

    public Task WriteTextAsync(string path, string text, CancellationToken cancellationToken) =>
        WriteBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(text), cancellationToken);

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(BasketballPlayByPlayEvent)) { return; }
            info.PolymorphismOptions = new JsonPolymorphismOptions { TypeDiscriminatorPropertyName = "$event" };
            foreach (var type in typeof(BasketballPlayByPlayEvent).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(BasketballPlayByPlayEvent)) && t.IsSealed).OrderBy(t => t.Name))
            { info.PolymorphismOptions.DerivedTypes.Add(new JsonDerivedType(type, type.Name)); }
        });
        var options = new JsonSerializerOptions { WriteIndented = false, TypeInfoResolver = resolver };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
