using System.Text.Json;

namespace SportsModels.Data.Basketball.Contracts;

public enum CheckStatus { Pass, Fail, NotAssessable, NotApplicable }
public enum FindingSeverity { Warning, Error }

public sealed record CheckResult(string Code, CheckStatus Status, string Evidence,
    long? EventId = null, int? Period = null, long? EntityId = null, string? Stat = null,
    double? Expected = null, double? Observed = null, FindingSeverity? Severity = null,
    IReadOnlyList<int>? SourceRows = null);

public sealed record StatisticLine(long TeamId, long? PlayerId, int Period,
    IReadOnlyDictionary<string, double> Values, IReadOnlyList<string> Unknown)
{
    public IReadOnlyDictionary<string, string> ComparisonKeys { get; init; } = new Dictionary<string, string>();
}

public sealed record LineupEvidence(int Period, long TeamId, IReadOnlyList<long> Players, string Evidence);
public sealed record PlayerStint(int Period, long TeamId, long PlayerId, double StartRemaining, double EndRemaining);
public sealed record ScoreObservation(int SequenceNumber, int? Home, int? Away, int SourceRow);
public sealed record FoulCountObservation(int SequenceNumber, long PlayerId, int PersonalFouls, int SourceRow);
public sealed record TeamFoulObservation(int SequenceNumber, long TeamId, int? TeamFouls, bool Penalty, int SourceRow);
public sealed record CoverageGap(int SequenceNumber, IReadOnlyList<string> Statistics, bool Lineup, bool Possession, string Reason);

/// <summary>Reference observations are supplied separately; provider interpretation belongs upstream.</summary>
public sealed record GameValidationContext(bool OrdinaryNbaRules, bool Final,
    IReadOnlyList<LineupEvidence> OpeningLineups,
    IReadOnlyList<StatisticLine> ReferenceStatistics,
    IReadOnlyList<ScoreObservation> Scores,
    IReadOnlyList<int> UnresolvedSequences,
    bool HasUnresolvedCredits,
    string ReferenceLimitation)
{
    public IReadOnlyList<CoverageGap> Gaps { get; init; } = [];
    public IReadOnlyList<FoulCountObservation> FoulCounts { get; init; } = [];
    public IReadOnlyList<TeamFoulObservation> TeamFouls { get; init; } = [];
    public double? MinutesToleranceSeconds { get; init; }
    public string MinutesPrecisionEvidence { get; init; } = "Unverified reference rounding and source clock precision.";
}

public sealed record ValidationReport(IReadOnlyList<CheckResult> Checks,
    IReadOnlyList<StatisticLine> DerivedStatistics, IReadOnlyList<PlayerStint> Stints);

public interface IBasketballValidator
{
    ValidationReport Validate(BasketballGamePlayByPlay game, GameValidationContext context);
}

public sealed record InputSnapshot(string SnapshotId, string OriginalPath, string RetainedPath,
    string Sha256, long ByteLength, string Endpoint, DateTime? RetrievedAtUtc, bool BytesRetained);

public sealed record SourceRowDisposition(int RowIndex, JsonElement Original,
    string Disposition, IReadOnlyList<long> EventIds, string Explanation);
