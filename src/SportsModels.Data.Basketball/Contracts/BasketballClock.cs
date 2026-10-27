namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A period-relative clock; no fixed regulation period length is assumed.</summary>
/// <param name="Period">The regulation quarter or overtime classification.</param>
/// <param name="OvertimePeriodNumber">Null in regulation; 1-based in overtime.</param>
/// <param name="ClockRemaining">Remaining period time, including fractional seconds.</param>
public sealed record BasketballClock(
    BasketballPeriod Period,
    int? OvertimePeriodNumber,
    TimeSpan ClockRemaining);

