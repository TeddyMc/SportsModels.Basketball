using System.Net;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Application;

internal sealed class AcquisitionHaltedException(string message) : Exception(message);

internal sealed class NbaRequestRecovery(DataAcquisitionRequest options, IAcquisitionDelay delay,
    Func<string, string, string, Task> progress)
{
    private int consecutiveFailures;
    private int cooldowns;

    public async Task<T> ExecuteAsync<T>(string resource, Func<Task<T>> action, CancellationToken token)
    {
        for (var attempt = 1; ; attempt++)
        {
            await delay.WaitAsync(options.RequestDelay, token);

            try
            {
                var result = await action();
                consecutiveFailures = 0;
                return result;
            }
            catch (Exception error) when (Transient(error, token))
            {
                consecutiveFailures++;
                await progress("RequestFailed", resource, $"Attempt {attempt}/{options.AttemptsPerResource}: {error.Message}");

                if (consecutiveFailures >= options.ConsecutiveFailureLimit)
                {
                    if (cooldowns >= options.MaximumCooldowns)
                        throw new AcquisitionHaltedException("Cooldown limit exhausted; retained data can be resumed with the same command.");

                    cooldowns++;
                    await progress("Cooldown", resource, $"Cooldown {cooldowns}/{options.MaximumCooldowns}: {options.Cooldown}.");
                    await delay.WaitAsync(options.Cooldown, token);
                    consecutiveFailures = 0;
                }
                else if (attempt < options.AttemptsPerResource)
                {
                    await delay.WaitAsync(options.RetryDelay, token);
                }

                if (attempt >= options.AttemptsPerResource) { throw; }
            }
        }
    }

    private static bool Transient(Exception error, CancellationToken token) => error switch
    {
        OperationCanceledException => !token.IsCancellationRequested,
        HttpRequestException http => http.StatusCode is null or HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500,
        _ => false
    };
}
