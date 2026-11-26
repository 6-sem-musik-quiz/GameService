using System.Diagnostics;
using GameService.Domain.Models;
using GameService.Repository.State;
using GameService.Service.Games;
using GameService.Service.Interfaces;
using GameService.Service.Options;
using GameService.Service.Tracing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace GameService.Service.Tests.Tracing;

// SCRUM-173: a game is one trace. The timers (deadlines) run as spans in the trace of the request that
// created the game, also when they run long after it (and, with Redis, on another pod).
public class GameTracingTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryDeadlineQueue _deadlines;
    private readonly GameCoordinator _coordinator;
    private readonly ActivityListener _listener;
    private readonly List<Activity> _spans = [];

    public GameTracingTests()
    {
        _deadlines = new InMemoryDeadlineQueue(_time);
        _coordinator = new GameCoordinator(new InMemoryGameSessionStore(_time), new InMemoryGameLocks(), _deadlines,
            Substitute.For<IGameNotifier>(), Substitute.For<IResultPublisher>(), _time,
            MsOptions.Create(new GameOptions()), NullLogger<GameCoordinator>.Instance, new Random(1));
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name is GameTracing.SourceName or "test",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (_spans) _spans.Add(a); }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _coordinator.Dispose();
        _deadlines.Dispose();
    }

    private static Game NewGame(int rounds) => new()
    {
        Id = Guid.NewGuid(),
        Settings = new GameSettings { CategoryId = 1, CategoryName = "80s", Rounds = rounds, AnswerTimeSeconds = 30 },
        Songs = Enumerable.Range(0, rounds + 1).Select(i => new Song
        {
            Id = i, Title = $"Song {i}", Artists = ["A"], Year = 1970 + i, DurationSeconds = 200, AudioUrl = $"https://m/{i}.mp3"
        }).ToList(),
        Players = [new GamePlayer { UserId = 1, UserName = "p1" }, new GamePlayer { UserId = 2, UserName = "p2" }]
    };

    private IEnumerable<Activity> Spans(Game game)
    {
        lock (_spans)
            return _spans.Where(s => s.Source.Name == GameTracing.SourceName
                                     && (string?)s.GetTagItem("game.id") == game.Id.ToString()).ToList();
    }

    [Fact]
    public async Task TheGamesTimers_AreSpansInTheTraceThatCreatedTheGame()
    {
        var game = NewGame(rounds: 1);
        ActivityTraceId createdIn;
        using (var request = new ActivitySource("test").StartActivity("POST /api/games")) // LobbyService's call
        {
            createdIn = request!.TraceId;
            await _coordinator.GameCreatedAsync(game);
        }

        // Outside the request: nobody joins, so the join timeout starts the game, then the ready timeout
        // starts round 1, the answer time ends it, and the pause finishes the game.
        _time.Advance(TimeSpan.FromSeconds(15));
        _time.Advance(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromSeconds(3 + 30 + 1));
        _time.Advance(TimeSpan.FromSeconds(8));

        Assert.Equal(GameStatus.Finished, game.Status);
        // The listener is global: other tests' games run at the same time, so only this game's spans.
        var timers = Spans(game).ToList();
        Assert.Equal(["game.join", "game.ready", "game.round", "game.pause"], timers.Select(s => s.OperationName));
        Assert.All(timers, s => Assert.Equal(createdIn, s.TraceId));
    }

    [Fact]
    public async Task AGameCreatedWithoutTracing_GetsItsOwnTraces()
    {
        var game = NewGame(rounds: 1);
        await _coordinator.GameCreatedAsync(game); // no request span

        _time.Advance(TimeSpan.FromSeconds(15));

        var join = Assert.Single(Spans(game), s => s.OperationName == "game.join");
        Assert.Null(join.ParentId);
    }
}
