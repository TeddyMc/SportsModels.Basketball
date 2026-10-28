using System.Globalization;
using Autofac;
using SportsModels.Data.NbaOfficial.Autofac;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.Basketball.Infrastructure;

if (args.Length > 0 && args[0] is "convert" or "references" or "backup-registry")
{
    return await RunDataCommand(args);
}

const string DefaultOutputDirectory = @"C:\Data\Basketball\NbaOfficial\stats\playbyplayv3";

if (args.Length is < 2 or > 3
    || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
    || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
{
    Console.Error.WriteLine($"Usage: SportsModels.Data.NbaOfficial.Host <start:yyyy-MM-dd> <end:yyyy-MM-dd> [output-directory; default {DefaultOutputDirectory}]");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    var builder = new ContainerBuilder();
    builder.RegisterModule<PlayByPlayModule>();

    using var container = builder.Build();

    var result = await container.Resolve<IPlayByPlayIngestion>()
        .RunAsync(new IngestionRequest(start, end, args.Length == 3 ? args[2] : DefaultOutputDirectory), cancellation.Token);

    foreach (var failure in result.Failures)
    {
        Console.Error.WriteLine($"FAILED {failure.Resource} [{failure.Kind}]: {failure.Message}");
    }

    Console.WriteLine($"Discovered={result.Discovered} Downloaded={result.Downloaded} Skipped={result.Skipped} Failures={result.Failed}");

    return result.Failed == 0 ? 0 : 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled. Previously completed files are retained.");
    return 130;
}
catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

static async Task<int> RunDataCommand(string[] arguments)
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    try
    {
        if (arguments[0] == "backup-registry" && arguments.Length == 3)
        {
            if (!File.Exists(arguments[1])) { throw new FileNotFoundException("Registry does not exist."); }
            using var registry = new SqliteIdentityRegistry(arguments[1]);
            registry.Backup(arguments[2]);
            Console.WriteLine($"Backed up registry {registry.RegistryId}, revision {registry.Revision}.");
            return 0;
        }
        var builder = new ContainerBuilder();
        if (arguments[0] == "references" && arguments.Length is >= 3 and <= 7)
        {
            builder.RegisterModule<PlayByPlayModule>();
            using var container = builder.Build();
            var result = await container.Resolve<INbaReferenceAcquisition>().RunAsync(new(arguments[1], arguments[2],
                GameIds: arguments.Length >= 4 && arguments[3] != "all" ? arguments[3].Split(',') : null)
            {
                Endpoints = arguments.Length >= 5 && arguments[4] != "all" ? arguments[4].Split(',') : null,
                StartDate = arguments.Length >= 6 ? DateOnly.ParseExact(arguments[5], "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                EndDate = arguments.Length >= 7 ? DateOnly.ParseExact(arguments[6], "yyyy-MM-dd", CultureInfo.InvariantCulture) : null
            }, cancellation.Token);
            foreach (var failure in result.Failures) { Console.Error.WriteLine($"FAILED {failure.Resource} [{failure.Kind}]: {failure.Message}"); }
            Console.WriteLine($"Games={result.Discovered} Snapshots={result.Downloaded} Existing={result.Skipped} Failures={result.Failed}");
            return result.Failed == 0 ? 0 : 1;
        }
        if (arguments[0] == "convert" && arguments.Length is 5 or 6)
        {
            builder.RegisterModule(new ConversionModule(arguments[4]));
            using var container = builder.Build();
            var result = await container.Resolve<INbaConversion>().RunAsync(new(arguments[1], arguments[2], arguments[3],
                GameIds: arguments.Length == 6 ? arguments[5].Split(',') : null), cancellation.Token);
            Console.WriteLine($"Run={result.RunId} Attempted={result.Attempted} Converted={result.Converted} Failed={result.Failed}");
            Console.WriteLine(result.RunDirectory);
            return result.Failed == 0 ? 0 : 1;
        }
        Console.Error.WriteLine("Usage: references <pbp-directory> <reference-directory> [game-ids|all] [endpoints|all] [start:yyyy-MM-dd] [end:yyyy-MM-dd]\n"
            + "       convert <pbp-directory> <reference-directory> <output-directory> <registry.sqlite> [comma-separated-game-ids]\n"
            + "       backup-registry <registry.sqlite> <new-backup.sqlite>");
        return 2;
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled; incomplete runs have no completion manifest."); return 130; }
    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
    { Console.Error.WriteLine(exception.Message); return 2; }
}
