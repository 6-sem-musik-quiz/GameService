using GameService.Repository.State;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace GameService.Repository.Extensions;

public static class RepositoryLayerExtensions
{
    // GameService has no SQL database in the C4 design (only UserDB, StatsDB and MusicDB do).
    // ADR-0004: the running games are kept in Redis (redisConnectionString, env ConnectionStrings__Redis),
    // so GameService can run several pods. Without it (tests, local runs) they are kept in memory and
    // only one instance is supported.
    public static IServiceCollection AddRepositoryLayer(this IServiceCollection services, string? redisConnectionString)
    {
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddSingleton<IGameSessionStore, InMemoryGameSessionStore>();
            services.AddSingleton<IGameLocks, InMemoryGameLocks>();
            services.AddSingleton<IDeadlineQueue, InMemoryDeadlineQueue>();
            return services;
        }

        var options = ConfigurationOptions.Parse(redisConnectionString);
        options.AbortOnConnectFail = false; // start (and serve /health) even when Redis is down
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        services.AddSingleton<IGameSessionStore, RedisGameSessionStore>();
        services.AddSingleton<IGameLocks, RedisGameLocks>();
        services.AddSingleton<RedisDeadlineQueue>();
        services.AddSingleton<IDeadlineQueue>(sp => sp.GetRequiredService<RedisDeadlineQueue>());
        services.AddHostedService<DeadlinePollingService>();
        return services;
    }
}
