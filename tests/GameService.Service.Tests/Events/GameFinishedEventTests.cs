using System.Text.Json;
using GameService.Domain.Events;
using GameService.Service.Publishers;

namespace GameService.Service.Tests.Events;

public class GameFinishedEventTests
{
    [Fact]
    public void SerializeGameFinished_ProducesValidJson_WithCorrectContractFields()
    {
        // Arrange
        var gameId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var startTime = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var endTime = new DateTime(2026, 11, 24, 12, 10, 0, DateTimeKind.Utc);

        var gameFinished = new GameFinishedEvent
        {
            GameId = gameId,
            Rounds = 10,
            StartTime = startTime,
            EndTime = endTime,
            CatId = 42,
            CategoryName = "80s Rock Classics",
            Players =
            [
                new PlayerGameResult
                {
                    UserId = 101,
                    UserName = "Mathias",
                    Score = 950,
                    CorrectAnswers = 9,
                    AnsweredRounds = 10,
                    Placement = 1
                },
                new PlayerGameResult
                {
                    UserId = 102,
                    UserName = "Asger",
                    Score = 800,
                    CorrectAnswers = 8,
                    AnsweredRounds = 10,
                    Placement = 2
                }
            ]
        };

        // Act
        var json = ResultPublisher.SerializeGameFinished(gameFinished);

        // Assert - parse JSON document to verify camelCase shape and values
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(gameId.ToString(), root.GetProperty("gameId").GetString());
        Assert.Equal(10, root.GetProperty("rounds").GetInt32());
        Assert.Equal(42, root.GetProperty("catId").GetInt32());
        Assert.Equal("80s Rock Classics", root.GetProperty("categoryName").GetString());

        var players = root.GetProperty("players");
        Assert.Equal(2, players.GetArrayLength());

        var p1 = players[0];
        Assert.Equal(101, p1.GetProperty("userId").GetInt32());
        Assert.Equal("Mathias", p1.GetProperty("userName").GetString());
        Assert.Equal(950, p1.GetProperty("score").GetInt32());
        Assert.Equal(9, p1.GetProperty("correctAnswers").GetInt32());
        Assert.Equal(10, p1.GetProperty("answeredRounds").GetInt32());
        Assert.Equal(1, p1.GetProperty("placement").GetInt32());
    }

    [Fact]
    public void SerializeGameFinished_Roundtrip_RestoresMatchingValues()
    {
        // Arrange
        var original = new GameFinishedEvent
        {
            GameId = Guid.NewGuid(),
            Rounds = 5,
            StartTime = DateTime.UtcNow.AddMinutes(-5),
            EndTime = DateTime.UtcNow,
            CatId = 7,
            CategoryName = "Dansk Pop",
            Players =
            [
                new PlayerGameResult
                {
                    UserId = 1,
                    UserName = "TestSpiller",
                    Score = 500,
                    CorrectAnswers = 5,
                    AnsweredRounds = 5,
                    Placement = 1
                }
            ]
        };

        // Act
        var json = ResultPublisher.SerializeGameFinished(original);
        var deserialized = JsonSerializer.Deserialize<GameFinishedEvent>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.GameId, deserialized.GameId);
        Assert.Equal(original.Rounds, deserialized.Rounds);
        Assert.Equal(original.CatId, deserialized.CatId);
        Assert.Equal(original.CategoryName, deserialized.CategoryName);
        Assert.Single(deserialized.Players);
        Assert.Equal("TestSpiller", deserialized.Players[0].UserName);
        Assert.Equal(500, deserialized.Players[0].Score);
    }
}
