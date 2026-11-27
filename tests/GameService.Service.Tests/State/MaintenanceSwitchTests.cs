using GameService.Repository.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace GameService.Service.Tests.State;

// SCRUM-179: GameService reads the edge's maintenance flag (Redis hash slopster:maintenance) itself.
public sealed class MaintenanceSwitchTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("redis:7-alpine").Build();
    private IConnectionMultiplexer _mux = null!;

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _mux = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _mux.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task ReadsTheEdgesFlag_AndPicksUpAChangeAfterTheCache()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = new RedisMaintenanceSwitch(_mux, time, NullLogger<RedisMaintenanceSwitch>.Instance);
        Assert.Null(await sut.OffMessageAsync()); // on

        // What the edge writes when an admin switches GameService (and here also LobbyService) off.
        var db = _mux.GetDatabase();
        await db.HashSetAsync("slopster:maintenance", "gameservice",
            """{"Message":"Back at 20:00","By":"asger","Since":"2026-11-27T15:00:00+00:00"}""");
        await db.HashSetAsync("slopster:maintenance", "lobbyservice",
            """{"Message":null,"By":"asger","Since":"2026-11-27T15:00:00+00:00"}""");

        Assert.Null(await sut.OffMessageAsync()); // still cached
        time.Advance(RedisMaintenanceSwitch.CacheFor);
        Assert.Equal("Back at 20:00", await sut.OffMessageAsync());

        await db.HashDeleteAsync("slopster:maintenance", "gameservice");
        time.Advance(RedisMaintenanceSwitch.CacheFor);
        Assert.Null(await sut.OffMessageAsync()); // LobbyService being off does not matter here
    }

    [Fact]
    public async Task UnreachableRedis_FailsOpen()
    {
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 200;
        options.AsyncTimeout = 200;
        await using var dead = await ConnectionMultiplexer.ConnectAsync(options);

        var sut = new RedisMaintenanceSwitch(dead, TimeProvider.System, NullLogger<RedisMaintenanceSwitch>.Instance);

        Assert.Null(await sut.OffMessageAsync());
    }
}
