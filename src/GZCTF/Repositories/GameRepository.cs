using System.Diagnostics;
using GZCTF.Models.Data;
using GZCTF.Models.Request.Admin;
using GZCTF.Models.Request.Game;
using GZCTF.Repositories.Interface;
using GZCTF.Services.Cache;
using GZCTF.Services.Config;
using GZCTF.Utils;
using Microsoft.EntityFrameworkCore;

namespace GZCTF.Repositories;

public class GameRepository(
    ILogger<GameRepository> logger,
    CacheHelper cacheHelper,
    IGameChallengeRepository challengeRepository,
    IParticipationRepository participationRepository,
    IConfigService configService,
    AppDbContext context) : RepositoryBase(context), IGameRepository
{
    readonly byte[] _xorKey = configService.GetXorKey();

    private sealed class ChallengeScoreState
    {
        public ChallengeInfo Info { get; init; } = null!;
        public int OriginalScore { get; init; }
        public double MinScoreRate { get; init; }
        public double Difficulty { get; init; }
        public int AwardedSolves { get; set; }
        public DateTimeOffset ExpectedSolveTimeUtc { get; init; }
        public int LateSolveScore { get; init; }

        public (int score, bool isLate) AwardScore(DateTimeOffset submitTimeUtc)
        {
            var solveNumber = AwardedSolves + 1;
            var score = GameChallenge.CalculateScore(OriginalScore, MinScoreRate, Difficulty, solveNumber,
                submitTimeUtc, ExpectedSolveTimeUtc);
            var isLate = submitTimeUtc > ExpectedSolveTimeUtc;
            AwardedSolves++;
            return (score, isLate);
        }
    }

    private sealed record ChallengeProjection(int Id, string Title, ChallengeCategory Category, ChallengeType Type);

    public override Task<int> CountAsync(CancellationToken token = default) => Context.Games.CountAsync(token);

    public Task<bool> HasGameAsync(int id, CancellationToken token = default)
        => Context.Games.AnyAsync(g => g.Id == id, token);

    public async Task<Game?> CreateGame(Game game, CancellationToken token = default)
    {
        game.GenerateKeyPair(_xorKey);

        if (_xorKey.Length == 0)
            logger.SystemLog(StaticLocalizer[nameof(Resources.Program.GameRepository_XorKeyNotConfigured)],
                TaskStatus.Pending,
                LogLevel.Warning);

        await Context.AddAsync(game, token);
        await SaveAsync(token);

        await cacheHelper.FlushGameListCache(token);
        await cacheHelper.FlushRecentGamesCache(token);

        return game;
    }

    public async Task UpdateGame(Game game, CancellationToken token = default)
    {
        await SaveAsync(token);

        await cacheHelper.FlushGameListCache(token);
        await cacheHelper.FlushRecentGamesCache(token);
        await cacheHelper.FlushScoreboardCache(game.Id, token);
    }

    public string GetToken(Game game, Team team) => $"{team.Id}:{game.Sign($"GZCTF_TEAM_{team.Id}", _xorKey)}";

    public Task<Game?> GetGameById(int id, CancellationToken token = default) =>
        Context.Games.FirstOrDefaultAsync(x => x.Id == id, token);

    public Task<int[]> GetUpcomingGames(CancellationToken token = default) =>
        Context.Games.Where(g => g.StartTimeUtc > DateTime.UtcNow
                                 && g.StartTimeUtc - DateTime.UtcNow < TimeSpan.FromMinutes(15))
            .OrderBy(g => g.StartTimeUtc).Select(g => g.Id).ToArrayAsync(token);

    public Task<BasicGameInfoModel[]> FetchGameList(int count, int skip, CancellationToken token) =>
        Context.Games.Where(g => !g.Hidden)
            .OrderByDescending(g => g.StartTimeUtc).Skip(skip).Take(count)
            .Select(game => new BasicGameInfoModel
            {
                Id = game.Id,
                Title = game.Title,
                Summary = game.Summary,
                PosterHash = game.PosterHash,
                StartTimeUtc = game.StartTimeUtc,
                EndTimeUtc = game.EndTimeUtc,
                TeamMemberCountLimit = game.TeamMemberCountLimit
            }).ToArrayAsync(token);


    public async Task<ArrayResponse<BasicGameInfoModel>> GetGameInfo(int count = 20, int skip = 0,
        CancellationToken token = default)
    {
        var total = await Context.Games.CountAsync(game => !game.Hidden, token);
        if (skip >= total)
            return new([], total);

        if (skip + count > 100)
            return new(await FetchGameList(count, skip, token), total);

        var games = await cacheHelper.GetOrCreateAsync(logger, CacheKey.GameList,
            entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromDays(2);
                return FetchGameList(100, 0, token);
            }, token: token);

        return new(games.Skip(skip).Take(count).ToArray(), total);
    }

    public Task<DataWithModifiedTime<BasicGameInfoModel[]>> GetRecentGames(CancellationToken token = default)
        => cacheHelper.GetOrCreateAsync(logger, CacheKey.RecentGames,
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                var games = await GenRecentGames(token);
                return new DataWithModifiedTime<BasicGameInfoModel[]>(games, DateTimeOffset.UtcNow);
            }, token: token);

    public Task<BasicGameInfoModel[]> GenRecentGames(CancellationToken token = default) =>
        // sort by following rules:
        // 1. ongoing games > upcoming games > ended games
        // 2. ongoing games: by end time, ascending
        // 3. upcoming games: by start time, ascending
        // 4. ended games: by end time, descending
        Context.Games
            .Where(g => !g.Hidden)
            .OrderBy(g =>
                g.EndTimeUtc <= DateTimeOffset.UtcNow
                    ? DateTimeOffset.UtcNow - g.EndTimeUtc // ended games
                    : g.StartTimeUtc >= DateTimeOffset.UtcNow
                        ? g.StartTimeUtc - DateTimeOffset.UtcNow // upcoming games
                        : DateTimeOffset.UtcNow - g.StartTimeUtc < g.EndTimeUtc - DateTimeOffset.UtcNow
                            ? DateTimeOffset.UtcNow - g.StartTimeUtc
                            : g.EndTimeUtc - DateTimeOffset.UtcNow)
            .Take(50) // limit to 50 games
            .Select(game => new BasicGameInfoModel
            {
                Id = game.Id,
                Title = game.Title,
                Summary = game.Summary,
                PosterHash = game.PosterHash,
                StartTimeUtc = game.StartTimeUtc,
                EndTimeUtc = game.EndTimeUtc,
                TeamMemberCountLimit = game.TeamMemberCountLimit
            })
            .ToArrayAsync(token);

    public Task<ScoreboardModel> GetScoreboard(Game game, CancellationToken token = default)
        => cacheHelper.GetOrCreateAsync(logger, CacheKey.ScoreBoard(game.Id),
            entry =>
            {
                entry.SlidingExpiration = TimeSpan.FromDays(7);
                return GenScoreboard(game, token);
            }, token: token);

    public Task<ScoreboardModel?> TryGetScoreboard(int gameId, CancellationToken token = default)
        => cacheHelper.GetAsync<ScoreboardModel>(CacheKey.ScoreBoard(gameId), token);

    public Task<bool> IsGameClosed(int gameId, CancellationToken token = default)
        => Context.Games.AnyAsync(game =>
            game.Id == gameId && game.EndTimeUtc < DateTimeOffset.UtcNow && !game.PracticeMode, token);

    public async Task<ScoreboardModel> GetScoreboardWithMembers(Game game, CancellationToken token = default)
    {
        // In most cases, we can get the scoreboard from the cache
        var scoreboard = await GetScoreboard(game, token);

        // load team info & participants
        var ids = scoreboard.Items.Values.Select(i => i.Id).ToArray();
        var teams = await Context.Teams
            .IgnoreAutoIncludes().Include(t => t.Captain)
            .Where(t => ids.Contains(t.Id))
            .Include(t => t.Members).ToHashSetAsync(token);

        // load participants with team id and game id, select all UserInfos
        var participants = await Context.UserParticipations
            .Where(p => ids.Contains(p.TeamId) && p.GameId == game.Id)
            .Include(p => p.User)
            .Select(p => new { p.TeamId, p.User })
            .GroupBy(p => p.TeamId)
            .ToDictionaryAsync(g => g.Key,
                g => g.Select(p => p.User).ToHashSet(), token);

        // update scoreboard items
        foreach (var item in scoreboard.Items.Values)
        {
            if (teams.FirstOrDefault(t => t.Id == item.Id) is { } team)
                item.TeamInfo = team;

            if (participants.TryGetValue(item.Id, out var users))
                item.Participants = users;
        }

        return scoreboard;
    }

    public async Task<IReadOnlyList<ChallengeStatisticModel>> GetChallengeStatistics(Game game,
        CancellationToken token = default)
    {
        var participations = await Context.Participations.AsNoTracking()
            .Where(p => p.GameId == game.Id && p.Status == ParticipationStatus.Accepted)
            .Select(p => new { p.Id, p.TeamId })
            .ToArrayAsync(token);

        var totalTeamCount = participations.Length;
        var acceptedParticipationIds = participations.Select(p => p.Id).ToHashSet();
        var teamToParticipationId = participations.GroupBy(p => p.TeamId)
            .ToDictionary(g => g.Key, g => g.First().Id);

        var challenges = await Context.GameChallenges.AsNoTracking()
            .Where(c => c.GameId == game.Id)
            .OrderBy(c => c.Category)
            .ThenBy(c => c.Title)
            .Select(c => new ChallengeProjection(c.Id, c.Title, c.Category, c.Type))
            .ToArrayAsync(token);

        var dynamicChallengeIds = challenges
            .Where(c => c.Type == ChallengeType.DynamicContainer)
            .Select(c => c.Id)
            .ToHashSet();

        var submissions = await Context.Submissions.AsNoTracking()
            .Where(s => s.GameId == game.Id && s.Status != AnswerResult.FlagSubmitted &&
                        s.ParticipationId != 0)
            .Select(s => new
            {
                s.ParticipationId,
                s.ChallengeId,
                s.Status,
                s.SubmitTimeUtc
            })
            .OrderBy(s => s.ChallengeId)
            .ThenBy(s => s.ParticipationId)
            .ThenBy(s => s.SubmitTimeUtc)
            .ToArrayAsync(token);

        var containerEvents = await Context.GameEvents.AsNoTracking()
            .Where(e => e.GameId == game.Id && e.Type == EventType.ContainerStart)
            .ToArrayAsync(token);

        var containerStarts = await Context.Containers.AsNoTracking()
            .Where(c => c.GameInstance != null && c.GameInstance.Challenge.GameId == game.Id &&
                        c.GameInstance.Challenge.Type == ChallengeType.DynamicContainer)
            .Select(c => new
            {
                c.GameInstance!.ChallengeId,
                c.GameInstance.ParticipationId,
                c.StartedAt
            })
            .ToArrayAsync(token);

        Dictionary<(int ChallengeId, int ParticipationId), DateTimeOffset> firstContainerStarts = new();

        foreach (var evt in containerEvents)
        {
            if (evt.Values is null || evt.Values.Count == 0)
                continue;

            if (!int.TryParse(evt.Values[0], out var challengeId))
                continue;

            if (!teamToParticipationId.TryGetValue(evt.TeamId, out var participationId))
                continue;

            if (!acceptedParticipationIds.Contains(participationId))
                continue;

            var key = (challengeId, participationId);
            if (!firstContainerStarts.TryGetValue(key, out var existing) || evt.PublishTimeUtc < existing)
                firstContainerStarts[key] = evt.PublishTimeUtc;
        }

        var containerFallback = containerStarts
            .Where(c => acceptedParticipationIds.Contains(c.ParticipationId))
            .GroupBy(c => (c.ChallengeId, c.ParticipationId))
            .ToDictionary(g => g.Key, g => g.Min(item => (DateTimeOffset?)item.StartedAt));

        Dictionary<int, HashSet<int>> activatedTeams = new();
        Dictionary<int, HashSet<int>> solvedTeams = new();
        Dictionary<int, List<int>> attemptsToSolve = new();
        Dictionary<int, List<double>> solveTimeMinutes = new();
        Dictionary<int, int> submissionCountByChallenge = new();

        foreach (var submissionGroup in submissions.GroupBy(s => (s.ChallengeId, s.ParticipationId)))
        {
            var (challengeId, participationId) = submissionGroup.Key;

            if (!acceptedParticipationIds.Contains(participationId))
                continue;

            var ordered = submissionGroup.OrderBy(s => s.SubmitTimeUtc).ToList();

            if (!submissionCountByChallenge.TryGetValue(challengeId, out var totalSubmissions))
                submissionCountByChallenge[challengeId] = ordered.Count;
            else
                submissionCountByChallenge[challengeId] = totalSubmissions + ordered.Count;

            if (!activatedTeams.TryGetValue(challengeId, out var activatedSet))
            {
                activatedSet = new HashSet<int>();
                activatedTeams[challengeId] = activatedSet;
            }

            activatedSet.Add(participationId);

            var firstAccepted = ordered.FirstOrDefault(s => s.Status == AnswerResult.Accepted);
            if (firstAccepted is null)
                continue;

            if (!solvedTeams.TryGetValue(challengeId, out var solvedSet))
            {
                solvedSet = new HashSet<int>();
                solvedTeams[challengeId] = solvedSet;
            }

            solvedSet.Add(participationId);

            if (!attemptsToSolve.TryGetValue(challengeId, out var attemptList))
            {
                attemptList = new List<int>();
                attemptsToSolve[challengeId] = attemptList;
            }

            var attemptCount = ordered.TakeWhile(s => s.SubmitTimeUtc <= firstAccepted.SubmitTimeUtc).Count();
            attemptList.Add(attemptCount);

            if (!dynamicChallengeIds.Contains(challengeId))
                continue;

            DateTimeOffset? startTime = null;
            if (firstContainerStarts.TryGetValue((challengeId, participationId), out var firstStart))
                startTime = firstStart;
            else if (containerFallback.TryGetValue((challengeId, participationId), out var fallback))
                startTime = fallback;

            if (startTime is null)
                continue;

            var duration = (firstAccepted.SubmitTimeUtc - startTime.Value).TotalMinutes;
            if (duration < 0)
                continue;

            if (!solveTimeMinutes.TryGetValue(challengeId, out var solvingList))
            {
                solvingList = new List<double>();
                solveTimeMinutes[challengeId] = solvingList;
            }

            solvingList.Add(duration);
        }

        foreach (var ((challengeId, participationId), _) in firstContainerStarts)
        {
            if (!activatedTeams.TryGetValue(challengeId, out var activatedSet))
            {
                activatedSet = new HashSet<int>();
                activatedTeams[challengeId] = activatedSet;
            }

            activatedSet.Add(participationId);
        }

        foreach (var ((challengeId, participationId), startedAt) in containerFallback)
        {
            if (startedAt is null)
                continue;

            if (!activatedTeams.TryGetValue(challengeId, out var activatedSet))
            {
                activatedSet = new HashSet<int>();
                activatedTeams[challengeId] = activatedSet;
            }

            activatedSet.Add(participationId);
        }

        List<ChallengeStatisticModel> result = new(); 

        foreach (var challenge in challenges)
        {
            activatedTeams.TryGetValue(challenge.Id, out var activatedSet);
            solvedTeams.TryGetValue(challenge.Id, out var solvedSet);
            attemptsToSolve.TryGetValue(challenge.Id, out var attemptList);
            solveTimeMinutes.TryGetValue(challenge.Id, out var solveList);
            submissionCountByChallenge.TryGetValue(challenge.Id, out var totalSubmissions);

            var attemptsMetric = attemptList is null
                ? new StatisticMetricModel()
                : BuildMetric(attemptList.Select(a => (double)a));

            var solveMetric = solveList is null
                ? new StatisticMetricModel()
                : BuildMetric(solveList);

            var solvedCount = solvedSet?.Count ?? 0;
            var activatedCount = activatedSet?.Count ?? 0;
            var completionRate = totalTeamCount == 0
                ? 0
                : (double)solvedCount / totalTeamCount;

            result.Add(new ChallengeStatisticModel
            {
                ChallengeId = challenge.Id,
                Title = challenge.Title,
                Category = challenge.Category,
                Type = challenge.Type,
                TotalTeamCount = totalTeamCount,
                ActivatedTeamCount = activatedCount,
                SolvedTeamCount = solvedCount,
                TotalSubmissionCount = totalSubmissions,
                CompletionRate = completionRate,
                AttemptsToSolve = attemptsMetric,
                SolveTimeMinutes = challenge.Type == ChallengeType.DynamicContainer ? solveMetric : new StatisticMetricModel()
            });
        }

        return result;
    }

    static StatisticMetricModel BuildMetric(IEnumerable<double> source)
    {
        var ordered = source
            .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
            .OrderBy(value => value)
            .ToArray();

        if (ordered.Length == 0)
            return new StatisticMetricModel();

        var median = ordered.Length % 2 == 0
            ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2.0
            : ordered[ordered.Length / 2];

        return new StatisticMetricModel
        {
            Average = ordered.Average(),
            Median = median,
            Minimum = ordered[0],
            Maximum = ordered[^1]
        };
    }

    public async Task<TaskStatus> DeleteGame(Game game, CancellationToken token = default)
    {
        var trans = await BeginTransactionAsync(token);

        try
        {
            var count = await Context.GameChallenges.Where(i => i.Game == game).CountAsync(token);
            logger.SystemLog(
                StaticLocalizer[nameof(Resources.Program.GameRepository_GameDeletionChallenges), game.Title,
                    count], TaskStatus.Pending,
                LogLevel.Debug
            );

            foreach (var chal in await Context.GameChallenges.Where(c => c.Game == game)
                         .ToArrayAsync(token))
                await challengeRepository.RemoveChallenge(chal, false, token);

            count = await Context.Participations.Where(i => i.Game == game).CountAsync(token);

            logger.SystemLog(
                StaticLocalizer[nameof(Resources.Program.GameRepository_GameDeletionTeams), game.Title, count],
                TaskStatus.Pending, LogLevel.Debug
            );

            foreach (var part in await Context.Participations.Where(p => p.Game == game).ToArrayAsync(token))
                await participationRepository.RemoveParticipation(part, false, token);

            Context.Remove(game);

            await SaveAsync(token);
            await trans.CommitAsync(token);

            await cacheHelper.FlushGameListCache(token);
            await cacheHelper.FlushRecentGamesCache(token);

            await cacheHelper.RemoveAsync(CacheKey.ScoreBoard(game.Id), token);

            return TaskStatus.Success;
        }
        catch (Exception e)
        {
            logger.SystemLog(StaticLocalizer[nameof(Resources.Program.Game_DeletionFailed)], TaskStatus.Pending,
                LogLevel.Debug);
            logger.SystemLog(e.Message, TaskStatus.Failed, LogLevel.Warning);
            await trans.RollbackAsync(token);

            return TaskStatus.Failed;
        }
    }

    public async Task DeleteAllWriteUps(Game game, CancellationToken token = default)
    {
        await Context.Entry(game).Collection(g => g.Participations).LoadAsync(token);

        logger.SystemLog(
            StaticLocalizer[nameof(Resources.Program.GameRepository_GameDeletionTeams), game.Title,
                game.Participations.Count],
            TaskStatus.Pending,
            LogLevel.Debug);

        foreach (var part in game.Participations)
            await participationRepository.DeleteParticipationWriteUp(part, token);
    }

    public Task<Game[]> GetGames(int count, int skip, CancellationToken token) =>
        Context.Games.OrderByDescending(g => g.Id).Skip(skip).Take(count).ToArrayAsync(token);

    // By xfoxfu & GZTimeWalker @ 2022/04/03
    // Refactored by GZTimeWalker @ 2024/08/31
    public async Task<ScoreboardModel> GenScoreboard(Game game, CancellationToken token = default)
    {
        Dictionary<int, ScoreboardItem> items; // participant id -> scoreboard item
        Dictionary<int, ChallengeScoreState> challenges;
        List<ChallengeItem> submissions;

        // 0. Begin transaction
        await using (var trans = await Context.Database.BeginTransactionAsync(token))
        {
            // 1. Fetch all teams with their members from Participations, into ScoreboardItem
            items = await Context.Participations
                .AsNoTracking()
                .IgnoreAutoIncludes()
                .Where(p => p.GameId == game.Id &&
                            (p.Status == ParticipationStatus.Accepted ||
                             p.Status == ParticipationStatus.Hidden))
                .Include(p => p.Team)
                .Select(p => new ScoreboardItem
                {
                    Id = p.Team.Id,
                    Bio = p.Team.Bio,
                    Name = p.Team.Name,
                    Avatar = p.Team.AvatarUrl,
                    Division = p.Division,
                    ParticipantId = p.Id,
                    TeamInfo = p.Team,
                    IsHidden = p.Status == ParticipationStatus.Hidden,
                    // pending fields: SolvedChallenges
                    Rank = 0,
                    LastSubmissionTime = DateTimeOffset.MinValue
                    // update: only store accepted challenges
                }).ToDictionaryAsync(i => i.ParticipantId, token);

            // 2. Fetch all challenges from GameChallenges, into ChallengeInfo
            challenges = await Context.GameChallenges
                .AsNoTracking()
                .IgnoreAutoIncludes()
                .Where(c => c.GameId == game.Id && c.IsEnabled)
                .OrderBy(c => c.Category)
                .ThenBy(c => c.Title)
                .Select(c => new ChallengeScoreState
                {
                    Info = new ChallengeInfo
                    {
                        Id = c.Id,
                        Title = c.Title,
                        Category = c.Category,
                        Score = c.CurrentScore,
                        SolvedCount = c.AcceptedCount,
                        DisableBloodBonus = c.DisableBloodBonus,
                        ExpectedSolveTimeUtc = c.ExpectedSolveTimeUtc,
                        LateSolveScore = GameChallenge.CalculateLateSolveScore(c.OriginalScore, c.MinScoreRate)
                    },
                    OriginalScore = c.OriginalScore,
                    MinScoreRate = c.MinScoreRate,
                    Difficulty = c.Difficulty,
                    ExpectedSolveTimeUtc = c.ExpectedSolveTimeUtc,
                    LateSolveScore = GameChallenge.CalculateLateSolveScore(c.OriginalScore, c.MinScoreRate),
                    AwardedSolves = 0
                }).ToDictionaryAsync(c => c.Info.Id, token);

            // 3. fetch all needed submissions into a list of ChallengeItem
            //    **take only the first accepted submission for each challenge & team**
            submissions = await Context.Submissions
                .AsNoTracking()
                .IgnoreAutoIncludes()
                .Include(s => s.User)
                .Include(s => s.GameChallenge)
                .Where(s => s.Status == AnswerResult.Accepted
                            && s.GameId == game.Id
                            && s.GameChallenge != null
                            && s.GameChallenge.IsEnabled
                            && s.SubmitTimeUtc < game.EndTimeUtc)
                .GroupBy(s => new { s.ChallengeId, s.ParticipationId })
                .Where(g => g.Any())
                .Select(g =>
                    g.OrderBy(s => s.SubmitTimeUtc)
                        .Take(1)
                        .Select(s =>
                            new ChallengeItem
                            {
                                Id = s.ChallengeId,
                                UserName = s.UserName,
                                SubmitTimeUtc = s.SubmitTimeUtc,
                                ParticipantId = s.ParticipationId,
                                // pending fields
                                Score = 0,
                                Type = SubmissionType.Normal
                            }
                        )
                        .First()
                )
                .ToListAsync(token);

            await trans.CommitAsync(token);
        }

        // 4. sort challenge items by submit time, and update the Score and Type fields
        var noBonus = game.BloodBonus.NoBonus;

        float[] bloodFactors =
        [
            game.BloodBonus.FirstBloodFactor,
            game.BloodBonus.SecondBloodFactor,
            game.BloodBonus.ThirdBloodFactor
        ];

        foreach (var item in submissions.OrderBy(s => s.SubmitTimeUtc))
        {
            // skip if the team is not in the scoreboard
            if (!items.TryGetValue(item.ParticipantId, out var scoreboardItem))
                continue;

            var challenge = challenges[item.Id];
            var challengeInfo = challenge.Info;
            var isHiddenTeam = scoreboardItem.IsHidden;
            int baseScore;
            bool isLateSolve;

            if (isHiddenTeam)
            {
                isLateSolve = item.SubmitTimeUtc > challenge.ExpectedSolveTimeUtc;
                if (isLateSolve)
                    baseScore = challenge.LateSolveScore;
                else
                {
                    var solveNumber = challenge.AwardedSolves + 1;
                    baseScore = GameChallenge.CalculateScore(challenge.OriginalScore, challenge.MinScoreRate,
                        challenge.Difficulty, solveNumber, item.SubmitTimeUtc, challenge.ExpectedSolveTimeUtc);
                }
            }
            else
            {
                var result = challenge.AwardScore(item.SubmitTimeUtc);
                baseScore = result.score;
                isLateSolve = result.isLate;
            }

            // 4.1. generate bloods if eligible and before the expected completion time
            if (!isHiddenTeam && !isLateSolve && challengeInfo is { DisableBloodBonus: false, Bloods.Count: < 3 })
            {
                item.Type = challengeInfo.Bloods.Count switch
                {
                    0 => SubmissionType.FirstBlood,
                    1 => SubmissionType.SecondBlood,
                    2 => SubmissionType.ThirdBlood,
                    _ => throw new UnreachableException()
                };
                challengeInfo.Bloods.Add(new Blood
                {
                    Id = scoreboardItem.Id,
                    Avatar = scoreboardItem.Avatar,
                    Name = scoreboardItem.Name,
                    SubmitTimeUtc = item.SubmitTimeUtc
                });
            }
            else
            {
                item.Type = SubmissionType.Normal;
            }

            // 4.2. update score
            if (isLateSolve)
            {
                item.Score = baseScore;
            }
            else if (noBonus)
            {
                item.Score = baseScore;
            }
            else
            {
                item.Score = item.Type switch
                {
                    SubmissionType.Unaccepted => throw new UnreachableException(),
                    SubmissionType.FirstBlood => Convert.ToInt32(baseScore * bloodFactors[0]),
                    SubmissionType.SecondBlood => Convert.ToInt32(baseScore * bloodFactors[1]),
                    SubmissionType.ThirdBlood => Convert.ToInt32(baseScore * bloodFactors[2]),
                    SubmissionType.Normal => baseScore,
                    _ => throw new ArgumentException(nameof(item.Type))
                };
            }

            // 4.3. update scoreboard item
            scoreboardItem.SolvedChallenges.Add(item);
            scoreboardItem.Score += item.Score;
            scoreboardItem.LastSubmissionTime = item.SubmitTimeUtc;
        }

        // 5. sort scoreboard items by score and last submission time (visible teams only)
        var orderedVisibleItems = items.Values
            .Where(i => !i.IsHidden)
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.LastSubmissionTime)
            .ToList();
        var hiddenItemsOrdered = items.Values
            .Where(i => i.IsHidden)
            .OrderByDescending(i => i.Score)
            .ThenBy(i => i.LastSubmissionTime)
            .ToList();

        // 6. update rank and organization rank
        var ranks = new Dictionary<string, int>();
        var currentRank = 1;
        Dictionary<string, HashSet<int>> orgTeams = new() { ["all"] = [] };
        foreach (var item in orderedVisibleItems)
        {
            item.Rank = currentRank++;

            if (item.Rank <= 10)
                orgTeams["all"].Add(item.Id);

            if (item.Division is null)
                continue;

            if (ranks.TryGetValue(item.Division, out var rank))
            {
                item.DivisionRank = rank + 1;
                ranks[item.Division]++;
                if (item.DivisionRank <= 10)
                    orgTeams[item.Division].Add(item.Id);
            }
            else
            {
                item.DivisionRank = 1;
                ranks[item.Division] = 1;
                orgTeams[item.Division] = [item.Id];
            }
        }

        // reset hidden items' ranks
        foreach (var hiddenItem in hiddenItemsOrdered)
        {
            hiddenItem.Rank = 0;
            hiddenItem.DivisionRank = null;
        }

        var finalOrder = orderedVisibleItems.Concat(hiddenItemsOrdered).ToList();
        items = finalOrder.ToDictionary(i => i.Id); // team id -> scoreboard item (includes hidden)

        // 7. generate top timelines by solved challenges
        var timelines = orgTeams.ToDictionary(
            i => i.Key,
            i => i.Value.Select(tid =>
                {
                    var item = items[tid];
                    return new TopTimeLine
                    {
                        Id = item.Id,
                        Name = item.Name,
                        Items = item.SolvedChallenges
                            .OrderBy(c => c.SubmitTimeUtc)
                            .Aggregate(new List<TimeLine>(), (acc, c) =>
                            {
                                var last = acc.LastOrDefault();
                                acc.Add(new TimeLine { Score = (last?.Score ?? 0) + c.Score, Time = c.SubmitTimeUtc });
                                return acc;
                            })
                    };
                }
            )
        );

        // Update challenge info with recalculated solved count and dynamic score excluding hidden teams
        foreach (var challenge in challenges.Values)
        {
            var solvedCount = orderedVisibleItems.Count(i => i.SolvedChallenges.Any(c => c.Id == challenge.Info.Id));
            challenge.Info.SolvedCount = solvedCount;
            challenge.Info.Score = GameChallenge.CalculateScore(
                challenge.OriginalScore,
                challenge.MinScoreRate,
                challenge.Difficulty,
                solvedCount + 1,
                DateTimeOffset.UtcNow,
                challenge.ExpectedSolveTimeUtc);
        }

        // 8. construct the final scoreboard model
        var challengesDict = challenges
            .Values
            .Select(c => c.Info)
            .GroupBy(c => c.Category)
            .ToDictionary(c => c.Key, c => c.AsEnumerable());

        return new()
        {
            Challenges = challengesDict,
            Items = items,
            TimeLines = timelines,
            BloodBonusValue = game.BloodBonus.Val
        };
    }
}
