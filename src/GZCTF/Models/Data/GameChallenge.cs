using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using GZCTF.Models.Request.Edit;

namespace GZCTF.Models.Data;

public class GameChallenge : Challenge
{
    /// <summary>
    /// Whether to record traffic
    /// </summary>
    public bool EnableTrafficCapture { get; set; }

    /// <summary>
    /// Whether to disable blood bonus
    /// </summary>
    public bool DisableBloodBonus { get; set; }

    /// <summary>
    /// Initial score
    /// </summary>
    [Required]
    public int OriginalScore { get; set; } = 1000;

    /// <summary>
    /// Minimum score rate
    /// </summary>
    [Required]
    [Range(0, 1)]
    public double MinScoreRate { get; set; } = 0.25;

    /// <summary>
    /// Difficulty coefficient
    /// </summary>
    [Required]
    public double Difficulty { get; set; } = 5;

    /// <summary>
    /// Expected completion time (UTC)
    /// </summary>
    [Required]
    public DateTimeOffset ExpectedSolveTimeUtc { get; set; } = GetDefaultExpectedSolveTime();

    static readonly TimeZoneInfo ChinaStandardTime = GetChinaTimeZone();

    /// <summary>
    /// Current score of the challenge
    /// </summary>
    [NotMapped]
    public int CurrentScore => CalculateScore(OriginalScore, MinScoreRate, Difficulty, AcceptedCount + 1,
        DateTimeOffset.UtcNow, ExpectedSolveTimeUtc);

    internal static int CalculateScore(int originalScore, double minScoreRate, double difficulty, int solveNumber,
        DateTimeOffset submissionTimeUtc, DateTimeOffset expectedSolveTimeUtc)
    {
        return submissionTimeUtc > expectedSolveTimeUtc
            ? CalculateLateSolveScore(originalScore, minScoreRate)
            : CalculateDynamicScore(originalScore, minScoreRate, difficulty, solveNumber);
    }

    internal static int CalculateLateSolveScore(int originalScore, double minScoreRate)
    {
        var sixtyPercent = (int)Math.Floor(originalScore * 0.6);
        var minScore = (int)Math.Floor(originalScore * minScoreRate);

        return Math.Clamp(Math.Min(sixtyPercent, minScore), 0, originalScore);
    }

    static int CalculateDynamicScore(int originalScore, double minScoreRate, double difficulty, int solveNumber)
    {
        if (solveNumber <= 1)
            return originalScore;

        var minScore = (int)Math.Floor(originalScore * minScoreRate);

        if (difficulty <= 0)
            return Math.Clamp(minScore, 0, originalScore);

        var rate = minScoreRate + (1.0 - minScoreRate) * Math.Exp((1 - solveNumber) / difficulty);
        var score = (int)Math.Floor(originalScore * rate);

        if (score < minScore)
            return minScore;

        return Math.Clamp(score, 0, originalScore);
    }

    internal static DateTimeOffset GetDefaultExpectedSolveTime(DateTimeOffset? creationTimeUtc = null)
    {
        var baseTime = creationTimeUtc ?? DateTimeOffset.UtcNow;
        if (ChinaStandardTime == TimeZoneInfo.Utc)
        {
            var utcTarget = baseTime.AddDays(7);
            var utcMidnight = new DateTime(utcTarget.UtcDateTime.Year, utcTarget.UtcDateTime.Month,
                utcTarget.UtcDateTime.Day, 23, 59, 0, DateTimeKind.Utc);
            return new DateTimeOffset(utcMidnight);
        }

        var localTime = TimeZoneInfo.ConvertTime(baseTime, ChinaStandardTime);
        var dueLocalDate = localTime.Date.AddDays(7);
        var dueLocalDateTime = dueLocalDate.AddHours(23).AddMinutes(59);
        var offset = ChinaStandardTime.GetUtcOffset(dueLocalDateTime);
        var localDue = new DateTimeOffset(dueLocalDateTime, offset);
        return localDue.ToUniversalTime();
    }

    static TimeZoneInfo GetChinaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }
        catch (TimeZoneNotFoundException)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
            }
            catch
            {
                return TimeZoneInfo.Utc;
            }
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    internal void Update(ChallengeUpdateModel model)
    {
        Title = model.Title ?? Title;
        Content = model.Content ?? Content;
        Category = model.Category ?? Category;
        Hints = model.Hints ?? Hints;
        IsEnabled = model.IsEnabled ?? IsEnabled;
        CPUCount = model.CPUCount ?? CPUCount;
        MemoryLimit = model.MemoryLimit ?? MemoryLimit;
        StorageLimit = model.StorageLimit ?? StorageLimit;
        ContainerImage = model.ContainerImage?.Trim() ?? ContainerImage;
        ContainerExposePort = model.ContainerExposePort ?? ContainerExposePort;
        OriginalScore = model.OriginalScore ?? OriginalScore;
        MinScoreRate = model.MinScoreRate ?? MinScoreRate;
        Difficulty = model.Difficulty ?? Difficulty;
        FileName = model.FileName ?? FileName;
        DisableBloodBonus = model.DisableBloodBonus ?? DisableBloodBonus;
        SubmissionLimit = model.SubmissionLimit ?? SubmissionLimit;
        ExpectedSolveTimeUtc = model.ExpectedSolveTimeUtc ?? ExpectedSolveTimeUtc;

        // only set FlagTemplate to null when pass an empty string (but not null)
        FlagTemplate = model.FlagTemplate is null ? FlagTemplate :
            string.IsNullOrWhiteSpace(model.FlagTemplate) ? null : model.FlagTemplate;

        // Container only
        EnableTrafficCapture = Type.IsContainer() && (model.EnableTrafficCapture ?? EnableTrafficCapture);
    }

    #region Db Relationship

    /// <summary>
    /// Submissions
    /// </summary>
    public List<Submission> Submissions { get; set; } = [];

    /// <summary>
    /// Challenge instances
    /// </summary>
    public List<GameInstance> Instances { get; set; } = [];

    /// <summary>
    /// Teams that activated the challenge
    /// </summary>
    public HashSet<Participation> Teams { get; set; } = [];

    /// <summary>
    /// Game ID
    /// </summary>
    public int GameId { get; set; }

    /// <summary>
    /// Game object
    /// </summary>
    public Game Game { get; set; } = null!;

    #endregion Db Relationship
}
