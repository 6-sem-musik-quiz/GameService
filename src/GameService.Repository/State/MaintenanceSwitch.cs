using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace GameService.Repository.State;

// SCRUM-179 ("design to be disabled"): whether an admin has switched GameService off. The edge refuses
// public calls to a switched-off service (SCRUM-194), but LobbyService starts a game by calling
// GameService inside the cluster, past the edge. So GameService reads the same flag itself.
// The flag is the field "gameservice" in the Redis hash slopster:maintenance, written by the edge's
// admin API: { "Message": "...", "By": "...", "Since": "..." }. Absent = on.
public interface IMaintenanceSwitch
{
    // null when GameService is on; otherwise the admin's message (may be empty).
    Task<string?> OffMessageAsync(CancellationToken ct = default);
}

// Without Redis (tests, local runs) GameService is always on.
public sealed class AlwaysOnMaintenanceSwitch : IMaintenanceSwitch
{
    public Task<string?> OffMessageAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
}

// Cached for CacheFor, so Redis is not read on every game start. Fails open: if Redis cannot be read,
// GameService counts as on.
public sealed class RedisMaintenanceSwitch(IConnectionMultiplexer redis, TimeProvider time, ILogger<RedisMaintenanceSwitch> logger)
    : IMaintenanceSwitch
{
    public const string Key = "slopster:maintenance";
    public const string Field = "gameservice";
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    private sealed record Flag(string? Message);

    private string? _message;
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    public async Task<string?> OffMessageAsync(CancellationToken ct = default)
    {
        if (time.GetUtcNow() - _readAt < CacheFor) return _message;
        try
        {
            var value = await redis.GetDatabase().HashGetAsync(Key, Field);
            _message = value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<Flag>(value.ToString())?.Message ?? "";
        }
        catch (Exception e) when (e is RedisException or TimeoutException or JsonException)
        {
            logger.LogWarning(e, "Could not read the maintenance flag; treating GameService as on.");
            _message = null;
        }
        _readAt = time.GetUtcNow();
        return _message;
    }
}
