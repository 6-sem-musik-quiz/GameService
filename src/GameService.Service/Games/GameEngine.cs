using GameService.Domain.Dtos;
using GameService.Domain.Models;
using GameService.Service.Exceptions;
using GameService.Service.Interfaces;
using GameService.Service.Sharding;

namespace GameService.Service.Games;

// ids: ADR-0004, the new game gets an id that this pod owns. Null (tests) = any id, as with one shard.
public class GameEngine(IMusicServiceClient musicClient, IGameStore store, IGameCoordinator coordinator,
    ShardedGameIds? ids = null) : IGameEngine
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 8;

    // The same choices LobbyService offers (LobbyManager.AllowedRounds / AllowedAnswerTimes).
    public static readonly IReadOnlySet<int> AllowedRounds = new HashSet<int> { 5, 10, 15, 20 };
    public static readonly IReadOnlySet<int> AllowedAnswerTimes = new HashSet<int> { 15, 30, 45, 60 };

    public async Task<CreateGameResponse> CreateGameAsync(CreateGameRequest request, string? bearerToken, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var needed = request.Rounds + 1;
        var songs = await musicClient.GetRandomSongsAsync(request.CategoryId, needed, bearerToken, cancellationToken);
        if (songs.Count < needed)
            throw new InsufficientSongsException(needed, songs.Count);

        var songList = songs.Take(needed).ToList();
        var startCard = songList[0];

        var game = new Game
        {
            Id = ids?.NewGameId() ?? Guid.NewGuid(),
            LobbyId = request.LobbyId,
            Settings = new GameSettings
            {
                CategoryId = request.CategoryId,
                CategoryName = request.CategoryName.Trim(),
                Rounds = request.Rounds,
                AnswerTimeSeconds = request.AnswerTimeSeconds
            },
            Songs = songList,
            Status = GameStatus.Waiting,
            Players = request.Players.Select(p => new GamePlayer
            {
                UserId = p.UserId,
                UserName = p.UserName.Trim(),
                Timeline = [new TimelineCard { Song = startCard }]
            }).ToList()
        };

        store.Add(game);
        coordinator.GameCreated(game); // starts the 15 s join timeout (SCRUM-102)
        return new CreateGameResponse(game.Id);
    }

    // SCRUM-103: is `year` in the right place if put at slotIndex in a timeline sorted by year?
    // slotIndex 0 = before the first card, n = after the last, i = between card i-1 and card i.
    // The same year counts as right on both sides.
    public static bool IsCorrectPlacement(IReadOnlyList<TimelineCard> timeline, int slotIndex, int year)
    {
        if (slotIndex < 0 || slotIndex > timeline.Count) return false;
        var afterPrevious = slotIndex == 0 || timeline[slotIndex - 1].Year <= year;
        var beforeNext = slotIndex == timeline.Count || year <= timeline[slotIndex].Year;
        return afterPrevious && beforeNext;
    }

    private static void Validate(CreateGameRequest? request)
    {
        if (request is null) throw new GameValidationException("Request body is required.");
        if (request.LobbyId == Guid.Empty) throw new GameValidationException("lobbyId is required.");
        if (request.CategoryId <= 0) throw new GameValidationException("categoryId must be positive.");
        if (string.IsNullOrWhiteSpace(request.CategoryName)) throw new GameValidationException("categoryName is required.");
        if (!AllowedRounds.Contains(request.Rounds))
            throw new GameValidationException($"rounds must be one of {string.Join(", ", AllowedRounds.Order())}.");
        if (!AllowedAnswerTimes.Contains(request.AnswerTimeSeconds))
            throw new GameValidationException($"answerTimeSeconds must be one of {string.Join(", ", AllowedAnswerTimes.Order())}.");

        var players = request.Players;
        if (players is null || players.Count < MinPlayers || players.Count > MaxPlayers)
            throw new GameValidationException($"A game requires {MinPlayers}-{MaxPlayers} players.");
        if (players.Any(p => p is null || p.UserId <= 0 || string.IsNullOrWhiteSpace(p.UserName)))
            throw new GameValidationException("Every player needs a userId and userName.");
        if (players.Select(p => p.UserId).Distinct().Count() != players.Count)
            throw new GameValidationException("Duplicate userId in players.");
    }
}
