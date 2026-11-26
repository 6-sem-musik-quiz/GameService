using System.Collections.Concurrent;
using GameService.Domain.Dtos;
using GameService.Domain.Events;
using GameService.Domain.Models;
using GameService.Repository.State;
using GameService.Service.Games;
using GameService.Service.Interfaces;
using GameService.Service.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using Testcontainers.Redis;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace GameService.Service.Tests.State;

// A real Redis in Docker (Testcontainers), one per test class.
public sealed class RedisFixture : IAsyncLifetime
{
    private readonly RedisContainer _container = new RedisBuilder().WithImage("redis:7.4-alpine").Build();
    public IConnectionMultiplexer Redis { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Redis = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString() + ",allowAdmin=true");
    }

    public async Task DisposeAsync()
    {
        await Redis.DisposeAsync();
        await _container.DisposeAsync();
    }

    public async Task FlushAsync()
    {
        foreach (var server in Redis.GetServers())
            await server.FlushAllDatabasesAsync();
    }
}

// ADR-0004: the Redis store, lock and deadline queue, and two GameService "pods" sharing one Redis.
public class RedisGameStateTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public Task InitializeAsync() => redis.FlushAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Store ----

    [Fact]
    public async Task Store_RoundTripsAWholeSession()
    {
        var store = new RedisGameSessionStore(redis.Redis, _time);
        var song = new Song { Id = 3, Title = "Take On Me", Artists = ["a-ha"], Year = 1985, DurationSeconds = 225, AudioUrl = "https://m/3.mp3" };
        var session = new GameSession
        {
            Game = new Game
            {
                Id = Guid.NewGuid(), LobbyId = Guid.NewGuid(), Status = GameStatus.Playing, CurrentRound = 2,
                Settings = new GameSettings { CategoryId = 7, CategoryName = "80s", Rounds = 10, AnswerTimeSeconds = 30 },
                Songs = [song, song],
                StartedAt = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc),
                ActiveRound = new ActiveRound { Round = 2, StartOffsetSeconds = 40, PlaybackStartsAt = _time.GetUtcNow(), EndsAt = _time.GetUtcNow().AddSeconds(30) },
                Players =
                [
                    new GamePlayer
                    {
                        UserId = 1, UserName = "asger", Score = 1, Correct = 1, Answered = 1,
                        Timeline = [new TimelineCard { Song = song }],
                        History = [new RoundHistory { Round = 1, Correct = true, AnsweredAfterMs = 3200 }]
                    }
                ]
            },
            Joined = [1, 2],
            Connections = new() { [1] = ["conn-a", "conn-b"] },
            Ready = new() { [3] = [1] },
            Answers = new() { [1] = new PlayerAnswer(1, 2500, 1) },
            PreparingRound = 0,
            Deadline = new GameDeadline(DeadlineKind.Round, 2, _time.GetUtcNow().AddSeconds(34), "tok")
        };

        await store.SaveAsync(session);
        var loaded = await store.GetAsync(session.Game.Id);

        Assert.NotNull(loaded);
        Assert.Equal(session.Game.Id, loaded.Game.Id);
        Assert.Equal((GameStatus.Playing, 2, 10), (loaded.Game.Status, loaded.Game.CurrentRound, loaded.Game.Settings.Rounds));
        Assert.Equal(session.Game.ActiveRound.EndsAt, loaded.Game.ActiveRound!.EndsAt);
        var player = Assert.Single(loaded.Game.Players);
        Assert.Equal(1985, Assert.Single(player.Timeline).Year);
        Assert.Equal(3200, Assert.Single(player.History).AnsweredAfterMs);
        Assert.Equal(["conn-a", "conn-b"], loaded.Connections[1].Order());
        Assert.Equal([1], loaded.Ready[3]);
        Assert.Equal(new PlayerAnswer(1, 2500, 1), loaded.Answers[1]);
        Assert.Equal(session.Deadline, loaded.Deadline);
    }

    [Fact]
    public async Task Store_CountsPlayingGames_AndKeepsFinishedOnes30Minutes()
    {
        var store = new RedisGameSessionStore(redis.Redis, _time);
        var a = new GameSession { Game = new Game { Id = Guid.NewGuid(), Status = GameStatus.Playing } };
        var b = new GameSession { Game = new Game { Id = Guid.NewGuid(), Status = GameStatus.Playing } };
        await store.SaveAsync(a);
        await store.SaveAsync(b);
        Assert.Equal(2, store.CountPlaying());

        b.Game.Status = GameStatus.Finished;
        await store.SaveAsync(b);

        Assert.Equal(1, store.CountPlaying());
        var ttl = await redis.Redis.GetDatabase().KeyTimeToLiveAsync(RedisKeys.Game(b.Game.Id));
        Assert.InRange(ttl!.Value, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
        Assert.Null(await store.GetAsync(Guid.NewGuid()));
    }

    // ---- Lock and deadlines across pods ----

    [Fact]
    public async Task Lock_IsExclusiveAcrossPods()
    {
        var podA = new RedisGameLocks(redis.Redis);
        var podB = new RedisGameLocks(redis.Redis);
        var game = Guid.NewGuid();

        var held = await podA.AcquireAsync(game);
        var waiting = podB.AcquireAsync(game);
        await Task.Delay(200);
        Assert.False(waiting.IsCompleted);

        await held.DisposeAsync();
        await (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
    }

    [Fact]
    public async Task Deadlines_AreHandledExactlyOnce_WhenSeveralPodsPoll()
    {
        var handled = new ConcurrentBag<string>();
        var pods = Enumerable.Range(0, 3).Select(_ =>
        {
            var queue = new RedisDeadlineQueue(redis.Redis, _time, NullLogger<RedisDeadlineQueue>.Instance);
            queue.SetHandler((_, token) => { handled.Add(token); return Task.CompletedTask; });
            return queue;
        }).ToList();
        for (var i = 0; i < 30; i++)
            await pods[0].ScheduleAsync(Guid.NewGuid(), $"t{i}", _time.GetUtcNow().AddSeconds(1));

        Assert.Equal(new PollResult(0, true), await pods[1].PollAsync()); // pending, not due yet
        _time.Advance(TimeSpan.FromSeconds(1));
        await Task.WhenAll(pods.Select(p => p.PollAsync()));

        Assert.Equal(30, handled.Count);
        Assert.Equal(30, handled.Distinct().Count());
    }

    [Fact]
    public async Task Poll_TellsWhetherAnyDeadlineIsLeft_SoIdlePodsPollLess()
    {
        var queue = new RedisDeadlineQueue(redis.Redis, _time, NullLogger<RedisDeadlineQueue>.Instance);
        var handled = 0;
        queue.SetHandler((_, _) => { handled++; return Task.CompletedTask; });

        var empty = await queue.PollAsync();
        Assert.Equal(new PollResult(0, false), empty);
        Assert.Equal(DeadlinePollingService.IdleInterval, DeadlinePollingService.NextDelay(empty));

        await queue.ScheduleAsync(Guid.NewGuid(), "a", _time.GetUtcNow().AddSeconds(1));
        await queue.ScheduleAsync(Guid.NewGuid(), "b", _time.GetUtcNow().AddSeconds(5));
        var waiting = await queue.PollAsync();
        Assert.Equal(new PollResult(0, true), waiting);
        Assert.Equal(DeadlinePollingService.BusyInterval, DeadlinePollingService.NextDelay(waiting));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new PollResult(1, true), await queue.PollAsync()); // "a" handled, "b" still pending
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(new PollResult(1, true), await queue.PollAsync()); // "b" handled
        Assert.Equal(new PollResult(0, false), await queue.PollAsync()); // nothing left: idle again
        Assert.Equal(2, handled);
    }

    // ---- Two GameService pods, one game ----

    private sealed class RecordingNotifier : IGameNotifier
    {
        public ConcurrentQueue<object> Messages { get; } = new();
        public Task PrefetchAsync(Guid gameId, IReadOnlyList<PrefetchDto> files) { Messages.Enqueue(files); return Task.CompletedTask; }
        public Task RoundStartedAsync(Guid gameId, RoundStartedDto round) { Messages.Enqueue(round); return Task.CompletedTask; }
        public Task PlayerAnsweredAsync(Guid gameId, PlayerAnsweredDto answered) { Messages.Enqueue(answered); return Task.CompletedTask; }
        public Task RoundEndedAsync(Guid gameId, RoundEndedDto round) { Messages.Enqueue(round); return Task.CompletedTask; }
        public Task GameEndedAsync(Guid gameId, GameEndedDto game) { Messages.Enqueue(game); return Task.CompletedTask; }
    }

    // One "pod": its own coordinator, notifier and deadline poller, sharing Redis with the others.
    private sealed record Pod(GameCoordinator Coordinator, RedisDeadlineQueue Deadlines, RecordingNotifier Notifier);

    private Pod NewPod(IResultPublisher publisher)
    {
        var deadlines = new RedisDeadlineQueue(redis.Redis, _time, NullLogger<RedisDeadlineQueue>.Instance);
        var notifier = new RecordingNotifier();
        var coordinator = new GameCoordinator(new RedisGameSessionStore(redis.Redis, _time), new RedisGameLocks(redis.Redis),
            deadlines, notifier, publisher, _time, MsOptions.Create(new GameOptions()), NullLogger<GameCoordinator>.Instance, new Random(1));
        return new Pod(coordinator, deadlines, notifier);
    }

    private static Game NewGame(int rounds) => new()
    {
        Id = Guid.NewGuid(),
        Settings = new GameSettings { CategoryId = 1, CategoryName = "80s", Rounds = rounds, AnswerTimeSeconds = 30 },
        Songs = Enumerable.Range(0, rounds + 1).Select(i => new Song
        {
            Id = i, Title = $"Song {i}", Artists = ["A"], Year = 1970 + i, DurationSeconds = 200, AudioUrl = $"https://m/{i}.mp3"
        }).ToList(),
        Players = [Player(1), Player(2)]
    };

    private static GamePlayer Player(int id) => new()
    {
        UserId = id, UserName = $"p{id}",
        Timeline = [new TimelineCard { Song = new Song { Id = 0, Title = "Song 0", Artists = ["A"], Year = 1970 } }]
    };

    // Time passes and every pod polls, like DeadlinePollingService does every 100 ms.
    private async Task Pass(TimeSpan time, params Pod[] pods)
    {
        _time.Advance(time);
        await Task.WhenAll(pods.Select(p => p.Deadlines.PollAsync()));
    }

    [Fact]
    public async Task TwoPods_PlayOneGame_WithPlayersAndCallsOnBothPods()
    {
        var publisher = Substitute.For<IResultPublisher>();
        var a = NewPod(publisher);
        var b = NewPod(publisher);
        var game = NewGame(rounds: 2);

        await a.Coordinator.GameCreatedAsync(game);
        await a.Coordinator.JoinAsync(game.Id, 1, "conn-1", () => Task.CompletedTask); // player 1 on pod A
        await b.Coordinator.JoinAsync(game.Id, 2, "conn-2", () => Task.CompletedTask); // player 2 on pod B: the game starts

        for (var round = 1; round <= 2; round++)
        {
            await a.Coordinator.ReadyAsync(game.Id, 1, round);
            await b.Coordinator.ReadyAsync(game.Id, 2, round); // the round starts on B
            await a.Coordinator.SubmitAnswerAsync(game.Id, 1, round, round); // after the last card: right
            await b.Coordinator.SubmitAnswerAsync(game.Id, 2, round, 0);     // before the first: wrong; round ends
            await Pass(TimeSpan.FromSeconds(8), a, b); // the pause: one of the pods prepares the next round / ends the game
        }

        var all = a.Notifier.Messages.Concat(b.Notifier.Messages).ToList();
        Assert.Equal(2, all.OfType<RoundStartedDto>().Count());
        Assert.Equal(2, all.OfType<RoundEndedDto>().Count());
        var ended = Assert.Single(all.OfType<GameEndedDto>());
        Assert.Equal([(1, 2 + 2), (2, 0)], ended.Standings.Select(s => (s.UserId, s.Score))); // 2 right + both awards
        await publisher.Received(1).PublishGameFinishedAsync(Arg.Is<GameFinishedEvent>(e => e.GameId == game.Id), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task APodCanDie_AndAnotherPodFinishesTheGame()
    {
        var publisher = Substitute.For<IResultPublisher>();
        var a = NewPod(publisher);
        var game = NewGame(rounds: 1);
        await a.Coordinator.GameCreatedAsync(game);
        await a.Coordinator.JoinAsync(game.Id, 1, "conn-1", () => Task.CompletedTask);
        await a.Coordinator.JoinAsync(game.Id, 2, "conn-2", () => Task.CompletedTask);
        await a.Coordinator.ReadyAsync(game.Id, 1, 1);
        await a.Coordinator.ReadyAsync(game.Id, 2, 1); // round 1 runs on pod A ...

        // ... and pod A dies. Pod B never saw this game; the players reconnect to it.
        var b = NewPod(publisher);
        var state = await b.Coordinator.JoinAsync(game.Id, 1, "conn-1b", () => Task.CompletedTask);
        Assert.Equal(1, state.Round);
        Assert.NotNull(state.CurrentRound);

        await b.Coordinator.SubmitAnswerAsync(game.Id, 1, 1, 1);
        await Pass(TimeSpan.FromSeconds(3 + 30 + 1), b); // the answer time runs out: B's poller ends the round
        await Pass(TimeSpan.FromSeconds(8), b);          // the pause: B finishes the game

        Assert.Single(b.Notifier.Messages.OfType<RoundEndedDto>());
        Assert.Single(b.Notifier.Messages.OfType<GameEndedDto>());
        await publisher.Received(1).PublishGameFinishedAsync(Arg.Any<GameFinishedEvent>(), Arg.Any<CancellationToken>());
    }
}
