using System.Text.Json;
using GameService.Domain.Dtos;
using GameService.Domain.Models;
using GameService.Service.Exceptions;
using GameService.Service.Games;
using GameService.Service.Interfaces;
using GameService.Service.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using GameService.Domain.Events;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace GameService.Service.Tests.Games;

// SCRUM-102. Time is a FakeTimeProvider, so no test waits for real time.
public class GameCoordinatorTests : IDisposable
{
    private const int Alice = 1, Bob = 2, Carol = 3;

    private sealed class RecordingNotifier : IGameNotifier
    {
        public List<object> Messages { get; } = [];

        public Task PrefetchAsync(Guid gameId, IReadOnlyList<PrefetchDto> files)
        {
            Messages.Add(files);
            return Task.CompletedTask;
        }

        public Task RoundStartedAsync(Guid gameId, RoundStartedDto round)
        {
            Messages.Add(round);
            return Task.CompletedTask;
        }

        public Task PlayerAnsweredAsync(Guid gameId, PlayerAnsweredDto answered)
        {
            Messages.Add(answered);
            return Task.CompletedTask;
        }

        public Task RoundEndedAsync(Guid gameId, RoundEndedDto round)
        {
            Messages.Add(round);
            return Task.CompletedTask;
        }

        public Task GameEndedAsync(Guid gameId, GameEndedDto game)
        {
            Messages.Add(game);
            return Task.CompletedTask;
        }

        public List<RoundStartedDto> RoundsStarted => Messages.OfType<RoundStartedDto>().ToList();
        public List<RoundEndedDto> RoundsEnded => Messages.OfType<RoundEndedDto>().ToList();

        public List<int[]> Prefetches =>
            Messages.OfType<IReadOnlyList<PrefetchDto>>().Select(f => f.Select(p => p.Round).ToArray()).ToList();
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingNotifier _notifier = new();
    private readonly IResultPublisher _publisher = Substitute.For<IResultPublisher>();
    private readonly InMemoryGameStore _store;
    private readonly GameCoordinator _coordinator;

    public GameCoordinatorTests()
    {
        _store = new InMemoryGameStore(_time);
        _coordinator = new GameCoordinator(_store, _notifier, _publisher, _time, MsOptions.Create(new GameOptions()),
            NullLogger<GameCoordinator>.Instance, new Random(42));
    }

    public void Dispose() => _coordinator.Dispose();

    private Game CreateGame(int rounds = 10, int answerTime = 30, int durationSeconds = 200, int players = 2)
    {
        var game = new Game
        {
            Id = Guid.NewGuid(),
            LobbyId = Guid.NewGuid(),
            Settings = new GameSettings { CategoryId = 1, CategoryName = "80s", Rounds = rounds, AnswerTimeSeconds = answerTime },
            Songs = Enumerable.Range(0, rounds + 1).Select(i => new Song
            {
                Id = 100 + i, Title = $"Song {i}", Artists = [$"Artist {i}"], Year = 1970 + i,
                DurationSeconds = durationSeconds, AudioUrl = $"https://media.example/songs/{i}.mp3"
            }).ToList(),
            Players = Enumerable.Range(1, players).Select(id => new GamePlayer { UserId = id, UserName = $"p{id}" }).ToList()
        };
        foreach (var player in game.Players)
            player.Timeline.Add(new TimelineCard { Song = game.Songs[0] });

        _store.Add(game);
        _coordinator.GameCreated(game);
        return game;
    }

    private Task<GameStateDto> Join(Game game, int userId, string? connectionId = null) =>
        _coordinator.JoinAsync(game.Id, userId, connectionId ?? $"conn-{userId}", () => Task.CompletedTask);

    private async Task StartGame(Game game)
    {
        foreach (var player in game.Players)
            await Join(game, player.UserId);
    }

    private async Task AllReady(Game game, int round)
    {
        foreach (var player in game.Players)
            await _coordinator.ReadyAsync(game.Id, player.UserId, round);
    }

