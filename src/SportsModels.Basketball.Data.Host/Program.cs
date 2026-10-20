using System.Globalization;
using Autofac;
using SportsModels.Basketball.Data.Autofac;
using SportsModels.Basketball.Data.Contracts;

if (args.Length != 3
    || !DateOnly.TryParseExact(args[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
    || !DateOnly.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
{
    Console.Error.WriteLine("Usage: SportsModels.Basketball.Data.Host <start:yyyy-MM-dd> <end:yyyy-MM-dd> <output-directory>");
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
        .RunAsync(new IngestionRequest(start, end, args[2]), cancellation.Token);

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
