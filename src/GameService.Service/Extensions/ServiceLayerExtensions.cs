using GameService.Service.Clients;
using GameService.Service.Games;
using GameService.Service.Interfaces;
using GameService.Service.Options;
using GameService.Service.Publishers;
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

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IGameStore, InMemoryGameStore>();
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
