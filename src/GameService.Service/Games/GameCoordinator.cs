using System.Collections.Concurrent;
using GameService.Domain.Dtos;
using GameService.Domain.Events;
using GameService.Domain.Models;
using GameService.Repository.State;
using GameService.Service.Exceptions;
using GameService.Service.Interfaces;
using GameService.Service.Metrics;
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
//   After the last RoundEnded and the same pause, so its reveal can be seen (SCRUM-170):
//   Finished, GameEnded with placements, and the GameFinished message to StatisticService
//   over RabbitMQ (SCRUM-104).
//
// ADR-0004: the coordinator itself holds no game. Every call and every deadline does
//   lock the game -> load its GameSession -> change it and send messages -> save it -> unlock,
// with the session in Redis and the lock in Redis in the cluster, so any GameService pod can serve
// any call for any game, and a pod can die or be replaced without ending its games. The game's timers
// are deadlines (DeadlineKind) in a shared queue that whichever pod claims first handles.
// Messages are sent while the lock is held, so they reach the group (SignalR Redis backplane) in the
// order the state changed. Time comes from TimeProvider, so tests control it.
public sealed class GameCoordinator : IGameCoordinator, IDisposable
{
    private readonly IGameSessionStore _store;
    private readonly IGameLocks _locks;
    private readonly IDeadlineQueue _deadlines;
    private readonly IGameNotifier _notifier;
    private readonly IResultPublisher _resultPublisher;
    private readonly TimeProvider _time;
    private readonly GameOptions _options;
    private readonly ILogger<GameCoordinator> _logger;
    private readonly Random _random;
    private readonly GameMetrics? _metrics;

    // This pod's hub connections, to find the game when one disconnects (only this pod hears that).
    private readonly ConcurrentDictionary<string, (Guid GameId, int UserId)> _connections = new();

    public GameCoordinator(
        IGameSessionStore store,
        IGameLocks locks,
        IDeadlineQueue deadlines,
        IGameNotifier notifier,
        IResultPublisher resultPublisher,
        TimeProvider time,
        IOptions<GameOptions> options,
        ILogger<GameCoordinator> logger,
        Random? random = null,
        GameMetrics? metrics = null)
    {
        _store = store;
        _locks = locks;
        _deadlines = deadlines;
        _notifier = notifier;
        _resultPublisher = resultPublisher;
        _time = time;
        _options = options.Value;
        _logger = logger;
        _random = random ?? Random.Shared;
        _metrics = metrics;
        _deadlines.SetHandler(OnDeadlineAsync);
    }

    public async Task GameCreatedAsync(Game game)
    {
        await using var _ = await _locks.AcquireAsync(game.Id);
        var session = new GameSession { Game = game };
        await ScheduleAsync(session, DeadlineKind.Join, 0, TimeSpan.FromSeconds(_options.JoinTimeoutSeconds));
        await _store.SaveAsync(session);
    }

    public async Task<GameStateDto> JoinAsync(Guid gameId, int userId, string connectionId, Func<Task> subscribe)
    {
        await using var _ = await _locks.AcquireAsync(gameId);
        var session = await LoadAsync(gameId);
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

        await _store.SaveAsync(session);
        return BuildState(session, player);
    }

    public async Task ReadyAsync(Guid gameId, int userId, int round)
    {
        await using var _ = await _locks.AcquireAsync(gameId);
        var session = await LoadAsync(gameId);
        PlayerOrThrow(session, userId);
        if (round < 1 || round > session.Game.Settings.Rounds)
            throw new GameRuleException($"Round must be between 1 and {session.Game.Settings.Rounds}.");
        if (round <= session.Game.CurrentRound)
            return; // that round has already started

        if (!session.Ready.TryGetValue(round, out var ready))
            session.Ready[round] = ready = [];
        ready.Add(userId);

        await StartRoundIfAllReadyLockedAsync(session);
        await _store.SaveAsync(session);
    }

