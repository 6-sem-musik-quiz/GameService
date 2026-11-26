using GameService.Domain.Dtos;
using GameService.Domain.Models;
using GameService.Service.Exceptions;
using GameService.Service.Games;
using GameService.Service.Interfaces;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace GameService.Service.Tests.Games;

public class GameEngineTests
{
    private readonly IMusicServiceClient _music = Substitute.For<IMusicServiceClient>();
    private readonly IGameCoordinator _coordinator = Substitute.For<IGameCoordinator>();
    private readonly GameEngine _engine;

    public GameEngineTests() => _engine = new GameEngine(_music, _coordinator);

    // The game GameEngine handed to the coordinator (which saves it, ADR-0004).
    private Game Created() =>
        (Game)_coordinator.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IGameCoordinator.GameCreatedAsync))
            .GetArguments()[0]!;

    [Fact]
    public async Task CreateGame_TellsTheCoordinator_SoTheJoinTimeoutStarts()
    {
        _music.GetRandomSongsAsync(7, 6, "tok", Arg.Any<CancellationToken>()).Returns(Songs(6));

        var response = await _engine.CreateGameAsync(Request(), "tok");

        await _coordinator.Received(1).GameCreatedAsync(Arg.Is<Game>(g => g.Id == response.GameId));
    }

    private static List<Song> Songs(int n) =>
        Enumerable.Range(1, n).Select(i => new Song { Id = i, Title = $"T{i}", Artists = ["A"], Year = 1960 + i }).ToList();

    private static CreateGameRequest Request(int players = 3, int rounds = 5) => new()
    {
        LobbyId = Guid.NewGuid(),
        CategoryId = 7,
        CategoryName = "80s",
        Rounds = rounds,
        AnswerTimeSeconds = 30,
        Players = Enumerable.Range(1, players).Select(i => new PlayerDto { UserId = i, UserName = $"p{i}" }).ToList()
    };

    [Fact]
    public async Task CreateGame_GivesAllPlayersTheSameStartCard_AndFetchesRoundsPlusOneSongs()
    {
        _music.GetRandomSongsAsync(7, 6, "tok", Arg.Any<CancellationToken>()).Returns(Songs(6));

        var response = await _engine.CreateGameAsync(Request(), "tok");

        var game = Created();
        Assert.Equal(response.GameId, game.Id);
        Assert.Equal(GameStatus.Waiting, game.Status);
        Assert.Equal(6, game.Songs.Count);
        Assert.All(game.Players, p =>
        {
            var card = Assert.Single(p.Timeline);
            Assert.Equal(1, card.Song.Id);
            Assert.Equal(0, p.Score);
        });
    }

    [Fact]
    public async Task CreateGame_TooFewSongs_Throws()
    {
        _music.GetRandomSongsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Songs(5));

        await Assert.ThrowsAsync<InsufficientSongsException>(() => _engine.CreateGameAsync(Request(), "tok"));
    }

    [Fact]
    public async Task CreateGame_MusicServiceFailure_Propagates()
    {
        _music.GetRandomSongsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new MusicServiceException("down"));

        await Assert.ThrowsAsync<MusicServiceException>(() => _engine.CreateGameAsync(Request(), "tok"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public async Task CreateGame_InvalidPlayerCount_Throws(int players)
    {
        await Assert.ThrowsAsync<GameValidationException>(() => _engine.CreateGameAsync(Request(players), "tok"));
        await _music.DidNotReceiveWithAnyArgs().GetRandomSongsAsync(default, default, default);
    }

    [Fact]
    public async Task CreateGame_DuplicateUserIds_Throws()
    {
        var request = Request();
        request.Players[1].UserId = request.Players[0].UserId;

        await Assert.ThrowsAsync<GameValidationException>(() => _engine.CreateGameAsync(request, "tok"));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(7, 30)]   // not a lobby choice
    [InlineData(25, 30)]
    [InlineData(5, 0)]
    [InlineData(5, 20)]   // not a lobby choice
    [InlineData(5, 120)]
    public async Task CreateGame_InvalidRoundsOrAnswerTime_Throws(int rounds, int answerTime)
    {
        var request = Request(rounds: rounds);
        request.AnswerTimeSeconds = answerTime;

        await Assert.ThrowsAsync<GameValidationException>(() => _engine.CreateGameAsync(request, "tok"));
    }

    [Theory]
    [InlineData(5, 15)]
    [InlineData(10, 30)]
    [InlineData(15, 45)]
    [InlineData(20, 60)]
    public async Task CreateGame_AllLobbySettings_AreAccepted(int rounds, int answerTime)
    {
        _music.GetRandomSongsAsync(7, rounds + 1, "tok", Arg.Any<CancellationToken>()).Returns(Songs(rounds + 1));
        var request = Request(rounds: rounds);
        request.AnswerTimeSeconds = answerTime;

        var response = await _engine.CreateGameAsync(request, "tok");

        Assert.Equal(rounds + 1, Created().Songs.Count);
    }

    [Fact]
    public async Task CreateGame_EmptyCategoryName_Throws()
    {
        var request = Request();
        request.CategoryName = " ";

        await Assert.ThrowsAsync<GameValidationException>(() => _engine.CreateGameAsync(request, "tok"));
    }
}
