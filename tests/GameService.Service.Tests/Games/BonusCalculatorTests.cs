using GameService.Domain.Models;
using GameService.Service.Games;

namespace GameService.Service.Tests.Games;

// SCRUM-151: "fastest" (Lynet) and "streak" (Stimen), +1 each, ties share.
public class BonusCalculatorTests
{
    // One string per player: one entry per round, "R1200" = right after 1200 ms,
    // "W800" = wrong after 800 ms, "-" = no answer.
    private static BonusCalculator.PlayerRounds Player(int userId, params string[] rounds) =>
        new(userId, rounds.Select((r, i) => r == "-"
            ? new RoundHistory { Round = i + 1, Correct = false, AnsweredAfterMs = null }
            : new RoundHistory { Round = i + 1, Correct = r[0] == 'R', AnsweredAfterMs = int.Parse(r[1..]) }).ToList());

    private static BonusCalculator.Result Calc(params BonusCalculator.PlayerRounds[] players) =>
        BonusCalculator.Calculate(players, players[0].History.Count);

    [Theory]
    [InlineData(5, 2)]
    [InlineData(10, 4)]
    [InlineData(20, 7)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    public void MinCorrectForFastest_IsRoundsDividedByThree_RoundedUp(int rounds, int expected) =>
        Assert.Equal(expected, BonusCalculator.MinCorrectForFastest(rounds));

    [Fact]
    public void Fastest_CountsOnlyCorrectAnswers()
    {
        // Alice is fastest overall, but her quick answers are wrong; on correct answers Bob is faster.
        var result = Calc(
            Player(1, "W100", "W100", "R5000", "R5000", "-"),
            Player(2, "R2000", "R2000", "W9000", "-", "-"));

        var fastest = result.Awards[0];
        Assert.Equal(BonusCalculator.Fastest, fastest.Kind);
        Assert.Equal([2], fastest.Winners);
        Assert.Equal([(2, (int?)2000, true), (1, 5000, true)], fastest.Contenders.Select(c => (c.UserId, c.Value, c.Qualified)));
    }

    [Fact]
    public void Fastest_NeedsTheMinimumOfCorrectAnswers()
    {
        // 5 rounds: 2 correct needed. Alice has 1 very fast correct answer; Bob 2 slower ones.
        var result = Calc(
            Player(1, "R100", "W100", "W100", "-", "-"),
            Player(2, "R4000", "R6000", "-", "-", "-"));

        var fastest = result.Awards[0];
        Assert.Equal([2], fastest.Winners);
        Assert.Equal([(2, (int?)5000, true), (1, 100, false)], fastest.Contenders.Select(c => (c.UserId, c.Value, c.Qualified)));
    }

    [Theory]
    [InlineData(3000, 3100, new[] { 1, 2 })] // exactly 100 ms apart: equal
    [InlineData(3000, 3000, new[] { 1, 2 })]
    [InlineData(3000, 3101, new[] { 1 })]
    public void Fastest_Tie_WithinOneHundredMs_BothGetIt(int aliceMs, int bobMs, int[] winners)
    {
        var result = Calc(
            Player(1, $"R{aliceMs}", $"R{aliceMs}", "-"),
            Player(2, $"R{bobMs}", $"R{bobMs}", "-"));

        Assert.Equal(winners, result.Awards[0].Winners);
        // Both also share the streak (2 each), so a fastest winner has 2 points and the other 1.
        Assert.All(new[] { 1, 2 }, u => Assert.Equal(winners.Contains(u) ? 2 : 1, result.BonusByUser[u]));
    }

    [Theory]
    [InlineData(new[] { "R1", "R1", "W1", "R1", "R1", "R1" }, 3)] // broken by a wrong answer
    [InlineData(new[] { "R1", "R1", "-", "R1" }, 2)]              // broken by no answer
    [InlineData(new[] { "R1", "R1", "R1", "R1" }, 4)]
    [InlineData(new[] { "W1", "-", "W1" }, 0)]
    [InlineData(new[] { "R1", "W1", "R1" }, 1)]
    public void LongestStreak(string[] rounds, int expected) =>
        Assert.Equal(expected, BonusCalculator.LongestStreak(Player(1, rounds).History));

    [Fact]
    public void Streak_GoesToTheLongest_AndATieShares()
    {
        var result = Calc(
            Player(1, "R1", "R1", "R1", "W1", "-"),
            Player(2, "-", "R1", "R1", "R1", "W1"),
            Player(3, "R1", "R1", "W1", "R1", "-"));

        var streak = result.Awards[1];
        Assert.Equal(BonusCalculator.Streak, streak.Kind);
        Assert.Equal([1, 2], streak.Winners);
        Assert.Equal([(1, (int?)3, true), (2, 3, true), (3, 2, true)], streak.Contenders.Select(c => (c.UserId, c.Value, c.Qualified)));
    }

    [Fact]
    public void Streak_OfOne_DoesNotQualify()
    {
        var result = Calc(Player(1, "R1", "W1", "R1"), Player(2, "-", "R1", "-"));

        Assert.Empty(result.Awards[1].Winners);
        Assert.All(result.Awards[1].Contenders, c => Assert.False(c.Qualified));
    }

    [Fact]
    public void NobodyQualified_GivesNoWinners_AndNoBonus()
    {
        var result = Calc(Player(1, "W1", "-", "W1"), Player(2, "-", "-", "-"));

        Assert.All(result.Awards, a => Assert.Empty(a.Winners));
        Assert.All(result.BonusByUser.Values, b => Assert.Equal(0, b));
        var fastest = result.Awards[0];
        Assert.All(fastest.Contenders, c => Assert.Equal((null, false), (c.Value, c.Qualified)));
    }

    [Fact]
    public void BonusByUser_CountsEachAwardWon()
    {
        // Alice: fastest and longest streak. Bob: neither (one correct answer only).
        var result = Calc(
            Player(1, "R1000", "R1000", "R1000"),
            Player(2, "W100", "R5000", "-"));

        Assert.Equal(2, result.BonusByUser[1]);
        Assert.Equal(0, result.BonusByUser[2]);
    }
}
