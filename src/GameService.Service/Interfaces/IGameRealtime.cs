using GameService.Domain.Dtos;
using GameService.Domain.Models;

namespace GameService.Service.Interfaces;

// Sends messages to everyone in a game. Implemented in the Api project with SignalR, so the
// service layer does not depend on it.
public interface IGameNotifier
{
    Task PrefetchAsync(Guid gameId, IReadOnlyList<PrefetchDto> files);
    Task RoundStartedAsync(Guid gameId, RoundStartedDto round);
}

// Runs a game in real time: who is connected, when the game and each round start (SCRUM-102).
// One GameService instance is assumed (see README); the state is in memory.
public interface IGameCoordinator
{
    // Called by GameEngine right after a game is created. Starts the join timeout.
    void GameCreated(Game game);

    // A player's connection joined the game. `subscribe` adds the connection to the game's group;
    // it runs after the membership check and before the game can start, so the connection gets
    // every message from then on. Starts the game when every player has joined.
    // Throws GameAccessException if the game does not exist or the user is not a player in it.
    Task<GameStateDto> JoinAsync(Guid gameId, int userId, string connectionId, Func<Task> subscribe);

    // The player has downloaded the file for the round.
    // Throws GameAccessException (not a player) or GameRuleException (round outside 1..rounds).
    Task ReadyAsync(Guid gameId, int userId, int round);

    // A connection closed. The player stays in the game but no longer counts as connected
    // when the server waits for Ready.
    Task DisconnectedAsync(string connectionId);

    // Waits for Ready for the round (or the ready timeout) and then starts it. The first round is
    // prepared when the game starts; SCRUM-103 calls this for the next round after RoundEnded.
    Task PrepareRoundAsync(Guid gameId, int round);
}