    private async Task AllAnswer(Game game, int round, int slotIndex = 0)
    {
        foreach (var player in game.Players)
            await _coordinator.SubmitAnswerAsync(game.Id, player.UserId, round, slotIndex);
    }

    private void PauseBetweenRounds() => _time.Advance(TimeSpan.FromSeconds(8));

    // ---- Start offset ----

    [Theory]
    [InlineData(200, 30)]
    [InlineData(31, 30)]
    [InlineData(30, 30)]   // exactly as long as the answer time
    [InlineData(10, 30)]   // shorter than the answer time
    [InlineData(0, 15)]
    public void PickStartOffset_IsWithinZeroAndDurationMinusAnswerTime(int duration, int answerTime)
    {
        var random = new Random(7);
        var max = Math.Max(0, duration - answerTime);

        var offsets = Enumerable.Range(0, 2000).Select(_ => RoundTiming.PickStartOffset(random, duration, answerTime)).ToList();

        Assert.All(offsets, o => Assert.InRange(o, 0, max));
        if (max > 0)
            Assert.Contains(max, offsets); // the upper bound can be reached
    }

    [Fact]
    public async Task RoundStarted_OffsetIsZero_WhenSongIsShorterThanAnswerTime()
    {
        var game = CreateGame(answerTime: 30, durationSeconds: 20);
        await StartGame(game);

        await AllReady(game, 1);

        Assert.Equal(0, Assert.Single(_notifier.RoundsStarted).StartOffsetSeconds);
    }

    // ---- Who may join ----

    [Fact]
    public async Task Join_UnknownGame_Throws()
    {
        await Assert.ThrowsAsync<GameAccessException>(() =>
            _coordinator.JoinAsync(Guid.NewGuid(), Alice, "c", () => Task.CompletedTask));
    }

    [Fact]
    public async Task Join_UserNotInGame_Throws_AndIsNotSubscribed()
    {
        var game = CreateGame();
        var subscribed = false;

        await Assert.ThrowsAsync<GameAccessException>(() =>
            _coordinator.JoinAsync(game.Id, 99, "c", () => { subscribed = true; return Task.CompletedTask; }));

        Assert.False(subscribed);
    }

    [Fact]
    public async Task Join_ReturnsStateWithMyTimelineAndFirstFiveFiles_BeforeTheGameStarts()
    {
        var game = CreateGame(rounds: 10);

        var state = await Join(game, Alice);

        Assert.Equal(game.Id, state.GameId);
        Assert.Equal(0, state.Round);
        Assert.Equal(10, state.TotalRounds);
        Assert.Equal(new GameSettingsDto(1, "80s", 10, 30), state.Settings);
        Assert.Equal([Alice, Bob], state.Players.Select(p => p.UserId));
        var card = Assert.Single(state.MyTimeline);
        Assert.Equal(("Song 0", 1970), (card.Title, card.Year));
        Assert.Equal(["Artist 0"], card.Artists);
        Assert.Equal([1, 2, 3, 4, 5], state.Prefetch.Select(p => p.Round));
        Assert.Equal("https://media.example/songs/1.mp3", state.Prefetch[0].AudioUrl);
        Assert.Null(state.CurrentRound);
    }

    // ---- Game start ----

    [Fact]
    public async Task Game_Starts_WhenAllPlayersHaveJoined()
    {
        var game = CreateGame(rounds: 10);

        await Join(game, Alice);
        Assert.Empty(_notifier.Messages);
        Assert.Equal(GameStatus.Waiting, game.Status);

        await Join(game, Bob);

        Assert.Equal(GameStatus.Playing, game.Status);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, game.StartedAt);
        Assert.Equal([[1, 2, 3, 4, 5]], _notifier.Prefetches);
        Assert.Empty(_notifier.RoundsStarted); // round 1 waits for Ready
    }

    [Fact]
    public async Task Game_Starts_FifteenSecondsAfterCreation_WhenNotEveryoneJoins()
    {
        var game = CreateGame();
        await Join(game, Alice);

        _time.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(GameStatus.Waiting, game.Status);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(GameStatus.Playing, game.Status);
        Assert.Equal([[1, 2, 3, 4, 5]], _notifier.Prefetches);
    }

