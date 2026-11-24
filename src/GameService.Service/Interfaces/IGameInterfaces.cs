using GameService.Domain.Dtos;
using GameService.Domain.Models;

namespace GameService.Service.Interfaces;

public interface IGameStore
{
    void Add(Game game);
    Game? Get(Guid gameId);
    // Removes Finished games that ended more than 30 minutes ago.
    int RemoveExpired();
}

public interface IMusicServiceClient
{
    Task<IReadOnlyList<Song>> GetRandomSongsAsync(int categoryId, int count, string? bearerToken, CancellationToken cancellationToken = default);
}

public interface IGameEngine
{
    Task<CreateGameResponse> CreateGameAsync(CreateGameRequest request, string? bearerToken, CancellationToken cancellationToken = default);
}
