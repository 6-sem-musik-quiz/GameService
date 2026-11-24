using GameService.Service.Games;

namespace GameService.Service.Tests.Games;

// SCRUM-104: equal scores share a placement and the next placement skips (1, 1, 3).
public class PlacementCalculatorTests
{
    [Theory]
    [InlineData(new[] { 5, 3, 1 }, new[] { 1, 2, 3 })]           // no tie
    [InlineData(new[] { 5, 5, 3 }, new[] { 1, 1, 3 })]           // tie on first place
    [InlineData(new[] { 5, 3, 3, 1 }, new[] { 1, 2, 2, 4 })]     // tie in the middle
    [InlineData(new[] { 2, 2, 2 }, new[] { 1, 1, 1 })]           // everyone tied
    [InlineData(new[] { 0, 4, 4 }, new[] { 3, 1, 1 })]           // input does not need to be sorted
    [InlineData(new[] { 7 }, new[] { 1 })]
    public void Calculate(int[] scores, int[] expected) =>
        Assert.Equal(expected, PlacementCalculator.Calculate(scores));
}
