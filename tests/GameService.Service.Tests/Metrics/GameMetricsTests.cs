using System.Diagnostics.Metrics;
using GameService.Domain.Models;
using GameService.Service.Games;
using GameService.Service.Interfaces;
using GameService.Service.Metrics;
using GameService.Service.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace GameService.Service.Tests.Metrics;

// SCRUM-119: the instruments of meter "Slopster.Game", read with a MeterListener.
public class GameMetricsTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryGameStore _store;
    private readonly GameMetrics _metrics;
    private readonly GameCoordinator _coordinator;
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, long Value, string? Result)> _measurements = [];

    public GameMetricsTests()
    {
        _store = new InMemoryGameStore(_time);
        _metrics = new GameMetrics(_store);
        _coordinator = new GameCoordinator(_store, Substitute.For<IGameNotifier>(), Substitute.For<IResultPublisher>(),
            _time, MsOptions.Create(new GameOptions()), NullLogger<GameCoordinator>.Instance, new Random(1), _metrics);

        // Only this test's meter: other tests may create their own "Slopster.Game" meters in parallel.
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == _metrics.Meter)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            string? result = null;
            foreach (var tag in tags)
                if (tag.Key == "result")
                    result = tag.Value as string;
            lock (_measurements)
                _measurements.Add((instrument.Name, value, result));
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _coordinator.Dispose();
        _metrics.Dispose();
    }

    private long Sum(string name, string? result = null) =>
        _measurements.Where(m => m.Name == name && (result is null || m.Result == result)).Sum(m => m.Value);

    private long ActiveGames()
    {
        _measurements.RemoveAll(m => m.Name == GameMetrics.GamesActiveName);
        _listener.RecordObservableInstruments();
        return _measurements.Single(m => m.Name == GameMetrics.GamesActiveName).Value;
    }

    private Game CreateGame(int rounds)
    {
        var game = new Game
        {
            Id = Guid.NewGuid(),
            Settings = new GameSettings { CategoryId = 1, CategoryName = "80s", Rounds = rounds, AnswerTimeSeconds = 30 },
            Songs = Enumerable.Range(0, rounds + 1).Select(i => new Song
            {
                Id = i, Title = $"Song {i}", Artists = ["A"], Year = 1970 + i, DurationSeconds = 200, AudioUrl = $"https://m/{i}.mp3"
            }).ToList(),
            Players = [new GamePlayer { UserId = 1, UserName = "p1" }, new GamePlayer { UserId = 2, UserName = "p2" }]
        };
        foreach (var player in game.Players)
            player.Timeline.Add(new TimelineCard { Song = game.Songs[0] });
        _store.Add(game);
        _coordinator.GameCreated(game);
        return game;
    }

    private async Task Join(Game game)
    {
        foreach (var player in game.Players)
            await _coordinator.JoinAsync(game.Id, player.UserId, $"conn-{game.Id}-{player.UserId}", () => Task.CompletedTask);
    }

    // Player 1 is always right (after the last card, round r plays year 1970 + r); player 2 always wrong.
    private async Task PlayRound(Game game, int round)
    {
        foreach (var player in game.Players)
            await _coordinator.ReadyAsync(game.Id, player.UserId, round);
        await _coordinator.SubmitAnswerAsync(game.Id, 1, round, game.Players[0].Timeline.Count);
        await _coordinator.SubmitAnswerAsync(game.Id, 2, round, 0);
        _time.Advance(TimeSpan.FromSeconds(8)); // the pause before the next round, or before the game ends
    }

    [Fact]
    public async Task Counters_CountRoundsAnswersAndFinishedGames()
    {
        var game = CreateGame(rounds: 3);
        await Join(game);

        for (var round = 1; round <= 3; round++)
            await PlayRound(game, round);

        Assert.Equal(GameStatus.Finished, game.Status);
        Assert.Equal(3, Sum(GameMetrics.RoundsPlayedName));
        Assert.Equal(3, Sum(GameMetrics.AnswersName, "correct"));
        Assert.Equal(3, Sum(GameMetrics.AnswersName, "wrong"));
        Assert.Equal(1, Sum(GameMetrics.GamesFinishedName));
    }

    [Fact]
    public async Task NoAnswer_IsNotCounted_ButTheRoundIs()
    {
        var game = CreateGame(rounds: 2);
        await Join(game);
        foreach (var player in game.Players)
            await _coordinator.ReadyAsync(game.Id, player.UserId, 1);

        _time.Advance(TimeSpan.FromSeconds(3 + 30 + 1)); // start delay + answer time + grace: nobody answered

        Assert.Equal(1, Sum(GameMetrics.RoundsPlayedName));
        Assert.Equal(0, Sum(GameMetrics.AnswersName));
        Assert.Equal(0, Sum(GameMetrics.GamesFinishedName));
    }

    [Fact]
    public async Task ActiveGames_CountsOnlyGamesBeingPlayed()
    {
        Assert.Equal(0, ActiveGames());

        var waiting = CreateGame(rounds: 1);
        var playing = CreateGame(rounds: 1);
        await Join(playing);
        Assert.Equal(GameStatus.Waiting, waiting.Status);
        Assert.Equal(1, ActiveGames());

        await Join(waiting);
        Assert.Equal(2, ActiveGames());

        await PlayRound(playing, 1);
        Assert.Equal(GameStatus.Finished, playing.Status);
        Assert.Equal(1, ActiveGames());
    }
}
