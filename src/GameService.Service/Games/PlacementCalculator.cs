namespace GameService.Service.Games;

// SCRUM-104: placements from scores. Equal scores share a placement and the next one skips
// ("standard competition ranking"): scores 5, 5, 3 give placements 1, 1, 3.
public static class PlacementCalculator
{
    // Returns the placement for each score, in the same order as `scores`.
    public static int[] Calculate(IReadOnlyList<int> scores) =>
        scores.Select(score => 1 + scores.Count(other => other > score)).ToArray();
}