    public async Task SubmitAnswerAsync(Guid gameId, int userId, int round, int slotIndex)
    {
        await using var _ = await _locks.AcquireAsync(gameId);
        var session = await LoadAsync(gameId);
        var player = PlayerOrThrow(session, userId);
        var active = session.Game.ActiveRound;
        if (active is null || active.Round != round)
            throw new GameRuleException("That round is not being played.");
        if (_time.GetUtcNow() > active.EndsAt.AddSeconds(_options.AnswerGraceSeconds))
            throw new GameRuleException("The time is up.");
        if (session.Answers.ContainsKey(userId))
            throw new GameRuleException("You have already answered.");
        if (slotIndex < 0 || slotIndex > player.Timeline.Count)
            throw new GameRuleException($"slotIndex must be between 0 and {player.Timeline.Count}.");

        var afterMs = AnsweredAfterMs(active, session.Game.Settings.AnswerTimeSeconds);
        session.Answers[userId] = new PlayerAnswer(slotIndex, afterMs, session.Answers.Count + 1);
        await SendAsync(() => _notifier.PlayerAnsweredAsync(gameId, new PlayerAnsweredDto(userId, afterMs)), gameId);

        if (ConnectedPlayers(session).All(session.Answers.ContainsKey))
            await EndRoundLockedAsync(session);
        await _store.SaveAsync(session);
    }

    public async Task DisconnectedAsync(string connectionId)
    {
        if (!_connections.TryRemove(connectionId, out var entry))
            return;

        await using var _ = await _locks.AcquireAsync(entry.GameId);
        if (await _store.GetAsync(entry.GameId) is not { } session)
            return;

        if (session.Connections.TryGetValue(entry.UserId, out var connections))
            connections.Remove(connectionId);
        // The player who left may have been the only one the server was waiting for.
        await StartRoundIfAllReadyLockedAsync(session);
        var connected = ConnectedPlayers(session);
        if (session.Game.ActiveRound is not null && connected.Count > 0 && connected.All(session.Answers.ContainsKey))
            await EndRoundLockedAsync(session);
        await _store.SaveAsync(session);
    }

    public async Task PrepareRoundAsync(Guid gameId, int round)
    {
        await using var _ = await _locks.AcquireAsync(gameId);
        var session = await LoadAsync(gameId);
        await PrepareRoundLockedAsync(session, round);
        await _store.SaveAsync(session);
    }

