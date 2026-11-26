using GameService.Domain.Models;

namespace GameService.Repository.State;

// ADR-0004: GameService keeps its games outside the pod, so any pod can serve any game and a pod can
// die or be replaced without ending the games. Redis in the cluster; in memory for tests and local runs.

public interface IGameSessionStore
{
    // Null if the game does not exist (never created, or finished more than 30 minutes ago).
    Task<GameSession?> GetAsync(Guid gameId);

    // Saves the whole session. A Finished game is kept 30 minutes, so players can still rejoin it.
    Task SaveAsync(GameSession session);

    // SCRUM-119: games with Status = Playing, in all pods (for the slopster_games_active gauge).
    long CountPlaying();
}

// One lock per game: only one pod at a time changes a game, and messages go out in the order the
// state changed. Dispose the result to release it.
public interface IGameLocks
{
    Task<IAsyncDisposable> AcquireAsync(Guid gameId);
}

// The game's timers as data: when a deadline comes due, one pod (whichever claims it first) calls the
// handler with the game and the deadline's token.
public interface IDeadlineQueue
{
    void SetHandler(Func<Guid, string, Task> handler);

    Task ScheduleAsync(Guid gameId, string token, DateTimeOffset dueAt);
}
