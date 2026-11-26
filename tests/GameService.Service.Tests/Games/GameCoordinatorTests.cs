using System.Text.Json;
using GameService.Domain.Dtos;
using GameService.Domain.Models;
using GameService.Service.Exceptions;
using GameService.Repository.State;
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
    private readonly InMemoryDeadlineQueue _deadlines;
    private readonly GameCoordinator _coordinator;

    // ADR-0004: the same coordinator as in the cluster, with the in-memory store, locks and deadlines.
    public GameCoordinatorTests()
    {
        _deadlines = new InMemoryDeadlineQueue(_time);
        _coordinator = new GameCoordinator(new InMemoryGameSessionStore(_time), new InMemoryGameLocks(), _deadlines,
            _notifier, _publisher, _time, MsOptions.Create(new GameOptions()), NullLogger<GameCoordinator>.Instance, new Random(42));
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        _deadlines.Dispose();
    }

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

        _coordinator.GameCreatedAsync(game).GetAwaiter().GetResult(); // in memory: completes at once
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

    // SCRUM-159 keeps the song playing through the reveal: answer time + grace + pause must fit after the
    // offset, or the song runs out during the reveal (seen in the end-to-end test).
    [Fact]
    public void PlaySeconds_IsAnswerTime_PlusGrace_PlusTheRevealPause() =>
        Assert.Equal(30 + 1 + 8, RoundTiming.PlaySeconds(30, 1, 8));

    [Fact]
    public async Task EveryRound_LeavesRoomInTheSong_ForTheAnswerTimeAndTheReveal()
    {
        var game = CreateGame(rounds: 10, answerTime: 30, durationSeconds: 45); // 45 - (30 + 1 + 8) = 6
        await StartGame(game);

        for (var round = 1; round <= 10; round++)
        {
            await AllReady(game, round);
            await AllAnswer(game, round);
            PauseBetweenRounds();
        }

        var offsets = _notifier.RoundsStarted.Select(r => r.StartOffsetSeconds).ToList();
        Assert.Equal(10, offsets.Count);
        Assert.All(offsets, o => Assert.InRange(o, 0, 6));
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

        // Answered before playback started (still in startsInMs), so 0 ms.
        Assert.Equal(new PlayerAnsweredDto(Alice, 0), Assert.Single(_notifier.Messages.OfType<PlayerAnsweredDto>()));
        Assert.Equal(["userId", "answeredAfterMs"], JsonSerializer.SerializeToElement(new PlayerAnsweredDto(Alice, 0), JsonSerializerOptions.Web)
            .EnumerateObject().Select(p => p.Name));
    }

    // ---- Rival data (SCRUM-150) ----

    private const int PlaybackDelayMs = 3000; // GameOptions.StartDelayMs

    [Theory]
    [InlineData(0, 0)]          // still in startsInMs: before playback
    [InlineData(3000, 0)]       // the moment playback starts
    [InlineData(7200, 4200)]    // 4.2 s into the song
    [InlineData(33500, 30000)]  // in the grace second after the answer time: clamped to 30 s
    public async Task AnsweredAfterMs_IsMeasuredFromPlaybackStart_AndClamped(int msSinceRoundStarted, int expected)
    {
        var game = await GameInRound1();

        _time.Advance(TimeSpan.FromMilliseconds(msSinceRoundStarted));
        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1);

        Assert.Equal(expected, _notifier.Messages.OfType<PlayerAnsweredDto>().Single().AnsweredAfterMs);
    }

    [Fact]
    public async Task RoundEnded_HasEachAnswersTime_AndYearRange_AndNullsForNoAnswer()
    {
        var game = await GameInRound1(players: 3);
        _time.Advance(TimeSpan.FromMilliseconds(PlaybackDelayMs + 2500));
        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1); // after 1970: right
        _time.Advance(TimeSpan.FromMilliseconds(1000));
        await _coordinator.SubmitAnswerAsync(game.Id, Bob, 1, 0);   // before 1970: wrong
        // Carol does not answer; she is connected, so the round ends on time.
        _time.Advance(TimeSpan.FromSeconds(30));

        var results = Assert.Single(_notifier.RoundsEnded).Results;
        var alice = results.Single(r => r.UserId == Alice);
        Assert.Equal(2500, alice.AnsweredAfterMs);
        Assert.Equal([1970, null], alice.Range); // after the last card
        var bob = results.Single(r => r.UserId == Bob);
        Assert.Equal(3500, bob.AnsweredAfterMs);
        Assert.Equal([null, 1970], bob.Range);   // before the first card
        var carol = results.Single(r => r.UserId == Carol);
        Assert.Null(carol.AnsweredAfterMs);
        Assert.Null(carol.Range);
    }

    [Fact]
    public async Task Range_BetweenTwoCards_UsesTheTimelineBeforeThisRoundsCard()
    {
        var game = await GameInRound1();
        await AllReady(game, 2);
        await AllAnswer(game, 1, slotIndex: 1); // both right: timelines are now 1970, 1971
        PauseBetweenRounds();                   // round 2 plays 1972

        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 2, 1); // between 1970 and 1971: wrong
        await _coordinator.SubmitAnswerAsync(game.Id, Bob, 2, 2);   // after 1971: right

        var results = _notifier.RoundsEnded.Last().Results;
        Assert.Equal([1970, 1971], results.Single(r => r.UserId == Alice).Range);
        Assert.Equal([1971, null], results.Single(r => r.UserId == Bob).Range);
    }

    [Fact]
    public void RoundResult_SerializesNullsForNoAnswer_AndTheRangeAsAnArray()
    {
        var noAnswer = JsonSerializer.SerializeToElement(
            new RoundResultDto(3, "carol", null, false, 0, [], null, null), JsonSerializerOptions.Web);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, noAnswer.GetProperty("answeredAfterMs").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, noAnswer.GetProperty("range").ValueKind);

        var openEnd = JsonSerializer.SerializeToElement(
            new RoundResultDto(1, "alice", 0, true, 1, [], 1200, [null, 1970]), JsonSerializerOptions.Web);
        Assert.Equal("[null,1970]", openEnd.GetProperty("range").GetRawText());
    }

    [Fact]
    public async Task State_HasEveryonesCardsHistoryAndThePastSongs_GrowingOnePerRound()
    {
        var game = await GameInRound1();
        await AllReady(game, 2);
        _time.Advance(TimeSpan.FromMilliseconds(PlaybackDelayMs + 1000));
        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1); // right, after 1000 ms
        await _coordinator.SubmitAnswerAsync(game.Id, Bob, 1, 0);   // wrong, after 1000 ms

        var afterRound1 = await Join(game, Alice, "conn-1b");
        Assert.Equal([new PastSongDto(1, afterRound1.PastSongs[0].Song)], afterRound1.PastSongs);
        Assert.Equal(("Song 1", 1971), (afterRound1.PastSongs[0].Song.Title, afterRound1.PastSongs[0].Song.Year));
        var bob = afterRound1.Players.Single(p => p.UserId == Bob);
        Assert.Equal([1970], bob.Timeline.Select(c => c.Year));
        Assert.Equal([new RoundHistoryDto(1, false, 1000)], bob.History);
        var alice = afterRound1.Players.Single(p => p.UserId == Alice);
        Assert.Equal([1970, 1971], alice.Timeline.Select(c => c.Year));
        Assert.Equal([new RoundHistoryDto(1, true, 1000)], alice.History);

        PauseBetweenRounds();
        _time.Advance(TimeSpan.FromSeconds(34)); // round 2 times out, nobody answers

        var afterRound2 = await Join(game, Alice, "conn-1c");
        Assert.Equal([1, 2], afterRound2.PastSongs.Select(s => s.Round));
        Assert.Equal(new RoundHistoryDto(2, false, null), afterRound2.Players.Single(p => p.UserId == Bob).History[^1]);
    }

    [Fact]
    public async Task AnsweredOrder_FollowsTheOrderOfAnswers_AndResetsWithTheNextRound()
    {
        var game = await GameInRound1(players: 3);
        await AllReady(game, 2);
        await _coordinator.SubmitAnswerAsync(game.Id, Bob, 1, 0);
        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1);

        var during = await Join(game, Carol, "conn-3b");
        Assert.Equal(2, during.Players.Single(p => p.UserId == Alice).AnsweredOrder);
        Assert.Equal(1, during.Players.Single(p => p.UserId == Bob).AnsweredOrder);
        Assert.Null(during.Players.Single(p => p.UserId == Carol).AnsweredOrder);

        await _coordinator.SubmitAnswerAsync(game.Id, Carol, 1, 0); // everyone answered: round ends
        PauseBetweenRounds();                                       // round 2 starts

        var next = await Join(game, Carol, "conn-3c");
        Assert.Equal(2, next.Round);
        Assert.All(next.Players, p => Assert.Null(p.AnsweredOrder));
    }

    [Fact]
    public async Task StateDuringARound_NeverContainsTheRoundsSongOrAnyPlacement()
    {
        var game = await GameInRound1();
        await _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 1); // a right answer, not yet judged

        var during = await Join(game, Bob, "conn-2b");
        _time.Advance(TimeSpan.FromMilliseconds(PlaybackDelayMs + 30_500)); // the grace second
        var inGrace = await Join(game, Bob, "conn-2c");

        foreach (var state in new[] { during, inGrace })
        {
            Assert.Empty(state.PastSongs);
            Assert.All(state.Players, p => Assert.Empty(p.History));
            Assert.DoesNotContain(state.Players.SelectMany(p => p.Timeline), c => c.Year == 1971 || c.Title == "Song 1");
            Assert.DoesNotContain(state.MyTimeline, c => c.Year == 1971);
        }
        var json = JsonSerializer.Serialize(during, JsonSerializerOptions.Web);
        Assert.DoesNotContain("Song 1\"", json);
        Assert.DoesNotContain("1971", json);
        Assert.DoesNotContain("slot", json, StringComparison.OrdinalIgnoreCase);
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

        await PlayAllRounds(game, userId => userId == Bob); // Bob 5 + 2 bonus, Alice 0, Carol 0

        Assert.IsType<GameEndedDto>(_notifier.Messages[^1]);
        Assert.IsType<RoundEndedDto>(_notifier.Messages[^2]);
        var ended = Assert.Single(_notifier.Messages.OfType<GameEndedDto>());
        Assert.Equal(game.Id, ended.GameId);
        Assert.Equal([(Bob, 7, 1), (Alice, 0, 2), (Carol, 0, 2)], ended.Standings.Select(s => (s.UserId, s.Score, s.Placement)));
        var bob = ended.Standings[0];
        Assert.Equal((5, 5), (bob.Correct, bob.Answered));
        Assert.Equal(6, bob.Timeline.Count);
        Assert.Equal(GameStatus.Finished, game.Status);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, game.EndedAt); // ended after the last pause
        Assert.Equal(5, _notifier.RoundsStarted.Count); // no round 6
    }

    // SCRUM-170: the last round's reveal is shown as long as the others before the end screen.
    [Fact]
    public async Task GameEnded_IsNotSent_UntilThePauseAfterTheLastRoundEnded_IsOver()
    {
        var game = CreateGame(rounds: 2);
        await StartGame(game);
        await AllReady(game, 1);
        await AllAnswer(game, 1);
        PauseBetweenRounds();
        await AllReady(game, 2);
        await AllAnswer(game, 2);

        Assert.Equal(2, _notifier.RoundsEnded.Count);
        _time.Advance(TimeSpan.FromSeconds(8) - TimeSpan.FromMilliseconds(1));
        Assert.Empty(_notifier.Messages.OfType<GameEndedDto>());
        Assert.Equal(GameStatus.Playing, game.Status);
        await _publisher.DidNotReceive().PublishGameFinishedAsync(Arg.Any<GameFinishedEvent>(), Arg.Any<CancellationToken>());

        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Single(_notifier.Messages.OfType<GameEndedDto>());
        Assert.IsType<RoundEndedDto>(_notifier.Messages[^2]);
        Assert.Equal(GameStatus.Finished, game.Status);
        await _publisher.Received(1).PublishGameFinishedAsync(Arg.Any<GameFinishedEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DuringThePauseAfterTheLastRound_NothingCanChangeTheGame()
    {
        var game = CreateGame(rounds: 1);
        await StartGame(game);
        await AllReady(game, 1);
        await AllAnswer(game, 1);

        await Assert.ThrowsAsync<GameRuleException>(() => _coordinator.SubmitAnswerAsync(game.Id, Alice, 1, 0));
        await Assert.ThrowsAsync<GameRuleException>(() => _coordinator.ReadyAsync(game.Id, Alice, 2));
        await _coordinator.ReadyAsync(game.Id, Alice, 1); // an old round: ignored
        await _coordinator.PrepareRoundAsync(game.Id, 2);

        Assert.Single(_notifier.RoundsStarted);
        Assert.Empty(_notifier.Messages.OfType<GameEndedDto>());
    }

    [Fact]
    public async Task Rejoin_DuringThePauseAfterTheLastRound_GetsItsResult_ThenGameEnded()
    {
        var game = CreateGame(rounds: 2);
        await StartGame(game);
        await AllReady(game, 1);
        await AllAnswer(game, 1);
        PauseBetweenRounds();
        await AllReady(game, 2);
        await AllAnswer(game, 2, slotIndex: 1); // round 1 was wrong, so one card; after it: right

        _time.Advance(TimeSpan.FromSeconds(3));
        var state = await Join(game, Alice, "conn-1b");

        Assert.Equal(2, state.Round);
        Assert.Null(state.CurrentRound);
        Assert.Empty(state.Prefetch);
        Assert.Equal([1, 2], state.PastSongs!.Select(s => s.Round));
        var alice = state.Players.Single(p => p.UserId == Alice);
        Assert.Equal(2, alice.History!.Count);
        Assert.True(alice.History[^1].Correct);
        Assert.Empty(_notifier.Messages.OfType<GameEndedDto>());

        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Single(_notifier.Messages.OfType<GameEndedDto>());
    }

    [Fact]
    public async Task GameEnded_PublishesGameFinishedOnce_WithTheRightNumbers()
    {
        var game = CreateGame(rounds: 5);
        await StartGame(game);
        var startedAt = game.StartedAt;

        await PlayAllRounds(game, _ => true); // a tie on first place, and both share both awards

        await _publisher.Received(1).PublishGameFinishedAsync(Arg.Is<GameFinishedEvent>(e =>
            e.GameId == game.Id && e.LobbyId == game.LobbyId && e.Rounds == 5 && e.CatId == 1 && e.CategoryName == "80s"
            && e.StartTime == startedAt && e.EndTime == game.EndedAt
            && e.Players.Count == 2
            && e.Players.All(p => p.Score == 7 && p.CorrectAnswers == 5 && p.AnsweredRounds == 5 && p.Placement == 1)
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

    // ---- Bonus awards (SCRUM-151) ----

    // 5 rounds. Bob answers first (1000 ms) and is right in rounds 1-2, then wrong; Alice answers
    // later (3000 ms) and is right every round. Bob is fastest on correct answers (2 of 5 is enough);
    // Alice has the longest streak.
    private async Task PlayFastBobSteadyAlice(Game game)
    {
        for (var round = 1; round <= game.Settings.Rounds; round++)
        {
            await AllReady(game, round);
            _time.Advance(TimeSpan.FromMilliseconds(PlaybackDelayMs + 1000));
            var bob = game.Players.Single(p => p.UserId == Bob);
            await _coordinator.SubmitAnswerAsync(game.Id, Bob, round, round <= 2 ? bob.Timeline.Count : 0);
            _time.Advance(TimeSpan.FromMilliseconds(2000));
            var alice = game.Players.Single(p => p.UserId == Alice);
            await _coordinator.SubmitAnswerAsync(game.Id, Alice, round, alice.Timeline.Count);
            PauseBetweenRounds();
        }
    }

    [Fact]
    public async Task GameEnded_HasAwards_AndStandingsIncludeTheBonus()
    {
        var game = CreateGame(rounds: 5);
        await StartGame(game);

        await PlayFastBobSteadyAlice(game);

        var ended = Assert.Single(_notifier.Messages.OfType<GameEndedDto>());
        Assert.Equal([(Alice, 6, 1), (Bob, 3, 2)], ended.Standings.Select(s => (s.UserId, s.Score, s.Placement)));
        Assert.Equal([(Alice, 5), (Bob, 2)], ended.Standings.Select(s => (s.UserId, s.Correct)));

        Assert.Equal(["fastest", "streak"], ended.Awards.Select(a => a.Kind));
        var fastest = ended.Awards[0];
        Assert.Equal([Bob], fastest.Winners);
        Assert.Equal([(Bob, (int?)1000, true), (Alice, 3000, true)], fastest.Contenders.Select(c => (c.UserId, c.Value, c.Qualified)));
        var streak = ended.Awards[1];
        Assert.Equal([Alice], streak.Winners);
        Assert.Equal([(Alice, (int?)5, true), (Bob, 2, true)], streak.Contenders.Select(c => (c.UserId, c.Value, c.Qualified)));

        // The round results are still the scores before the awards.
        Assert.Equal(5, _notifier.RoundsEnded[^1].Results.Single(r => r.UserId == Alice).Score);
    }

    [Fact]
    public async Task GameEnded_HasEveryRoundsSong()
    {
        var game = CreateGame(rounds: 3);
        await StartGame(game);

        await PlayAllRounds(game, _ => false);

        var ended = Assert.Single(_notifier.Messages.OfType<GameEndedDto>());
        Assert.Equal([(1, "Song 1", 1971), (2, "Song 2", 1972), (3, "Song 3", 1973)],
            ended.Songs.Select(s => (s.Round, s.Song.Title, s.Song.Year)));
        Assert.All(ended.Awards, a => Assert.Empty(a.Winners));
    }

    [Fact]
    public async Task GameFinished_ScoreIncludesTheBonus()
    {
        var game = CreateGame(rounds: 5);
        await StartGame(game);

        await PlayFastBobSteadyAlice(game);

        await _publisher.Received(1).PublishGameFinishedAsync(Arg.Is<GameFinishedEvent>(e =>
            e.Players.Single(p => p.UserId == Alice).Score == 6
            && e.Players.Single(p => p.UserId == Alice).Placement == 1
            && e.Players.Single(p => p.UserId == Bob).Score == 3
            && e.Players.Single(p => p.UserId == Bob).Placement == 2
            && e.Players.Single(p => p.UserId == Bob).CorrectAnswers == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GameEnded_Json_IsCamelCase()
    {
        var dto = new GameEndedDto(Guid.Empty, [],
            [new AwardDto("fastest", [1], [new AwardContenderDto(1, null, false)])],
            [new PastSongDto(1, new CardDto("t", ["a"], 1999))]);

        var json = JsonSerializer.SerializeToElement(dto, JsonSerializerOptions.Web);

        Assert.Equal(["gameId", "standings", "awards", "songs"], json.EnumerateObject().Select(p => p.Name));
        var award = json.GetProperty("awards")[0];
        Assert.Equal(["kind", "winners", "contenders"], award.EnumerateObject().Select(p => p.Name));
        var contender = award.GetProperty("contenders")[0];
        Assert.Equal(["userId", "value", "qualified"], contender.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, contender.GetProperty("value").ValueKind);
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
        var json = JsonSerializer.SerializeToElement(new RoundStartedDto(1, 10, 3000, 42, 30, _time.GetUtcNow()), JsonSerializerOptions.Web);

        Assert.Equal(["round", "totalRounds", "startsInMs", "startOffsetSeconds", "answerTimeSeconds", "playbackStartsAt"],
            json.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Rejoin_DuringARound_ReturnsWhatIsLeftOfIt()
    {
        var game = CreateGame(rounds: 10, answerTime: 30);
        await StartGame(game);
        await AllReady(game, 1);
        var started = _notifier.RoundsStarted.Single();
        var playbackStartsAt = _time.GetUtcNow().AddMilliseconds(3000);
        Assert.Equal(playbackStartsAt, started.PlaybackStartsAt);

        // Before playback: the remaining delay.
        _time.Advance(TimeSpan.FromMilliseconds(1000));
        var beforePlayback = (await Join(game, Alice, "conn-1b")).CurrentRound!;
        Assert.Equal(new RoundStartedDto(1, 10, 2000, started.StartOffsetSeconds, 30, playbackStartsAt), beforePlayback);

        // 2 s of delay left + 5 s of playback: start now, 5 s further into the song, 5 s less to answer,
        // and playbackStartsAt moved by the same 5 s, so the three still agree.
        _time.Advance(TimeSpan.FromSeconds(7));
        var state = await Join(game, Alice, "conn-1c");
        Assert.Equal(1, state.Round);
        Assert.Equal(new RoundStartedDto(1, 10, 0, started.StartOffsetSeconds + 5, 25, playbackStartsAt.AddSeconds(5)),
            state.CurrentRound);
        Assert.Equal([1, 2, 3, 4, 5, 6], state.Prefetch.Select(p => p.Round));

        // SCRUM-168: between whole seconds the int fields round down, but playbackStartsAt keeps the
        // exact position: at server time "now" the song is 5.4 s past the original offset.
        _time.Advance(TimeSpan.FromMilliseconds(400));
        var between = (await Join(game, Alice, "conn-1e")).CurrentRound!;
        Assert.Equal(started.StartOffsetSeconds + 5, between.StartOffsetSeconds);
        Assert.Equal(TimeSpan.FromMilliseconds(400), _time.GetUtcNow() - between.PlaybackStartsAt);

        // After the answer time, no current round.
        _time.Advance(TimeSpan.FromSeconds(25));
        Assert.Null((await Join(game, Alice, "conn-1d")).CurrentRound);
    }
}