    // A deadline came due (in this pod or, with Redis, claimed by this pod). It is ignored if the game
    // has moved on and replaced it.
    private async Task OnDeadlineAsync(Guid gameId, string token)
    {
        try
        {
            await using var _ = await _locks.AcquireAsync(gameId);
            if (await _store.GetAsync(gameId) is not { } session || session.Deadline?.Token != token)
                return;

            var deadline = session.Deadline;
            session.Deadline = null;
            var game = session.Game;
            switch (deadline.Kind)
            {
                case DeadlineKind.Join when game.Status == GameStatus.Waiting:
                    _logger.LogInformation("Game {GameId} starts after the join timeout with {Joined}/{Total} players",
                        gameId, session.Joined.Count, game.Players.Count);
                    await StartGameLockedAsync(session);
                    break;
                case DeadlineKind.Ready when session.PreparingRound == deadline.Round:
                    await StartRoundLockedAsync(session);
                    break;
                case DeadlineKind.Round when game.ActiveRound?.Round == deadline.Round:
                    await EndRoundLockedAsync(session);
                    break;
                case DeadlineKind.Pause when deadline.Round < game.Settings.Rounds:
                    await PrepareRoundLockedAsync(session, deadline.Round + 1);
                    break;
                case DeadlineKind.Pause:
                    await FinishGameLockedAsync(session);
                    break;
            }
            await _store.SaveAsync(session);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "A deadline for game {GameId} failed", gameId);
        }
    }

    private async Task StartGameLockedAsync(GameSession session)
    {
        var game = session.Game;
        if (game.Status != GameStatus.Waiting) return;

        session.Deadline = null;
        game.Status = GameStatus.Playing;
        game.StartedAt = _time.GetUtcNow().UtcDateTime;

        var files = PrefetchFor(game, RoundTiming.Rounds(1, _options.PrefetchWindow, game.Settings.Rounds));
        await SendAsync(() => _notifier.PrefetchAsync(game.Id, files), game.Id);
        await PrepareRoundLockedAsync(session, 1);
    }

    private async Task PrepareRoundLockedAsync(GameSession session, int round)
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

        await ScheduleAsync(session, DeadlineKind.Ready, round, TimeSpan.FromSeconds(_options.ReadyTimeoutSeconds));
    }

    private async Task StartRoundIfAllReadyLockedAsync(GameSession session)
    {
        if (session.PreparingRound > 0 && AllConnectedReady(session, session.PreparingRound))
            await StartRoundLockedAsync(session);
    }

    private async Task StartRoundLockedAsync(GameSession session)
    {
        var game = session.Game;
        var round = session.PreparingRound;
        if (round == 0) return;

        session.PreparingRound = 0;
        session.Deadline = null;

        var answerTime = game.Settings.AnswerTimeSeconds;
        var offset = RoundTiming.PickStartOffset(_random, game.SongForRound(round).DurationSeconds,
            RoundTiming.PlaySeconds(answerTime, _options.AnswerGraceSeconds, _options.PauseBetweenRoundsSeconds));
        var playbackStartsAt = _time.GetUtcNow().AddMilliseconds(_options.StartDelayMs);

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
        await ScheduleAsync(session, DeadlineKind.Round, round,
            TimeSpan.FromMilliseconds(_options.StartDelayMs) + TimeSpan.FromSeconds(answerTime + _options.AnswerGraceSeconds));

        var started = new RoundStartedDto(round, game.Settings.Rounds, _options.StartDelayMs, offset, answerTime, playbackStartsAt);
        await SendAsync(() => _notifier.RoundStartedAsync(game.Id, started), game.Id);

        var next = round + _options.PrefetchWindow;
        if (next <= game.Settings.Rounds)
        {
            var files = PrefetchFor(game, [next]);
            await SendAsync(() => _notifier.PrefetchAsync(game.Id, files), game.Id);
        }
    }

    // Judges every answer, updates scores and timelines, sends RoundEnded and schedules the next round.
    private async Task EndRoundLockedAsync(GameSession session)
    {
        var game = session.Game;
        var active = game.ActiveRound;
        if (active is null) return;

        game.ActiveRound = null;
        session.Deadline = null;
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
            {
                player.Answered++;
                _metrics?.Answer(correct);
            }
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
        _metrics?.RoundPlayed();

        var ended = new RoundEndedDto(active.Round, ToCard(song), results);
        await SendAsync(() => _notifier.RoundEndedAsync(game.Id, ended), game.Id);

        // SCRUM-170: the last round's reveal gets the same pause as the others before GameEnded.
        // Meanwhile nothing can change the game: there is no active round and no round left to prepare.
        await ScheduleAsync(session, DeadlineKind.Pause, active.Round, TimeSpan.FromSeconds(_options.PauseBetweenRoundsSeconds));
    }

    // SCRUM-104: the last round is over. Players get GameEnded even if publishing to RabbitMQ fails.
    private async Task FinishGameLockedAsync(GameSession session)
    {
        var game = session.Game;
        if (game.Status == GameStatus.Finished) return;

        game.Status = GameStatus.Finished;
        game.EndedAt = _time.GetUtcNow().UtcDateTime;
        _metrics?.GameFinished();

        // SCRUM-151: the bonus awards are added to the normal score before placements are worked out.
        var bonus = BonusCalculator.Calculate(
            game.Players.Select(p => new BonusCalculator.PlayerRounds(p.UserId, p.History)).ToList(),
            game.Settings.Rounds);
        foreach (var player in game.Players)
            player.Score += bonus.BonusByUser[player.UserId];

        // Highest score first; ties in the order the players were in the lobby.
        var players = game.Players.OrderByDescending(p => p.Score).ToList();
        var placements = PlacementCalculator.Calculate(players.Select(p => p.Score).ToList());

        var standings = players.Select((p, i) =>
            new StandingDto(p.UserId, p.UserName, p.Score, p.Correct, p.Answered, placements[i], Cards(p))).ToList();
        var songs = Enumerable.Range(1, game.Settings.Rounds)
            .Select(r => new PastSongDto(r, ToCard(game.SongForRound(r))))
            .ToList();
        var gameEnded = new GameEndedDto(game.Id, standings, bonus.Awards, songs);
        await SendAsync(() => _notifier.GameEndedAsync(game.Id, gameEnded), game.Id);

        // Same message and field names as SCRUM-83/84 plus lobbyId (v1.1, SCRUM-172). StatisticService saves
        // the result, LobbyService reopens the lobby.
        var finished = new GameFinishedEvent
        {
            GameId = game.Id,
            LobbyId = game.LobbyId, // SCRUM-172: LobbyService reopens this lobby
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
            await _resultPublisher.PublishGameFinishedAsync(finished, timeout.Token);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Publishing GameFinished for game {GameId} failed; the result is not in the statistics", game.Id);
        }
    }

    // Replaces the game's deadline. The old one may still come due, but its token no longer matches.
    private async Task ScheduleAsync(GameSession session, DeadlineKind kind, int round, TimeSpan after)
    {
        var deadline = new GameDeadline(kind, round, _time.GetUtcNow() + after, Guid.NewGuid().ToString("N"));
        session.Deadline = deadline;
        await _deadlines.ScheduleAsync(session.Game.Id, deadline.Token, deadline.DueAt);
    }

    // Milliseconds from playback start to now, clamped to [0, answer time].
    private int AnsweredAfterMs(ActiveRound round, int answerTimeSeconds)
    {
        var ms = (_time.GetUtcNow() - round.PlaybackStartsAt).TotalMilliseconds;
        return (int)Math.Clamp(Math.Round(ms), 0, answerTimeSeconds * 1000);
    }

    // [from, to]: the years of the cards on either side of the slot; null for an open end.
    private static IReadOnlyList<int?> RangeOf(List<TimelineCard> timeline, int slot) =>
    [
        slot > 0 ? timeline[slot - 1].Year : null,
        slot < timeline.Count ? timeline[slot].Year : null
    ];

    private static List<int> ConnectedPlayers(GameSession session) =>
        session.Connections.Where(c => c.Value.Count > 0).Select(c => c.Key).ToList();

    private static CardDto ToCard(Song song) => new(song.Title, song.Artists, song.Year);

    private static List<CardDto> Cards(GamePlayer player) =>
        player.Timeline.OrderBy(c => c.Year).Select(c => ToCard(c.Song)).ToList();

    // True when at least one player is connected and every connected player is ready for the round.
    private static bool AllConnectedReady(GameSession session, int round)
    {
        var connected = ConnectedPlayers(session);
        return connected.Count > 0
               && session.Ready.TryGetValue(round, out var ready)
               && connected.All(ready.Contains);
    }

    private GameStateDto BuildState(GameSession session, GamePlayer me)
    {
        var game = session.Game;
        var now = _time.GetUtcNow();
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
                round.StartOffsetSeconds, game.Settings.AnswerTimeSeconds, round.PlaybackStartsAt);

        // The int fields can only move by whole seconds; PlaybackStartsAt moves by the same, so a client
        // with a server clock offset still gets the exact position (SCRUM-168).
        var played = (int)(now - round.PlaybackStartsAt).TotalSeconds;
        return new RoundStartedDto(round.Round, game.Settings.Rounds, 0,
            round.StartOffsetSeconds + played, game.Settings.AnswerTimeSeconds - played,
            round.PlaybackStartsAt.AddSeconds(played));
    }

    private static List<PrefetchDto> PrefetchFor(Game game, IEnumerable<int> rounds) =>
        rounds.Select(r => new PrefetchDto(r, game.SongForRound(r).AudioUrl)).ToList();

    private async Task<GameSession> LoadAsync(Guid gameId) =>
        await _store.GetAsync(gameId) ?? throw new GameAccessException("The game does not exist.");

    private static GamePlayer PlayerOrThrow(GameSession session, int userId) =>
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
            _logger.LogError(e, "Sending a message to game {GameId} failed", gameId);
        }
    }

    // Nothing to release: the games live in the store and the timers in the deadline queue.
    public void Dispose() { }
}
