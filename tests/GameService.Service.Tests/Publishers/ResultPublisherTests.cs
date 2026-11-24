using GameService.Domain.Events;
using GameService.Service.Options;
using GameService.Service.Publishers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace GameService.Service.Tests.Publishers;

public class ResultPublisherTests
{
    private readonly IConnectionFactory _connectionFactory = Substitute.For<IConnectionFactory>();
    private readonly IConnection _connection = Substitute.For<IConnection>();
    private readonly IChannel _channel = Substitute.For<IChannel>();
    private readonly RabbitMqOptions _options = new()
    {
        Host = "test-host",
        Port = 5672,
        Username = "user",
        Password = "pwd",
        VirtualHost = "/",
        ExchangeName = "game-events",
        RoutingKey = "game.finished.v1"
    };

    public ResultPublisherTests()
    {
        _connectionFactory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(_connection);
        _connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(_channel);
        _connection.IsOpen.Returns(true);
        _channel.IsOpen.Returns(true);
    }

    [Fact]
    public async Task PublishGameFinishedAsync_Throws_WhenEventIsNull()
    {
        // Arrange
        var publisher = new ResultPublisher(
            Microsoft.Extensions.Options.Options.Create(_options),
            NullLogger<ResultPublisher>.Instance,
            _connectionFactory);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.PublishGameFinishedAsync(null!));
    }

    [Fact]
    public async Task PublishGameFinishedAsync_PublishesToCorrectExchangeAndRoutingKey_WithPersistentMode()
    {
        // Arrange
        var publisher = new ResultPublisher(
            Microsoft.Extensions.Options.Options.Create(_options),
            NullLogger<ResultPublisher>.Instance,
            _connectionFactory);

        var gameEvent = new GameFinishedEvent
        {
            GameId = Guid.NewGuid(),
            Rounds = 5,
            StartTime = DateTime.UtcNow.AddMinutes(-5),
            EndTime = DateTime.UtcNow,
            CatId = 1,
            CategoryName = "Pop",
            Players =
            [
                new PlayerGameResult
                {
                    UserId = 10,
                    UserName = "Spiller1",
                    Score = 300,
                    CorrectAnswers = 3,
                    AnsweredRounds = 5,
                    Placement = 1
                }
            ]
        };

        // Act
        await publisher.PublishGameFinishedAsync(gameEvent);

        // Assert - exchange declaration
        await _channel.Received(1).ExchangeDeclareAsync(
            exchange: "game-events",
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: Arg.Any<CancellationToken>());

        // Assert - publish call
        await _channel.Received(1).BasicPublishAsync(
            exchange: "game-events",
            routingKey: "game.finished.v1",
            mandatory: false,
            basicProperties: Arg.Is<BasicProperties>(p =>
                p.DeliveryMode == DeliveryModes.Persistent &&
                p.ContentType == "application/json" &&
                p.Type == "game.finished.v1"),
            body: Arg.Is<ReadOnlyMemory<byte>>(b => b.Length > 0),
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public void RabbitMqOptions_HasDefaultValues_MatchingAcceptanceCriteria()
    {
        // Arrange & Act
        var options = new RabbitMqOptions();

        // Assert
        Assert.Equal("localhost", options.Host);
        Assert.Equal(5672, options.Port);
        Assert.Equal("guest", options.Username);
        Assert.Equal("guest", options.Password);
        Assert.Equal("game-events", options.ExchangeName);
        Assert.Equal("game.finished.v1", options.RoutingKey);
    }
}
