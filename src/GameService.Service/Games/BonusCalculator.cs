using GameService.Domain.Dtos;
using GameService.Domain.Models;

namespace GameService.Service.Games;

// SCRUM-151 (gameservice.gamehub v1.2): two bonus awards when the game ends, +1 point each, simply
// added to the player's score.
//  - "fastest" (Lynet): lowest average answer time over correct answers only. To qualify, a player
//    needs at least ceil(rounds / 3) correct answers (2 of 5, 4 of 10, 7 of 20).
//  - "streak" (Stimen): longest run of correct answers in a row, at least 2. A wrong answer and no
//    answer both break it.
// Ties share the award: everyone gets +1. Average times within 100 ms of the best count as equal.
public static class BonusCalculator
{
    public const string Fastest = "fastest";
    public const string Streak = "streak";
    public const int EqualTimeMs = 100;
    public const int MinStreak = 2;

    public record PlayerRounds(int UserId, IReadOnlyList<RoundHistory> History);

    public record Result(IReadOnlyList<AwardDto> Awards, IReadOnlyDictionary<int, int> BonusByUser);

    public static int MinCorrectForFastest(int rounds) => (rounds + 2) / 3;

    // Returns both awards ("fastest" first) and each player's bonus (0-2).
    public static Result Calculate(IReadOnlyList<PlayerRounds> players, int rounds)
    {
        var awards = new[] { FastestAward(players, rounds), StreakAward(players) };
        var bonus = players.ToDictionary(p => p.UserId, p => awards.Count(a => a.Winners.Contains(p.UserId)));
        return new Result(awards, bonus);
    }

    private static AwardDto FastestAward(IReadOnlyList<PlayerRounds> players, int rounds)
    {
        var minCorrect = MinCorrectForFastest(rounds);
        var stats = players.Select(p =>
        {
            var times = p.History.Where(h => h.Correct && h.AnsweredAfterMs is not null)
                .Select(h => (double)h.AnsweredAfterMs!.Value).ToList();
            double? average = times.Count > 0 ? times.Average() : null;
            var correct = p.History.Count(h => h.Correct);
            return (p.UserId, Average: average, Qualified: average is not null && correct >= minCorrect);
        }).ToList();

        var qualified = stats.Where(s => s.Qualified).ToList();
        var winners = new List<int>();
        if (qualified.Count > 0)
        {
            var best = qualified.Min(s => s.Average!.Value);
            winners = qualified.Where(s => s.Average!.Value - best <= EqualTimeMs).Select(s => s.UserId).ToList();
        }

        var contenders = stats
            .OrderByDescending(s => s.Qualified)
            .ThenBy(s => s.Average ?? double.MaxValue)
            .Select(s => new AwardContenderDto(s.UserId,
                s.Average is { } avg ? (int)Math.Round(avg, MidpointRounding.AwayFromZero) : null, s.Qualified))
            .ToList();
        return new AwardDto(Fastest, winners, contenders);
    }

    private static AwardDto StreakAward(IReadOnlyList<PlayerRounds> players)
    {
        var stats = players.Select(p => (p.UserId, Longest: LongestStreak(p.History))).ToList();
        var best = stats.Count > 0 ? stats.Max(s => s.Longest) : 0;
        var winners = best >= MinStreak
            ? stats.Where(s => s.Longest == best).Select(s => s.UserId).ToList()
            : [];

        var contenders = stats
            .OrderByDescending(s => s.Longest)
            .Select(s => new AwardContenderDto(s.UserId, s.Longest, s.Longest >= MinStreak))
            .ToList();
        return new AwardDto(Streak, winners, contenders);
    }

    // History has one entry per ended round, so a round without an answer (Correct = false) breaks it.
    public static int LongestStreak(IReadOnlyList<RoundHistory> history)
    {
        int longest = 0, current = 0;
        foreach (var round in history.OrderBy(h => h.Round))
        {
            current = round.Correct ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        return longest;
    }
}
