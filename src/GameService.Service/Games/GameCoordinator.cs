using System.Collections.Concurrent;
using GameService.Domain.Dtos;
using GameService.Domain.Events;
using GameService.Domain.Models;
using GameService.Service.Exceptions;
using GameService.Service.Interfaces;
using GameService.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameService.Service.Games;

// SCRUM-102/103: the real-time flow of a game.
//
//   created --(all players joined, or JoinTimeoutSeconds)--> Playing: Prefetch rounds 1..5,
//   then for each round:
//     wait for Ready from every connected player (or ReadyTimeoutSeconds)
//     --> RoundStarted(round) and Prefetch(round + 5)
//     --> answers (PlayerAnswered for each) until every connected player has answered,
//         or endsAt + AnswerGraceSeconds
//     --> RoundEnded with the song and everyone's result
//     --> PauseBetweenRoundsSeconds, then the next round.
//   After the last RoundEnded (SCRUM-104): Finished, GameEnded with placements, and the
//   GameFinished message to StatisticService over RabbitMQ.
// Each game has its own lock. Messages are sent while it is held, so they reach the group in
// the order the state changed (Prefetch before the RoundStarted that needs it).
// All timers come from TimeProvider, so tests control time.
public sealed class GameCoordinator(
    IGameStore store,
    IGameNotifier notifier,
    IResultPublisher resultPublisher,
    TimeProvider time,
    IOptions<GameOptions> options,
    ILogger<GameCoordinator> logger,
    Random? random = null) : IGameCoordinator, IDisposable
{
    private readonly GameOptions _options = options.Value;
    private readonly Random _random = random ?? Random.Shared;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, (Guid GameId, int UserId)> _connections = new();

    // Order is 1 for the first answer of the round, 2 for the next, and so on.
    private sealed record Answer(int Slot, int AfterMs, int Order);

    private sealed class Session(Game game)
    {
        public Game Game { get; } = game;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public HashSet<int> Joined { get; } = [];
        public Dictionary<int, HashSet<string>> Connections { get; } = [];
        public Dictionary<int, HashSet<int>> Ready { get; } = [];
        // Answers for the active round, by userId. Judged when the round ends.
        public Dictionary<int, Answer> Answers { get; } = [];
        // The round the server is waiting for Ready for; 0 when it is not waiting.
        public int PreparingRound { get; set; }
        public ITimer? Timer { get; set; }

        public void SetTimer(ITimer? timer)
        {
            Timer?.Dispose();
            Timer = timer;
        }
    }

    public void GameCreated(Game game)
    {
        // Forget games the store has removed (finished more than 30 minutes ago).
        foreach (var id in _sessions.Keys.Where(id => store.Get(id) is null).ToList())
            if (_sessions.TryRemove(id, out var old))
                old.SetTimer(null);

        var session = _sessions.GetOrAdd(game.Id, _ => new Session(game));
        session.SetTimer(time.CreateTimer(_ => Fire(() => OnJoinTimeoutAsync(game.Id)), null,
            TimeSpan.FromSeconds(_options.JoinTimeoutSeconds), Timeout.InfiniteTimeSpan));
    }

    public async Task<GameStateDto> JoinAsync(Guid gameId, int userId, string connectionId, Func<Task> subscribe)
    {
        var session = GetSession(gameId);
        await session.Lock.WaitAsync();
        try
        {
            var player = PlayerOrThrow(session, userId);

            // Subscribe before the game can start, so this connection gets the first Prefetch.
            await subscribe();
            session.Joined.Add(userId);
            if (!session.Connections.TryGetValue(userId, out var connections))
                session.Connections[userId] = connections = [];
            connections.Add(connectionId);
            _connections[connectionId] = (gameId, userId);

            if (session.Game.Status == GameStatus.Waiting && session.Joined.Count == session.Game.Players.Count)
                await StartGameLockedAsync(session);
            else if (session.PreparingRound > 0)
                await StartRoundIfAllReadyLockedAsync(session);

            return BuildState(session, player);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    public async Task ReadyAsync(Guid gameId, int userId, int round)
    {
        var session = GetSession(gameId);
        await session.Lock.WaitAsync();
        try
        {
            PlayerOrThrow(session, userId);
            if (round < 1 || round > session.Game.Settings.Rounds)
                throw new GameRuleException($"Round must be between 1 and {session.Game.Settings.Rounds}.");
            if (round <= session.Game.CurrentRound)
                return; // that round has already started

            if (!session.Ready.TryGetValue(round, out var ready))
                session.Ready[round] = ready = [];
            ready.Add(userId);

            await StartRoundIfAllReadyLockedAsync(session);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    public async Task SubmitAnswerAsync(Guid gameId, int userId, int round, int slotIndex)
    {
        var session = GetSession(gameId);
        await session.Lock.WaitAsync();
        try
        {
            var player = PlayerOrThrow(session, userId);
            var active = session.Game.ActiveRound;
            if (active is null || active.Round != round)
                throw new GameRuleException("That round is not being played.");
            if (time.GetUtcNow() > active.EndsAt.AddSeconds(_options.AnswerGraceSeconds))
                throw new GameRuleException("The time is up.");
            if (session.Answers.ContainsKey(userId))
                throw new GameRuleException("You have already answered.");
            if (slotIndex < 0 || slotIndex > player.Timeline.Count)
                throw new GameRuleException($"slotIndex must be between 0 and {player.Timeline.Count}.");

            var afterMs = AnsweredAfterMs(active, session.Game.Settings.AnswerTimeSeconds);
            session.Answers[userId] = new Answer(slotIndex, afterMs, session.Answers.Count + 1);
            await SendAsync(() => notifier.PlayerAnsweredAsync(gameId, new PlayerAnsweredDto(userId, afterMs)), gameId);

            if (ConnectedPlayers(session).All(session.Answers.ContainsKey))
                await EndRoundLockedAsync(session);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    public async Task DisconnectedAsync(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var entry) || !_sessions.TryGetValue(entry.GameId, out var session))
            return;

        await session.Lock.WaitAsync();
        try
        {
            if (session.Connections.TryGetValue(entry.UserId, out var connections))
                connections.Remove(connectionId);
            // The player who left may have been the only one the server was waiting for.
            await StartRoundIfAllReadyLockedAsync(session);
            var connected = ConnectedPlayers(session);
            if (session.Game.ActiveRound is not null && connected.Count > 0 && connected.All(session.Answers.ContainsKey))
                await EndRoundLockedAsync(session);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    public async Task PrepareRoundAsync(Guid gameId, int round)
    {
        var session = GetSession(gameId);
        await session.Lock.WaitAsync();
        try
        {
            await PrepareRoundLockedAsync(session, round);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task OnJoinTimeoutAsync(Guid gameId)
    {
        if (!_sessions.TryGetValue(gameId, out var session)) return;
        await session.Lock.WaitAsync();
        try
        {
            if (session.Game.Status == GameStatus.Waiting)
            {
                logger.LogInformation("Game {GameId} starts after the join timeout with {Joined}/{Total} players",
                    gameId, session.Joined.Count, session.Game.Players.Count);
                await StartGameLockedAsync(session);
            }
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task OnReadyTimeoutAsync(Guid gameId, int round)
    {
        if (!_sessions.TryGetValue(gameId, out var session)) return;
        await session.Lock.WaitAsync();
        try
        {
            if (session.PreparingRound == round)
                await StartRoundLockedAsync(session);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task OnRoundTimeoutAsync(Guid gameId, int round)
    {
        if (!_sessions.TryGetValue(gameId, out var session)) return;
        await session.Lock.WaitAsync();
        try
        {
            if (session.Game.ActiveRound?.Round == round)
                await EndRoundLockedAsync(session);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private async Task StartGameLockedAsync(Session session)
    {
        var game = session.Game;
        if (game.Status != GameStatus.Waiting) return;

        session.SetTimer(null);
        game.Status = GameStatus.Playing;
        game.StartedAt = time.GetUtcNow().UtcDateTime;

        var files = PrefetchFor(game, RoundTiming.Rounds(1, _options.PrefetchWindow, game.Settings.Rounds));
        await SendAsync(() => notifier.PrefetchAsync(game.Id, files), game.Id);
        await PrepareRoundLockedAsync(session, 1);
    }

    private async Task PrepareRoundLockedAsync(Session session, int round)
    {
        var game = session.Game;
        if (game.Status != GameStatus.Playing || round < 1 || round > game.Settings.Rounds || round <= game.CurrentRound
            || game.ActiveRound is not null) // never two rounds at once
            return;

        session.PreparingRound = round;
        if (AllConnectedReady(session, round))
        {
            await StartRoundLockedAsync(session);
            return;
        }

        session.SetTimer(time.CreateTimer(_ => Fire(() => OnReadyTimeoutAsync(game.Id, round)), null,
            TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds), Timeout.InfiniteTimeSpan));
    }

    private async Task StartRoundIfAllReadyLockedAsync(Session session)
    {
        if (session.PreparingRound > 0 && AllConnectedReady(session, session.PreparingRound))
            await StartRoundLockedAsync(session);
    }

    private async Task StartRoundLockedAsync(Session session)
    {
        var game = session.Game;
        var round = session.PreparingRound;
        if (round == 0) return;

        session.PreparingRound = 0;
        session.SetTimer(null);

        var answerTime = game.Settings.AnswerTimeSeconds;
        var offset = RoundTiming.PickStartOffset(_random, game.SongForRound(round).DurationSeconds, answerTime);
        var playbackStartsAt = time.GetUtcNow().AddMilliseconds(_options.StartDelayMs);

        game.CurrentRound = round;
        game.ActiveRound = new ActiveRound
        {
            Round = round,
            StartOffsetSeconds = offset,
            PlaybackStartsAt = playbackStartsAt,
            EndsAt = playbackStartsAt.AddSeconds(answerTime)
        };
        foreach (var done in session.Ready.Keys.Where(r => r <= round).ToList())
            session.Ready.Remove(done);
        session.Answers.Clear();
        session.SetTimer(time.CreateTimer(_ => Fire(() => OnRoundTimeoutAsync(game.Id, round)), null,
            TimeSpan.FromMilliseconds(_options.StartDelayMs) + TimeSpan.FromSeconds(answerTime + _options.AnswerGraceSeconds),
            Timeout.InfiniteTimeSpan));

        var started = new RoundStartedDto(round, game.Settings.Rounds, _options.StartDelayMs, offset, answerTime);
        await SendAsync(() => notifier.RoundStartedAsync(game.Id, started), game.Id);

        var next = round + _options.PrefetchWindow;
        if (next <= game.Settings.Rounds)
        {
            var files = PrefetchFor(game, [next]);
            await SendAsync(() => notifier.PrefetchAsync(game.Id, files), game.Id);
        }
    }

    // Judges every answer, updates scores and timelines, sends RoundEnded and schedules the next round.
    private async Task EndRoundLockedAsync(Session session)
    {
        var game = session.Game;
        var active = game.ActiveRound;
        if (active is null) return;

        game.ActiveRound = null;
        session.SetTimer(null);
        var song = game.SongForRound(active.Round);

        var results = new List<RoundResultDto>();
        foreach (var player in game.Players)
        {
            var answer = session.Answers.GetValueOrDefault(player.UserId);
            int? slot = answer?.Slot;
            var correct = slot is { } index && GameEngine.IsCorrectPlacement(player.Timeline, index, song.Year);
            // The range is read before this round's card goes in.
            var range = slot is { } at ? RangeOf(player.Timeline, at) : null;
            if (slot is not null)
                player.Answered++;
            if (correct)
            {
                player.Timeline.Insert(slot!.Value, new TimelineCard { Song = song });
                player.Score++;
                player.Correct++;
            }
            player.History.Add(new RoundHistory { Round = active.Round, Correct = correct, AnsweredAfterMs = answer?.AfterMs });
            results.Add(new RoundResultDto(player.UserId, player.UserName, slot, correct, player.Score, Cards(player),
                answer?.AfterMs, range));
        }
        session.Answers.Clear();

        var ended = new RoundEndedDto(active.Round, ToCard(song), results);
        await SendAsync(() => notifier.RoundEndedAsync(game.Id, ended), game.Id);

        if (active.Round < game.Settings.Rounds)
        {
            var next = active.Round + 1;
            session.SetTimer(time.CreateTimer(_ => Fire(() => PrepareRoundAsync(game.Id, next)), null,
                TimeSpan.FromSeconds(_options.PauseBetweenRoundsSeconds), Timeout.InfiniteTimeSpan));
        }
        else
        {
            await FinishGameLockedAsync(session);
        }
    }

    // SCRUM-104: the last round is over. Players get GameEnded even if publishing to RabbitMQ fails.
    private async Task FinishGameLockedAsync(Session session)
    {
        var game = session.Game;
        if (game.Status == GameStatus.Finished) return;

        game.Status = GameStatus.Finished;
        game.EndedAt = time.GetUtcNow().UtcDateTime;

        // Highest score first; ties in the order the players were in the lobby.
        var players = game.Players.OrderByDescending(p => p.Score).ToList();
        var placements = PlacementCalculator.Calculate(players.Select(p => p.Score).ToList());

        var standings = players.Select((p, i) =>
            new StandingDto(p.UserId, p.UserName, p.Score, p.Correct, p.Answered, placements[i], Cards(p))).ToList();
        await SendAsync(() => notifier.GameEndedAsync(game.Id, new GameEndedDto(game.Id, standings)), game.Id);

        // Same message and field names as SCRUM-83/84; StatisticService saves it as is.
        var finished = new GameFinishedEvent
        {
            GameId = game.Id,
            Rounds = game.Settings.Rounds,
            StartTime = game.StartedAt ?? game.EndedAt.Value,
            EndTime = game.EndedAt.Value,
            CatId = game.Settings.CategoryId,
            CategoryName = game.Settings.CategoryName,
            Players = players.Select((p, i) => new PlayerGameResult
            {
                UserId = p.UserId,
                UserName = p.UserName,
                Score = p.Score,
                CorrectAnswers = p.Correct,
                AnsweredRounds = p.Answered,
                Placement = placements[i]
            }).ToList()
        };
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await resultPublisher.PublishGameFinishedAsync(finished, timeout.Token);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Publishing GameFinished for game {GameId} failed; the result is not in the statistics", game.Id);
        }
    }

    // Milliseconds from playback start to now, clamped to [0, answer time].
    private int AnsweredAfterMs(ActiveRound round, int answerTimeSeconds)
    {
        var ms = (time.GetUtcNow() - round.PlaybackStartsAt).TotalMilliseconds;
        return (int)Math.Clamp(Math.Round(ms), 0, answerTimeSeconds * 1000);
    }

    // [from, to]: the years of the cards on either side of the slot; null for an open end.
    private static IReadOnlyList<int?> RangeOf(List<TimelineCard> timeline, int slot) =>
    [
        slot > 0 ? timeline[slot - 1].Year : null,
        slot < timeline.Count ? timeline[slot].Year : null
    ];

    private static List<int> ConnectedPlayers(Session session) =>
        session.Connections.Where(c => c.Value.Count > 0).Select(c => c.Key).ToList();

    private static CardDto ToCard(Song song) => new(song.Title, song.Artists, song.Year);

    private static List<CardDto> Cards(GamePlayer player) =>
        player.Timeline.OrderBy(c => c.Year).Select(c => ToCard(c.Song)).ToList();

    // True when at least one player is connected and every connected player is ready for the round.
    private static bool AllConnectedReady(Session session, int round)
    {
        var connected = ConnectedPlayers(session);
        return connected.Count > 0
               && session.Ready.TryGetValue(round, out var ready)
               && connected.All(ready.Contains);
    }

    private GameStateDto BuildState(Session session, GamePlayer me)
    {
        var game = session.Game;
        var now = time.GetUtcNow();
        var active = game.ActiveRound is { } a && now < a.EndsAt ? a : null;

        // Files the client should have: the round being played (its blob is needed for playback)
        // and the next PrefetchWindow rounds.
        var firstNeeded = active is not null ? game.CurrentRound : game.CurrentRound + 1;
        var prefetch = game.Status == GameStatus.Finished
            ? []
            : PrefetchFor(game, RoundTiming.Rounds(firstNeeded, game.CurrentRound + _options.PrefetchWindow, game.Settings.Rounds));

        // Only rounds that have ended: a round still in its grace second (ActiveRound set) is not.
        var lastEnded = game.ActiveRound is null ? game.CurrentRound : game.CurrentRound - 1;
        var pastSongs = Enumerable.Range(1, Math.Max(0, lastEnded))
            .Select(r => new PastSongDto(r, ToCard(game.SongForRound(r))))
            .ToList();

        return new GameStateDto(
            game.Id,
            new GameSettingsDto(game.Settings.CategoryId, game.Settings.CategoryName, game.Settings.Rounds, game.Settings.AnswerTimeSeconds),
            game.CurrentRound,
            game.Settings.Rounds,
            game.Players.Select(p => new PlayerSummaryDto(
                p.UserId,
                p.UserName,
                p.Score,
                Cards(p),
                p.History.Select(h => new RoundHistoryDto(h.Round, h.Correct, h.AnsweredAfterMs)).ToList(),
                game.ActiveRound is not null && session.Answers.TryGetValue(p.UserId, out var answer) ? answer.Order : null))
                .ToList(),
            Cards(me),
            prefetch,
            active is null ? null : RemainingOf(active, game, now),
            pastSongs);
    }

    // What is left of a round that is being played, for a player who (re)joins in the middle of it.
    private static RoundStartedDto RemainingOf(ActiveRound round, Game game, DateTimeOffset now)
    {
        var untilPlayback = round.PlaybackStartsAt - now;
        if (untilPlayback > TimeSpan.Zero)
            return new RoundStartedDto(round.Round, game.Settings.Rounds, (int)Math.Ceiling(untilPlayback.TotalMilliseconds),
                round.StartOffsetSeconds, game.Settings.AnswerTimeSeconds);

        var played = (int)(now - round.PlaybackStartsAt).TotalSeconds;
        return new RoundStartedDto(round.Round, game.Settings.Rounds, 0,
            round.StartOffsetSeconds + played, game.Settings.AnswerTimeSeconds - played);
    }

    private static List<PrefetchDto> PrefetchFor(Game game, IEnumerable<int> rounds) =>
        rounds.Select(r => new PrefetchDto(r, game.SongForRound(r).AudioUrl)).ToList();

    private Session GetSession(Guid gameId)
    {
        if (_sessions.TryGetValue(gameId, out var session)) return session;
        var game = store.Get(gameId) ?? throw new GameAccessException("The game does not exist.");
        return _sessions.GetOrAdd(gameId, _ => new Session(game));
    }

    private static GamePlayer PlayerOrThrow(Session session, int userId) =>
        session.Game.Players.FirstOrDefault(p => p.UserId == userId)
        ?? throw new GameAccessException("You are not a player in this game.");

    // A failed send must not leave the game stuck, so it is logged and the flow continues.
    private async Task SendAsync(Func<Task> send, Guid gameId)
    {
        try
        {
            await send();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Sending a message to game {GameId} failed", gameId);
        }
    }

    private void Fire(Func<Task> work) => _ = RunTimerAsync(work);

    private async Task RunTimerAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e)
        {
            logger.LogError(e, "A game timer failed");
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.SetTimer(null);
    }
}
