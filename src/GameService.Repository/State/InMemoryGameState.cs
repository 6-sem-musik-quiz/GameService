using System.Collections.Concurrent;
using GameService.Domain.Models;

namespace GameService.Repository.State;

// In-memory versions: one process only (tests and local runs without Redis). The store keeps the
// objects themselves, so a test can look at the Game it created.

public sealed class InMemoryGameSessionStore(TimeProvider time) : IGameSessionStore
{
    public static readonly TimeSpan FinishedRetention = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, GameSession> _sessions = new();

    public Task<GameSession?> GetAsync(Guid gameId) => Task.FromResult(_sessions.GetValueOrDefault(gameId));

    public Task SaveAsync(GameSession session)
    {
        RemoveExpired();
        _sessions[session.Game.Id] = session;
        return Task.CompletedTask;
    }

    public long CountPlaying() => _sessions.Values.Count(s => s.Game.Status == GameStatus.Playing);

    private void RemoveExpired()
    {
        var cutoff = time.GetUtcNow().UtcDateTime - FinishedRetention;
        foreach (var (id, session) in _sessions)
            if (session.Game.Status == GameStatus.Finished && session.Game.EndedAt is { } ended && ended <= cutoff)
                _sessions.TryRemove(id, out _);
    }
}

public sealed class InMemoryGameLocks : IGameLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IAsyncDisposable> AcquireAsync(Guid gameId)
    {
        var semaphore = _locks.GetOrAdd(gameId, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}

// A TimeProvider timer per deadline, so tests drive time with FakeTimeProvider.
public sealed class InMemoryDeadlineQueue(TimeProvider time) : IDeadlineQueue, IDisposable
{
    private readonly ConcurrentDictionary<string, ITimer> _timers = new();
    private Func<Guid, string, Task>? _handler;

    public void SetHandler(Func<Guid, string, Task> handler) => _handler = handler;

    public Task ScheduleAsync(Guid gameId, string token, DateTimeOffset dueAt)
    {
        var delay = dueAt - time.GetUtcNow();
        _timers[token] = time.CreateTimer(_ =>
        {
            if (_timers.TryRemove(token, out var timer)) timer.Dispose();
            if (_handler is { } handler) _ = handler(gameId, token);
        }, null, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var timer in _timers.Values) timer.Dispose();
        _timers.Clear();
    }
}
