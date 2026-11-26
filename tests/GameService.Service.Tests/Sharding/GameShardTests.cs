using GameService.Domain.Models;
using GameService.Service.Games;
using GameService.Service.Sharding;
using Microsoft.Extensions.Time.Testing;

namespace GameService.Service.Tests.Sharding;

// ADR-0004 / gameservice.gamehub@1.4: the first two hex characters of the gameId are the owning pod.
public class GameShardTests
{
    [Theory]
    [InlineData("00a7f3c1-5b2e-4d0a-9c1f-7e3b2a1d4c5e", 0)]
    [InlineData("02a7f3c1-5b2e-4d0a-9c1f-7e3b2a1d4c5e", 2)]
    [InlineData("1Fa7f3c1-5b2e-4d0a-9c1f-7e3b2a1d4c5e", 31)] // upper case reads the same
    [InlineData("ffa7f3c1-5b2e-4d0a-9c1f-7e3b2a1d4c5e", 255)]
    public void Of_ReadsTheFirstTwoHexCharacters(string gameId, int expected) =>
        Assert.Equal(expected, GameShard.Of(Guid.Parse(gameId)));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(255)]
    public void NewGameId_StartsWithTheShard_AndIsOtherwiseRandom(int shard)
    {
        var ids = Enumerable.Range(0, 200).Select(_ => GameShard.NewGameId(shard)).ToList();

        Assert.All(ids, id => Assert.Equal(shard, GameShard.Of(id)));
        Assert.All(ids, id => Assert.StartsWith(shard.ToString("x2"), id.ToString("D")));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Equal('4', id.ToString("D")[14])); // still a version-4 GUID
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void NewGameId_OutsideTwoHexCharacters_Throws(int shard) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => GameShard.NewGameId(shard));

    [Theory]
    [InlineData("gameservice-0", 0)]
    [InlineData("gameservice-2", 2)]
    [InlineData("gameservice-12", 12)]
    [InlineData(null, 0)]
    [InlineData("my-laptop", 0)]                    // local run
    [InlineData("gameservice-7b5d9d5d56-j5p9n", 0)] // a Deployment pod: one pod, shard 0
    public void ShardIndex_ComesFromThePodName(string? hostName, int expected) =>
        Assert.Equal(expected, new ShardedGameIds(new ShardingOptions(), hostName).ShardIndex);

    [Fact]
    public void ShardIndex_FromConfiguration_WinsOverThePodName() =>
        Assert.Equal(1, new ShardedGameIds(new ShardingOptions { ShardIndex = 1 }, "gameservice-2").ShardIndex);

    [Theory]
    [InlineData("gameservice-256", null)]
    [InlineData(null, -1)]
    [InlineData(null, 300)]
    public void ShardIndex_OutsideTwoHexCharacters_FailsAtStartup(string? hostName, int? index) =>
        Assert.Throws<InvalidOperationException>(() =>
            new ShardedGameIds(new ShardingOptions { ShardIndex = index }, hostName));

    [Fact]
    public void NewGameId_IsOwnedByThisPod()
    {
        var ids = new ShardedGameIds(new ShardingOptions(), "gameservice-3");

        var id = ids.NewGameId();

        Assert.Equal(3, GameShard.Of(id));
        Assert.True(ids.Owns(id));
        Assert.False(ids.Owns(GameShard.NewGameId(4)));
    }
}

// ADR-0004: the preStop drain waits until no game is Waiting or Playing.
public class GameDrainTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly InMemoryGameStore _store;
    private readonly GameDrain _drain;

    public GameDrainTests()
    {
        _store = new InMemoryGameStore(_time);
        _drain = new GameDrain(_store, _time);
    }

    private Game Add(GameStatus status)
    {
        var game = new Game { Id = Guid.NewGuid(), Status = status, EndedAt = status == GameStatus.Finished ? _time.GetUtcNow().UtcDateTime : null };
        _store.Add(game);
        return game;
    }

    [Fact]
    public async Task Drain_WithNoActiveGames_ReturnsAtOnce_AndMarksThePodDraining()
    {
        Add(GameStatus.Finished);

        await _drain.DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.True(_drain.IsDraining);
        Assert.Equal(0, _drain.ActiveGames);
    }

    [Fact]
    public async Task Drain_WaitsUntilWaitingAndPlayingGamesHaveFinished()
    {
        var playing = Add(GameStatus.Playing);
        var waiting = Add(GameStatus.Waiting);

        var drained = _drain.DrainAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.True(_drain.IsDraining); // not ready from the first moment
        Assert.Equal(2, _drain.ActiveGames);

        playing.Status = GameStatus.Finished;
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(drained.IsCompleted); // the waiting game is still active

        waiting.Status = GameStatus.Finished;
        _time.Advance(TimeSpan.FromSeconds(2));
        await drained.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Drain_StopsWaiting_WhenTheCallIsCancelled()
    {
        Add(GameStatus.Playing);
        using var cancel = new CancellationTokenSource();

        var drained = _drain.DrainAsync(TimeSpan.FromSeconds(2), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drained);
    }
}
