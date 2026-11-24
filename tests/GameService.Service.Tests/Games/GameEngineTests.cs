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
    private readonly InMemoryGameStore _store = new(TimeProvider.System);
    private readonly GameEngine _engine;

    public GameEngineTests() => _engine = new GameEngine(_music, _store);

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

        var game = _store.Get(response.GameId)!;
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

        Assert.Equal(rounds + 1, _store.Get(response.GameId)!.Songs.Count);
    }

    [Fact]
    public async Task CreateGame_EmptyCategoryName_Throws()
    {
        var request = Request();
        request.CategoryName = " ";

        await Assert.ThrowsAsync<GameValidationException>(() => _engine.CreateGameAsync(request, "tok"));
    }
}

public class InMemoryGameStoreTests
{
    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void RemoveExpired_RemovesFinishedGamesAfter30Minutes_KeepsOthers()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryGameStore(time);
        var finished = new Game { Id = Guid.NewGuid(), Status = GameStatus.Finished, EndedAt = time.Now.UtcDateTime };
        var waiting = new Game { Id = Guid.NewGuid(), Status = GameStatus.Waiting };
        store.Add(finished);
        store.Add(waiting);

        time.Now = time.Now.AddMinutes(29);
        Assert.Equal(0, store.RemoveExpired());

        time.Now = time.Now.AddMinutes(2);
        Assert.Equal(1, store.RemoveExpired());
        Assert.Null(store.Get(finished.Id));
        Assert.NotNull(store.Get(waiting.Id));
    }
}
