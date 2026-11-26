using System.Globalization;
using System.Text.RegularExpressions;
using GameService.Domain.Models;
using GameService.Service.Interfaces;
using Microsoft.Extensions.Options;

namespace GameService.Service.Sharding;

// ADR-0004 (SCRUM-129, gameservice.gamehub@1.4): every game is owned by one GameService pod
// (StatefulSet gameservice-0..N-1), and the owner is written in the gameId: the first two hex
// characters of gameId.ToString("D") are the pod's number (02a7f3c1-... = gameservice-2).
// The pod writes them when it creates the game; EdgeService reads them to route the game's traffic.
// The number of pods is not needed anywhere, so pods can be added without moving any game.
public static class GameShard
{
    public const int MaxShards = 256; // two hex characters

    public static int Of(Guid gameId) =>
        int.Parse(gameId.ToString("D").AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    // A random GUID whose first two hex characters are `shard`.
    public static Guid NewGameId(int shard)
    {
        if (shard is < 0 or >= MaxShards)
            throw new ArgumentOutOfRangeException(nameof(shard), shard, $"A shard is 0..{MaxShards - 1}.");
        var random = Guid.NewGuid().ToString("D");
        return Guid.Parse(shard.ToString("x2", CultureInfo.InvariantCulture) + random[2..]);
    }
}

// The "Sharding" section (env Sharding__ShardIndex).
public class ShardingOptions
{
    public const string SectionName = "Sharding";

    // This pod's number. Null = the last number in HOSTNAME (StatefulSet pod gameservice-2 is 2),
    // or 0 when the host name does not end in -<number> (local run, single Deployment).
    public int? ShardIndex { get; set; }
}

// This pod's shard, and the ids of the games it creates.
public sealed class ShardedGameIds
{
    private static readonly Regex TrailingNumber = new(@"-(\d+)$", RegexOptions.Compiled);

    public ShardedGameIds(IOptions<ShardingOptions> options)
        : this(options.Value, Environment.GetEnvironmentVariable("HOSTNAME")) { }

    public ShardedGameIds(ShardingOptions options, string? hostName)
    {
        ShardIndex = options.ShardIndex ?? IndexFromHostName(hostName);
        if (ShardIndex is < 0 or >= GameShard.MaxShards)
            throw new InvalidOperationException(
                $"Shard index {ShardIndex} is outside 0..{GameShard.MaxShards - 1} (two hex characters in the gameId).");
    }

    public int ShardIndex { get; }

    public bool Owns(Guid gameId) => GameShard.Of(gameId) == ShardIndex;

    public Guid NewGameId() => GameShard.NewGameId(ShardIndex);

    private static int IndexFromHostName(string? hostName)
    {
        var match = hostName is null ? null : TrailingNumber.Match(hostName);
        return match is { Success: true } && int.TryParse(match.Groups[1].Value, out var index) ? index : 0;
    }
}

// ADR-0004: before Kubernetes stops this pod (scale-down or deploy), preStop calls /internal/drain.
// From then on the pod is not ready (/health/ready fails, so the Service sends new games to the other
// pods), rejects new games, and the drain call returns once no game here is Waiting or Playing.
public sealed class GameDrain(IGameStore store, TimeProvider time)
{
    private volatile bool _draining;

    public bool IsDraining => _draining;

    public int ActiveGames => store.CountByStatus(GameStatus.Waiting) + store.CountByStatus(GameStatus.Playing);

    // Starts draining and waits until no game is active, checking every `poll`.
    public async Task DrainAsync(TimeSpan poll, CancellationToken cancellationToken)
    {
        _draining = true;
        while (ActiveGames > 0)
            await Task.Delay(poll, time, cancellationToken);
    }
}
