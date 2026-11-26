using GameService.Service.Clients;
using GameService.Service.Games;
using GameService.Service.Interfaces;
using GameService.Service.Metrics;
using GameService.Service.Options;
using GameService.Service.Publishers;
using GameService.Service.Sharding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameService.Service.Extensions;

public static class ServiceLayerExtensions
{
    public static IServiceCollection AddServiceLayer(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(RabbitMqOptions.SectionName);
        services.Configure<RabbitMqOptions>(options =>
        {
            var host = section["Host"];
            if (!string.IsNullOrWhiteSpace(host)) options.Host = host;

            if (int.TryParse(section["Port"], out var port)) options.Port = port;

            var username = section["Username"];
            if (!string.IsNullOrWhiteSpace(username)) options.Username = username;

            var password = section["Password"];
            if (!string.IsNullOrWhiteSpace(password)) options.Password = password;

            var virtualHost = section["VirtualHost"];
            if (!string.IsNullOrWhiteSpace(virtualHost)) options.VirtualHost = virtualHost;

            var exchange = section["ExchangeName"];
            if (!string.IsNullOrWhiteSpace(exchange)) options.ExchangeName = exchange;

            var routingKey = section["RoutingKey"];
            if (!string.IsNullOrWhiteSpace(routingKey)) options.RoutingKey = routingKey;
        });

        services.AddSingleton<IResultPublisher, ResultPublisher>();

        var game = configuration.GetSection(GameOptions.SectionName);
        services.Configure<GameOptions>(options =>
        {
            if (int.TryParse(game["JoinTimeoutSeconds"], out var join)) options.JoinTimeoutSeconds = join;
            if (int.TryParse(game["ReadyTimeoutSeconds"], out var ready)) options.ReadyTimeoutSeconds = ready;
            if (int.TryParse(game["StartDelayMs"], out var delay)) options.StartDelayMs = delay;
            if (int.TryParse(game["PrefetchWindow"], out var window)) options.PrefetchWindow = window;
            if (int.TryParse(game["AnswerGraceSeconds"], out var grace)) options.AnswerGraceSeconds = grace;
            if (int.TryParse(game["PauseBetweenRoundsSeconds"], out var pause)) options.PauseBetweenRoundsSeconds = pause;
        });

        var sharding = configuration.GetSection(ShardingOptions.SectionName);
        services.Configure<ShardingOptions>(options =>
        {
            if (int.TryParse(sharding["ShardCount"], out var count)) options.ShardCount = count;
            if (int.TryParse(sharding["ShardIndex"], out var index)) options.ShardIndex = index;
        });
        services.AddSingleton<ShardedGameIds>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IGameStore, InMemoryGameStore>();
        services.AddSingleton<GameMetrics>();
        // Needs an IGameNotifier, which the Api project registers (SignalR).
        services.AddSingleton<IGameCoordinator, GameCoordinator>();
        services.AddScoped<IGameEngine, GameEngine>();
        services.AddHttpClient<IMusicServiceClient, MusicServiceClient>((sp, client) =>
        {
            var url = sp.GetRequiredService<IConfiguration>()["Services:MusicService"];
            if (!string.IsNullOrWhiteSpace(url))
                client.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        });

        return services;
    }
}
