namespace GameService.Service.Games;

// Pure helpers for round timing, so they can be tested on their own.
public static class RoundTiming
{
    // A random whole second in [0, max(0, duration - playSeconds)], so the song does not run out while it
    // is played. A song shorter than that always starts at 0.
    public static int PickStartOffset(Random random, int durationSeconds, int playSeconds)
    {
        var max = Math.Max(0, durationSeconds - playSeconds);
        return random.Next(0, max + 1);
    }

    // SCRUM-159 keeps the song playing through the reveal, so it plays for the answer time, the grace
    // second(s) before the round ends, and the pause until the next round (or GameEnded, SCRUM-170).
    public static int PlaySeconds(int answerTimeSeconds, int answerGraceSeconds, int pauseBetweenRoundsSeconds) =>
        answerTimeSeconds + answerGraceSeconds + pauseBetweenRoundsSeconds;

    // The round numbers from..to that exist in a game with totalRounds rounds (inclusive).
    public static IEnumerable<int> Rounds(int from, int to, int totalRounds)
    {
        for (var round = Math.Max(1, from); round <= Math.Min(to, totalRounds); round++)
            yield return round;
    }
}
