namespace GameService.Service.Games;

// Pure helpers for round timing, so they can be tested on their own.
public static class RoundTiming
{
    // A random whole second in [0, max(0, duration - answerTime)], so the part that is played is at
    // least as long as the answer time. A song shorter than the answer time always starts at 0.
    public static int PickStartOffset(Random random, int durationSeconds, int answerTimeSeconds)
    {
        var max = Math.Max(0, durationSeconds - answerTimeSeconds);
        return random.Next(0, max + 1);
    }

    // The round numbers from..to that exist in a game with totalRounds rounds (inclusive).
    public static IEnumerable<int> Rounds(int from, int to, int totalRounds)
    {
        for (var round = Math.Max(1, from); round <= Math.Min(to, totalRounds); round++)
            yield return round;
    }
}
