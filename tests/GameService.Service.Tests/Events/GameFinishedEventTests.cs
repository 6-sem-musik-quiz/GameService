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
        var lobbyId = Guid.Parse("66666666-7777-8888-9999-000000000000");
        var startTime = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var endTime = new DateTime(2026, 11, 24, 12, 10, 0, DateTimeKind.Utc);

        var gameFinished = new GameFinishedEvent
        {
            GameId = gameId,
            LobbyId = lobbyId,
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
        Assert.Equal(lobbyId.ToString(), root.GetProperty("lobbyId").GetString()); // v1.1, SCRUM-172
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

    [Fact]
    public void SerializeGameFinished_EmptyPlayersList_ProducesEmptyJsonArray()
    {
        // Arrange
        var gameFinished = new GameFinishedEvent
        {
            GameId = Guid.NewGuid(),
            Rounds = 0,
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow,
            CatId = 1,
            CategoryName = "Empty Lobby",
            Players = []
        };

        // Act
        var json = ResultPublisher.SerializeGameFinished(gameFinished);

        // Assert
        using var doc = JsonDocument.Parse(json);
        var players = doc.RootElement.GetProperty("players");
        Assert.Equal(JsonValueKind.Array, players.ValueKind);
        Assert.Equal(0, players.GetArrayLength());
    }

    [Fact]
    public void SerializeGameFinished_HandlesDanishSpecialCharactersAndEmoji()
    {
        // Arrange
        var gameFinished = new GameFinishedEvent
        {
            GameId = Guid.NewGuid(),
            Rounds = 3,
            StartTime = DateTime.UtcNow,
            EndTime = DateTime.UtcNow,
            CatId = 99,
            CategoryName = "Danske Klassikere: Kim Larsen & Shu-bi-dua 🎵",
            Players =
            [
                new PlayerGameResult
                {
                    UserId = 42,
                    UserName = "Jørgen Åge Sørensen 🇩🇰",
                    Score = 300,
                    CorrectAnswers = 3,
                    AnsweredRounds = 3,
                    Placement = 1
                }
            ]
        };

        // Act
        var json = ResultPublisher.SerializeGameFinished(gameFinished);
        var roundtripped = JsonSerializer.Deserialize<GameFinishedEvent>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        // Assert
        Assert.NotNull(roundtripped);
        Assert.Equal("Danske Klassikere: Kim Larsen & Shu-bi-dua 🎵", roundtripped.CategoryName);
        Assert.Equal("Jørgen Åge Sørensen 🇩🇰", roundtripped.Players[0].UserName);
    }

    [Fact]
    public void SerializeGameFinished_PreservesIso8601UtcDateTimeFormat()
    {
        // Arrange
        var utcStart = new DateTime(2026, 11, 24, 14, 30, 0, DateTimeKind.Utc);
        var utcEnd = new DateTime(2026, 11, 24, 14, 45, 0, DateTimeKind.Utc);
        var game = new GameFinishedEvent
        {
            GameId = Guid.NewGuid(),
            StartTime = utcStart,
            EndTime = utcEnd,
            Players = []
        };

        // Act
        var json = ResultPublisher.SerializeGameFinished(game);

        // Assert
        using var doc = JsonDocument.Parse(json);
        var startTimeStr = doc.RootElement.GetProperty("startTime").GetString();
        var endTimeStr = doc.RootElement.GetProperty("endTime").GetString();

        Assert.NotNull(startTimeStr);
        Assert.NotNull(endTimeStr);
        Assert.Contains("2026-11-24T14:30:00", startTimeStr);
        Assert.Contains("2026-11-24T14:45:00", endTimeStr);
    }
}
