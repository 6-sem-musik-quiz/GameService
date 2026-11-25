using GameService.Domain.Dtos;
using GameService.Service.Exceptions;
using GameService.Service.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace GameService.Api.Hubs;

// Messages the server sends to the players in a game (group "game:{id}").
public interface IGameClient
{
    Task Prefetch(IReadOnlyList<PrefetchDto> files);
    Task RoundStarted(RoundStartedDto round);
    Task PlayerAnswered(PlayerAnsweredDto answered);
    Task RoundEnded(RoundEndedDto round);
    Task GameEnded(GameEndedDto game);
}

// SCRUM-102/103. SignalR hub at /hubs/game, and at /api/hubs/game where the edge forwards
// /api/gameservice/hubs/game. Requires a JWT; browsers send it as ?access_token=.
// The game logic lives in IGameCoordinator; this class only maps the user and errors.
[Authorize]
public class GameHub(IGameCoordinator coordinator, TimeProvider time) : Hub<IGameClient>
{
    public const string Path = "/hubs/game";
    public const string EdgePath = "/api/hubs/game";

    public static string GroupName(Guid gameId) => $"game:{gameId}";

    // Joins the game (again after a reconnect) and returns its state for this player.
    public Task<GameStateDto> JoinGame(Guid gameId) =>
        Run(() => coordinator.JoinAsync(gameId, UserId(), Context.ConnectionId,
            () => Groups.AddToGroupAsync(Context.ConnectionId, GroupName(gameId))));

    // SCRUM-168: the server's clock, so the client can work out its offset and play at
    // RoundStarted.PlaybackStartsAt together with everyone else. Changes no state.
    public DateTimeOffset ServerTime() => time.GetUtcNow();

    // The client has downloaded the file for the round.
    public async Task Ready(Guid gameId, int round)
    {
        var userId = UserId();
        await Run(async () =>
        {
            await coordinator.ReadyAsync(gameId, userId, round);
            return 0;
        });
    }

    // SCRUM-103: places the round's song at slotIndex in the caller's timeline.
    public async Task SubmitAnswer(Guid gameId, int round, int slotIndex)
    {
        var userId = UserId();
        await Run(async () =>
        {
            await coordinator.SubmitAnswerAsync(gameId, userId, round, slotIndex);
            return 0;
        });
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await coordinator.DisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private int UserId() =>
        int.TryParse(Context.User?.FindFirst("sub")?.Value, out var userId)
            ? userId
            : throw new HubException("The token has no valid user id.");

    // Only these messages reach the client; anything else becomes SignalR's generic error.
    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (e is GameAccessException or GameRuleException)
        {
            throw new HubException(e.Message);
        }
    }
}

// IGameNotifier over SignalR: sends to the game's group.
public class SignalRGameNotifier(IHubContext<GameHub, IGameClient> hub) : IGameNotifier
{
    public Task PrefetchAsync(Guid gameId, IReadOnlyList<PrefetchDto> files) =>
        hub.Clients.Group(GameHub.GroupName(gameId)).Prefetch(files);

    public Task RoundStartedAsync(Guid gameId, RoundStartedDto round) =>
        hub.Clients.Group(GameHub.GroupName(gameId)).RoundStarted(round);

    public Task PlayerAnsweredAsync(Guid gameId, PlayerAnsweredDto answered) =>
        hub.Clients.Group(GameHub.GroupName(gameId)).PlayerAnswered(answered);

    public Task RoundEndedAsync(Guid gameId, RoundEndedDto round) =>
        hub.Clients.Group(GameHub.GroupName(gameId)).RoundEnded(round);

    public Task GameEndedAsync(Guid gameId, GameEndedDto game) =>
        hub.Clients.Group(GameHub.GroupName(gameId)).GameEnded(game);
}
