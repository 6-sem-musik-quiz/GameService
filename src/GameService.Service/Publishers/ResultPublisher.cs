using System.Text;
using System.Text.Json;
using GameService.Domain.Events;
using GameService.Service.Interfaces;
using GameService.Service.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace GameService.Service.Publishers;

public class ResultPublisher : IResultPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly RabbitMqOptions _options;
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<ResultPublisher> _logger;
    private IConnection? _connection;
    private IChannel? _channel;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public ResultPublisher(
        IOptions<RabbitMqOptions> options,
        ILogger<ResultPublisher> logger,
        IConnectionFactory? connectionFactory = null)
    {
        _options = options.Value;
        _logger = logger;
        _connectionFactory = connectionFactory ?? new ConnectionFactory
        {
            HostName = _options.Host,
            Port = _options.Port,
            UserName = _options.Username,
            Password = _options.Password,
            VirtualHost = _options.VirtualHost
        };
    }

    public async Task PublishGameFinishedAsync(GameFinishedEvent gameFinished, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameFinished);

        var channel = await GetOrCreateChannelAsync(cancellationToken);

        await channel.ExchangeDeclareAsync(
            exchange: _options.ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        var json = SerializeGameFinished(gameFinished);
        var body = Encoding.UTF8.GetBytes(json);

        var properties = new BasicProperties
        {
            DeliveryMode = DeliveryModes.Persistent,
            ContentType = "application/json",
            Type = "game.finished.v1"
        };

        await channel.BasicPublishAsync(
            exchange: _options.ExchangeName,
            routingKey: _options.RoutingKey,
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Published GameFinished event for game {GameId} to exchange {Exchange} with routing key {RoutingKey}",
            gameFinished.GameId, _options.ExchangeName, _options.RoutingKey);
    }

    public static string SerializeGameFinished(GameFinishedEvent gameFinished)
    {
        return JsonSerializer.Serialize(gameFinished, JsonOptions);
    }

    private async Task<IChannel> GetOrCreateChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            if (_connection is not { IsOpen: true })
            {
                _connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
            }

            _channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
            return _channel;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.CloseAsync();
            _channel.Dispose();
        }

        if (_connection is not null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
        }

        _semaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
