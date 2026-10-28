namespace SportsModels.Data.Basketball.Contracts;

/// <summary>Attack-relative feet from the basket projected onto the court.</summary>
/// <param name="XFeet">Positive to the right when standing at the attacking basket facing centre court.</param>
/// <param name="YFeet">Positive toward centre court from the attacking basket.</param>
public sealed record BasketballShotLocation(
    double XFeet,
    double YFeet);