    [Fact]
    public async Task Game_Starts_AfterTimeout_EvenWhenNobodyJoined()
    {
        var game = CreateGame();

        _time.Advance(TimeSpan.FromSeconds(15));

        Assert.Equal(GameStatus.Playing, game.Status);
    }

    // ---- Round start ----

    [Fact]
    public async Task Round_Starts_WhenAllConnectedPlayersAreReady()
    {
        var game = CreateGame(rounds: 10, answerTime: 30);
        await StartGame(game);

        await _coordinator.ReadyAsync(game.Id, Alice, 1);
        Assert.Empty(_notifier.RoundsStarted);

        await _coordinator.ReadyAsync(game.Id, Bob, 1);

        var started = Assert.Single(_notifier.RoundsStarted);
        Assert.Equal(1, started.Round);
        Assert.Equal(10, started.TotalRounds);
        Assert.Equal(3000, started.StartsInMs);
        Assert.Equal(30, started.AnswerTimeSeconds);
        Assert.Equal(1, game.CurrentRound);
        Assert.Equal(_time.GetUtcNow().AddMilliseconds(3000).AddSeconds(30), game.ActiveRound!.EndsAt);
    }

    [Fact]
    public async Task Round_Starts_AfterReadyTimeout_WhenSomeoneIsNotReady()
    {
        var game = CreateGame();
        await StartGame(game);
        await _coordinator.ReadyAsync(game.Id, Alice, 1);

        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.Empty(_notifier.RoundsStarted);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Assert.Single(_notifier.RoundsStarted).Round);
    }

    [Fact]
    public async Task Round_DoesNotWaitFor_PlayersWhoAreNotConnected()
    {
        var game = CreateGame(players: 3);
        await Join(game, Alice);
        await Join(game, Bob);
        _time.Advance(TimeSpan.FromSeconds(15)); // Carol never joins; the game starts anyway

        await _coordinator.ReadyAsync(game.Id, Alice, 1);
        await _coordinator.DisconnectedAsync("conn-2"); // Bob drops; now only Alice is connected

        Assert.Equal(1, Assert.Single(_notifier.RoundsStarted).Round);
    }

    [Fact]
    public async Task Ready_SentEarly_ForALaterRound_CountsWhenThatRoundIsPrepared()
    {
        var game = CreateGame(rounds: 10);
        await StartGame(game);
        await AllReady(game, 1);
        await AllReady(game, 2); // downloaded during round 1
        await AllAnswer(game, 1);

        PauseBetweenRounds();

        Assert.Equal([1, 2], _notifier.RoundsStarted.Select(r => r.Round));
    }

    [Fact]
    public async Task Ready_ForARoundOutsideTheGame_IsRejected()
    {
        var game = CreateGame(rounds: 5);
        await StartGame(game);

        await Assert.ThrowsAsync<GameRuleException>(() => _coordinator.ReadyAsync(game.Id, Alice, 6));
        await Assert.ThrowsAsync<GameRuleException>(() => _coordinator.ReadyAsync(game.Id, Alice, 0));
        await Assert.ThrowsAsync<GameAccessException>(() => _coordinator.ReadyAsync(game.Id, Carol, 1));
    }

    // ---- Prefetch window ----

    [Fact]
    public async Task Prefetch_SendsRoundKPlusFive_WhenRoundKStarts_AndNothingBeyondTheLastRound()
    {
        var game = CreateGame(rounds: 10);
        await StartGame(game);

        for (var round = 1; round <= 10; round++)
        {
            await AllReady(game, round);
            await AllAnswer(game, round);
            PauseBetweenRounds();
        }

        Assert.Equal(Enumerable.Range(1, 10), _notifier.RoundsStarted.Select(r => r.Round));
        Assert.Equal([[1, 2, 3, 4, 5], [6], [7], [8], [9], [10]], _notifier.Prefetches);
        // Each RoundStarted for rounds 1-5 is followed by the prefetch of round + 5.
        var order = _notifier.Messages
            .Where(m => m is RoundStartedDto or IReadOnlyList<PrefetchDto>)
            .Select(m => m is RoundStartedDto r ? $"R{r.Round}" : $"P{string.Join(",", ((IReadOnlyList<PrefetchDto>)m).Select(p => p.Round))}");
        Assert.Equal(["P1,2,3,4,5", "R1", "P6", "R2", "P7", "R3", "P8", "R4", "P9", "R5", "P10", "R6", "R7", "R8", "R9", "R10"], order);
    }

    [Fact]
    public async Task Prefetch_ForAGameWithFewerRoundsThanTheWindow_SendsOnlyExistingRounds()
    {
        var game = CreateGame(rounds: 5);
        await StartGame(game);
        await AllReady(game, 1);

        Assert.Equal([[1, 2, 3, 4, 5]], _notifier.Prefetches);
    }

    // ---- Answers and round end (SCRUM-103) ----

    // Start card is 1970 (Songs[0]); round r plays year 1970 + r.
    private async Task<Game> GameInRound1(int rounds = 10, int players = 2)
    {
        var game = CreateGame(rounds: rounds, players: players);
        await StartGame(game);
        await AllReady(game, 1);
        return game;
    }

    [Fact]
    public async Task RightAnswer_AddsTheCardAndAPoint_WrongAnswer_AddsNothing()
    {
        var game = await GameInRound1();

        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1); // 1971 after 1970: right
        await _coordinator.SubmitAnswerAsync(game.Id, Bob, 1, 0);   // 1971 before 1970: wrong

        var alice = game.Players.Single(p => p.UserId == Alice);
        var bob = game.Players.Single(p => p.UserId == Bob);
        Assert.Equal((1, 1, 1), (alice.Score, alice.Correct, alice.Answered));
        Assert.Equal([1970, 1971], alice.Timeline.Select(c => c.Year));
        Assert.Equal((0, 0, 1), (bob.Score, bob.Correct, bob.Answered));
        Assert.Equal([1970], bob.Timeline.Select(c => c.Year));
    }

    [Fact]
    public async Task Round_EndsEarly_WhenAllHaveAnswered_AndRevealsSongAndResults()
    {
        var game = await GameInRound1();

        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1);
        Assert.Empty(_notifier.RoundsEnded);
        await _coordinator.SubmitAnswerAsync(game.Id, Bob, 1, 0);

        var ended = Assert.Single(_notifier.RoundsEnded);
        Assert.Equal(1, ended.Round);
        Assert.Equal(("Song 1", 1971), (ended.Song.Title, ended.Song.Year));
        var alice = ended.Results.Single(r => r.UserId == Alice);
        Assert.Equal((1, true, 1), (alice.SlotIndex, alice.Correct, alice.Score));
        Assert.Equal([1970, 1971], alice.Timeline.Select(c => c.Year));
        var bob = ended.Results.Single(r => r.UserId == Bob);
        Assert.Equal((0, false, 0), (bob.SlotIndex, bob.Correct, bob.Score));
        Assert.Null(game.ActiveRound);
    }

    [Fact]
    public async Task PlayerAnswered_IsSentForEachAnswer_WithoutTheAnswer()
    {
        var game = await GameInRound1();

        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1);

        Assert.Equal(new PlayerAnsweredDto(Alice), Assert.Single(_notifier.Messages.OfType<PlayerAnsweredDto>()));
        Assert.Equal(["userId"], JsonSerializer.SerializeToElement(new PlayerAnsweredDto(Alice), JsonSerializerOptions.Web)
            .EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Round_EndsAtEndsAtPlusOneSecond_AndNoAnswerCountsAsNotAnswered()
    {
        var game = await GameInRound1();
        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1);

        _time.Advance(TimeSpan.FromMilliseconds(3000) + TimeSpan.FromSeconds(30)); // endsAt
        Assert.Empty(_notifier.RoundsEnded);
        _time.Advance(TimeSpan.FromSeconds(1));

        var bob = Assert.Single(_notifier.RoundsEnded).Results.Single(r => r.UserId == Bob);
        Assert.Null(bob.SlotIndex);
        Assert.False(bob.Correct);
        Assert.Equal(0, game.Players.Single(p => p.UserId == Bob).Answered);
    }

    [Fact]
    public async Task Answer_InTheGraceSecond_StillCounts()
    {
        var game = await GameInRound1();
        _time.Advance(TimeSpan.FromMilliseconds(3000) + TimeSpan.FromSeconds(30.5));

        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1);
        _time.Advance(TimeSpan.FromSeconds(1)); // the round ends; answers are judged

        Assert.Equal(1, game.Players.Single(p => p.UserId == Alice).Score);
    }

    [Fact]
    public async Task Answers_AreRejected_WhenLateDoubleWrongRoundOrSlotOutOfRange()
    {
        var game = await GameInRound1();

        await Assert.ThrowsAsync<GameRuleException>(() => _coordinator.SubmitAnswerAsync(game.Id, Alice, 2, 0));  // not the current round
        await Assert.ThrowsAsync<GameRuleException>(() => _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 2));  // timeline has 1 card: 0..1
        await Assert.ThrowsAsync<GameRuleException>(() => _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, -1));
        await Assert.ThrowsAsync<GameAccessException>(() => _coordinator.SubmitAnswerAsync(game.Id, Carol, 1, 0));

        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1);
        await Assert.ThrowsAsync<GameRuleException>(() => _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 0)); // double answer

        _time.Advance(TimeSpan.FromSeconds(34)); // round over
        await Assert.ThrowsAsync<GameRuleException>(() => _coordinator.SubmitAnswerAsync(game.Id, Bob, 1, 0));
        Assert.Equal(1, game.Players.Single(p => p.UserId == Alice).Answered);
    }

    [Fact]
    public async Task NextRound_IsPrepared_EightSecondsAfterRoundEnded()
    {
        var game = await GameInRound1();
        await AllReady(game, 2);
        await AllAnswer(game, 1, slotIndex: 1);

        _time.Advance(TimeSpan.FromSeconds(7));
        Assert.Single(_notifier.RoundsStarted);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal([1, 2], _notifier.RoundsStarted.Select(r => r.Round));
        // The timeline grew, so round 2 allows slots 0..2.
        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 2, 2);
    }

    [Fact]
    public async Task Round_DoesNotWaitForAnswersFrom_DisconnectedPlayers()
    {
        var game = await GameInRound1();
        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1);

        await _coordinator.DisconnectedAsync("conn-2");

        Assert.Single(_notifier.RoundsEnded);
    }

    // ---- Game end (SCRUM-104) ----

    // Plays every round. Round r plays year 1970 + r, so "after the last card" is always right
    // and "before the first card" always wrong. `right` decides per player.
    private async Task PlayAllRounds(Game game, Func<int, bool> right)
    {
        for (var round = 1; round <= game.Settings.Rounds; round++)
        {
            await AllReady(game, round);
            foreach (var player in game.Players)
                await _coordinator.SubmitAnswerAsync(game.Id, player.UserId, round,
                    right(player.UserId) ? player.Timeline.Count : 0);
            PauseBetweenRounds();
        }
    }

    [Fact]
    public async Task GameEnded_IsSentAfterTheLastRoundEnded_WithStandingsSortedByScore()
    {
        var game = CreateGame(rounds: 5, players: 3);
        await StartGame(game);

        await PlayAllRounds(game, userId => userId == Bob); // Bob 5, Alice 0, Carol 0

        Assert.IsType<GameEndedDto>(_notifier.Messages[^1]);
        Assert.IsType<RoundEndedDto>(_notifier.Messages[^2]);
        var ended = Assert.Single(_notifier.Messages.OfType<GameEndedDto>());
        Assert.Equal(game.Id, ended.GameId);
        Assert.Equal([(Bob, 5, 1), (Alice, 0, 2), (Carol, 0, 2)], ended.Standings.Select(s => (s.UserId, s.Score, s.Placement)));
        var bob = ended.Standings[0];
        Assert.Equal((5, 5), (bob.Correct, bob.Answered));
        Assert.Equal(6, bob.Timeline.Count);
        Assert.Equal(GameStatus.Finished, game.Status);
        Assert.Equal(_time.GetUtcNow().AddSeconds(-8).UtcDateTime, game.EndedAt); // ended before the last pause
        Assert.Equal(5, _notifier.RoundsStarted.Count); // no round 6
    }

    [Fact]
    public async Task GameEnded_PublishesGameFinishedOnce_WithTheRightNumbers()
    {
        var game = CreateGame(rounds: 5);
        await StartGame(game);
        var startedAt = game.StartedAt;

        await PlayAllRounds(game, _ => true); // a tie on first place

        await _publisher.Received(1).PublishGameFinishedAsync(Arg.Is<GameFinishedEvent>(e =>
            e.GameId == game.Id && e.Rounds == 5 && e.CatId == 1 && e.CategoryName == "80s"
            && e.StartTime == startedAt && e.EndTime == game.EndedAt
            && e.Players.Count == 2
            && e.Players.All(p => p.Score == 5 && p.CorrectAnswers == 5 && p.AnsweredRounds == 5 && p.Placement == 1)
            && e.Players.Select(p => p.UserName).Order().SequenceEqual(new[] { "p1", "p2" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GameEnded_IsStillSent_WhenPublishingFails()
    {
        _publisher.PublishGameFinishedAsync(Arg.Any<GameFinishedEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("RabbitMQ is down"));
        var game = CreateGame(rounds: 5);
        await StartGame(game);

        await PlayAllRounds(game, _ => false); // must not throw out of SubmitAnswer

        Assert.Single(_notifier.Messages.OfType<GameEndedDto>());
        Assert.Equal(GameStatus.Finished, game.Status);
    }

    [Fact]
    public async Task UnansweredRounds_CountAsNotAnswered_InTheFinalResult()
    {
        var game = CreateGame(rounds: 5);
        await StartGame(game);

        for (var round = 1; round <= 5; round++)
        {
            await AllReady(game, round);
            _time.Advance(TimeSpan.FromSeconds(34)); // nobody answers; the round times out
            PauseBetweenRounds();
        }

        var standings = Assert.Single(_notifier.Messages.OfType<GameEndedDto>()).Standings;
        Assert.All(standings, s => Assert.Equal((0, 0, 0, 1), (s.Score, s.Correct, s.Answered, s.Placement)));
    }

    // ---- What clients see ----

    [Fact]
    public void RoundStarted_HasNoSongInformation()
    {
        var json = JsonSerializer.SerializeToElement(new RoundStartedDto(1, 10, 3000, 42, 30), JsonSerializerOptions.Web);

        Assert.Equal(["round", "totalRounds", "startsInMs", "startOffsetSeconds", "answerTimeSeconds"],
            json.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Rejoin_DuringARound_ReturnsWhatIsLeftOfIt()
    {
        var game = CreateGame(rounds: 10, answerTime: 30);
        await StartGame(game);
        await AllReady(game, 1);
        var started = _notifier.RoundsStarted.Single();

        // Before playback: the remaining delay.
        _time.Advance(TimeSpan.FromMilliseconds(1000));
        var beforePlayback = (await Join(game, Alice, "conn-1b")).CurrentRound!;
        Assert.Equal(new RoundStartedDto(1, 10, 2000, started.StartOffsetSeconds, 30), beforePlayback);

        // 2 s of delay left + 5 s of playback: start now, 5 s further into the song, 5 s less to answer.
        _time.Advance(TimeSpan.FromSeconds(7));
        var state = await Join(game, Alice, "conn-1c");
        Assert.Equal(1, state.Round);
        Assert.Equal(new RoundStartedDto(1, 10, 0, started.StartOffsetSeconds + 5, 25), state.CurrentRound);
        Assert.Equal([1, 2, 3, 4, 5, 6], state.Prefetch.Select(p => p.Round));

        // After the answer time, no current round.
        _time.Advance(TimeSpan.FromSeconds(25));
        Assert.Null((await Join(game, Alice, "conn-1d")).CurrentRound);
    }
}
