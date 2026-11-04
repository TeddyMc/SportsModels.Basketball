using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Infrastructure;

internal sealed class AcquisitionDelay : IAcquisitionDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}
