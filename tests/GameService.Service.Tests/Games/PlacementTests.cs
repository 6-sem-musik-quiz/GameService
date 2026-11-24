using GameService.Domain.Models;
using GameService.Service.Games;

namespace GameService.Service.Tests.Games;

// SCRUM-103: GameEngine.IsCorrectPlacement as a table test.
public class PlacementTests
{
    private static List<TimelineCard> Timeline(params int[] years) =>
        years.Select(y => new TimelineCard { Song = new Song { Year = y } }).ToList();

    [Theory]
    // before the first card
    [InlineData(0, 1970, true)]
    [InlineData(0, 1985, false)]
    // after the last card
    [InlineData(3, 2010, true)]
    [InlineData(3, 1995, false)]
    // between two cards
    [InlineData(1, 1985, true)]
    [InlineData(1, 1975, false)]
    [InlineData(1, 1995, false)]
    [InlineData(2, 1995, true)]
    // the same year counts on both sides
    [InlineData(0, 1980, true)]
    [InlineData(1, 1980, true)]
    [InlineData(1, 1990, true)]
    [InlineData(2, 1990, true)]
    [InlineData(3, 2000, true)]
    // outside 0..n
    [InlineData(-1, 1970, false)]
    [InlineData(4, 2010, false)]
    public void IsCorrectPlacement_In1980_1990_2000(int slotIndex, int year, bool expected) =>
        Assert.Equal(expected, GameEngine.IsCorrectPlacement(Timeline(1980, 1990, 2000), slotIndex, year));

    [Fact]
    public void IsCorrectPlacement_WithDuplicateYears_AcceptsAnySlotBetweenThem() =>
        Assert.True(GameEngine.IsCorrectPlacement(Timeline(1990, 1990), 1, 1990));
}
