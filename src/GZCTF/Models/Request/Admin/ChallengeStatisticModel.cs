using GZCTF.Models.Data;

namespace GZCTF.Models.Request.Admin;

public class StatisticMetricModel
{
    public double? Average { get; init; }
    public double? Median { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
}

public class ChallengeStatisticModel
{
    public int ChallengeId { get; init; }
    public string Title { get; init; } = string.Empty;
    public ChallengeCategory Category { get; init; }
    public ChallengeType Type { get; init; }
    public int TotalTeamCount { get; init; }
    public int ActivatedTeamCount { get; init; }
    public int SolvedTeamCount { get; init; }
    public int TotalSubmissionCount { get; init; }
    public double CompletionRate { get; init; }
    public StatisticMetricModel AttemptsToSolve { get; init; } = new();
    public StatisticMetricModel SolveTimeMinutes { get; init; } = new();
}
