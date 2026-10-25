using System.Globalization;
using Autofac;
using SportsModels.Data.NbaOfficial.Autofac;
using SportsModels.Data.NbaOfficial.Contracts;

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
