using System.Collections.Concurrent;
using GameService.Domain.Models;
using GameService.Service.Interfaces;

namespace GameService.Service.Games;

public class InMemoryGameStore(TimeProvider timeProvider) : IGameStore
{
    public static readonly TimeSpan FinishedRetention = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, Game> _games = new();

    public void Add(Game game)
    {
        RemoveExpired();
        _games[game.Id] = game;
    }

    public Game? Get(Guid gameId) => _games.GetValueOrDefault(gameId);

    public int CountByStatus(GameStatus status) => _games.Values.Count(g => g.Status == status);

    public int RemoveExpired()
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - FinishedRetention;
        var removed = 0;
        foreach (var (id, game) in _games)
        {
            if (game.Status == GameStatus.Finished && game.EndedAt is { } ended && ended <= cutoff
                && _games.TryRemove(id, out _))
                removed++;
        }
        return removed;
    }
}
