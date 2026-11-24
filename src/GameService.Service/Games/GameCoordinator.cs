using System.Collections.Concurrent;
using GameService.Domain.Dtos;
using GameService.Domain.Models;
using GameService.Service.Exceptions;
using GameService.Service.Interfaces;
using GameService.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GameService.Service.Games;

// SCRUM-102: the real-time flow of a game, from players joining to each RoundStarted.
//
//   created --(all players joined, or JoinTimeoutSeconds)--> Playing: Prefetch rounds 1..5,
//   then for each round: wait for Ready from every connected player (or ReadyTimeoutSeconds)
//   --> RoundStarted(round) and Prefetch(round + 5).
//
// Ending a round and starting the next one belong to SCRUM-103, which calls PrepareRoundAsync.
// Each game has its own lock. Messages are sent while it is held, so they reach the group in
// the order the state changed (Prefetch before the RoundStarted that needs it).
// All timers come from TimeProvider, so tests control time.
public sealed class GameCoordinator(
    IGameStore store,
    IGameNotifier notifier,
    TimeProvider time,
    IOptions<GameOptions> options,
    ILogger<GameCoordinator> logger,
    Random? random = null) : IGameCoordinator, IDisposable
{
    private readonly GameOptions _options = options.Value;
    private readonly Random _random = random ?? Random.Shared;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, (Guid GameId, int UserId)> _connections = new();

    private sealed class Session(Game game)
    {
        public Game Game { get; } = game;
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public HashSet<int> Joined { get; } = [];
        public Dictionary<int, HashSet<string>> Connections { get; } = [];
        public Dictionary<int, HashSet<int>> Ready { get; } = [];
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

    public async Task DisconnectedAsync(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var entry) || !_sessions.TryGetValue(entry.GameId, out var session))
            return;

        await session.Lock.WaitAsync();
        try
        {
            if (session.Connections.TryGetValue(entry.UserId, out var connections))
                connections.Remove(connectionId);
            // The player who left may have been the only one the round was waiting for.
            await StartRoundIfAllReadyLockedAsync(session);
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
        if (game.Status != GameStatus.Playing || round < 1 || round > game.Settings.Rounds || round <= game.CurrentRound)
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

        var started = new RoundStartedDto(round, game.Settings.Rounds, _options.StartDelayMs, offset, answerTime);
        await SendAsync(() => notifier.RoundStartedAsync(game.Id, started), game.Id);

        var next = round + _options.PrefetchWindow;
        if (next <= game.Settings.Rounds)
        {
            var files = PrefetchFor(game, [next]);
            await SendAsync(() => notifier.PrefetchAsync(game.Id, files), game.Id);
        }
    }

    // True when at least one player is connected and every connected player is ready for the round.
    private static bool AllConnectedReady(Session session, int round)
    {
        var connected = session.Connections.Where(c => c.Value.Count > 0).Select(c => c.Key).ToList();
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

        return new GameStateDto(
            game.Id,
            new GameSettingsDto(game.Settings.CategoryId, game.Settings.CategoryName, game.Settings.Rounds, game.Settings.AnswerTimeSeconds),
            game.CurrentRound,
            game.Settings.Rounds,
            game.Players.Select(p => new PlayerSummaryDto(p.UserId, p.UserName, p.Score)).ToList(),
            me.Timeline.OrderBy(c => c.Year).Select(c => new CardDto(c.Song.Title, c.Song.Artists, c.Song.Year)).ToList(),
            prefetch,
            active is null ? null : RemainingOf(active, game, now));
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
