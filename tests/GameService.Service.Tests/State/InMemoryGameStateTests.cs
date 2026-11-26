using GameService.Domain.Models;
using GameService.Repository.State;
using Microsoft.Extensions.Time.Testing;

namespace GameService.Service.Tests.State;

// ADR-0004: the in-memory store, locks and deadlines (one process; tests and local runs).
public class InMemoryGameStateTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));

    private static GameSession Session(GameStatus status, DateTime? endedAt = null) =>
        new() { Game = new Game { Id = Guid.NewGuid(), Status = status, EndedAt = endedAt } };

    [Fact]
    public async Task Store_ForgetsFinishedGames30MinutesAfterTheyEnded_AndKeepsTheOthers()
    {
        var store = new InMemoryGameSessionStore(_time);
        var finished = Session(GameStatus.Finished, _time.GetUtcNow().UtcDateTime);
        var playing = Session(GameStatus.Playing);
        await store.SaveAsync(finished);
        await store.SaveAsync(playing);

        _time.Advance(TimeSpan.FromMinutes(29));
        await store.SaveAsync(Session(GameStatus.Waiting));
        Assert.NotNull(await store.GetAsync(finished.Game.Id));

        _time.Advance(TimeSpan.FromMinutes(2));
        await store.SaveAsync(Session(GameStatus.Waiting));
        Assert.Null(await store.GetAsync(finished.Game.Id));
        Assert.NotNull(await store.GetAsync(playing.Game.Id));
        Assert.Equal(1, store.CountPlaying());
    }

    [Fact]
    public async Task Lock_IsExclusivePerGame_AndOtherGamesAreNotBlocked()
    {
        var locks = new InMemoryGameLocks();
        var game = Guid.NewGuid();

        var first = await locks.AcquireAsync(game);
        var second = locks.AcquireAsync(game);
        var otherGame = locks.AcquireAsync(Guid.NewGuid());

        Assert.True(otherGame.IsCompletedSuccessfully);
        Assert.False(second.IsCompleted);
        await first.DisposeAsync();
        await (await second.WaitAsync(TimeSpan.FromSeconds(5))).DisposeAsync();
    }

    [Fact]
    public async Task Deadline_CallsTheHandler_WhenDue()
    {
        using var queue = new InMemoryDeadlineQueue(_time);
        var fired = new List<(Guid, string)>();
        queue.SetHandler((game, token) => { fired.Add((game, token)); return Task.CompletedTask; });
        var gameId = Guid.NewGuid();

        await queue.ScheduleAsync(gameId, "t1", _time.GetUtcNow().AddSeconds(10));
        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(fired);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([(gameId, "t1")], fired);
    }
}
