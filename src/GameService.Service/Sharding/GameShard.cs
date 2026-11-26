using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace GameService.Service.Sharding;

// ADR-0004 (SCRUM-129): every game is owned by one GameService pod (StatefulSet gameservice-0..N-1).
//   shard(gameId) = FNV-1a-32(UTF-8(gameId.ToString("D").ToLowerInvariant())) % N
// EdgeService uses the same function to send the game's hub connection to its owner
// (gameservice.gamehub@1.3: ?gameId= on the hub URL).
public static class GameShard
{
    private const uint OffsetBasis = 2166136261;
    private const uint Prime = 16777619;

    public static uint Fnv1a32(Guid gameId)
    {
        var hash = OffsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(gameId.ToString("D").ToLowerInvariant()))
            hash = unchecked((hash ^ b) * Prime);
        return hash;
    }

    public static int Of(Guid gameId, int shardCount) => (int)(Fnv1a32(gameId) % (uint)shardCount);
}

// The "Sharding" section (env Sharding__ShardCount, Sharding__ShardIndex).
public class ShardingOptions
{
    public const string SectionName = "Sharding";

    // N: the number of GameService pods. Must be the same as EdgeService's. 1 = no sharding.
    public int ShardCount { get; set; } = 1;

    // This pod's shard. Null = the last number in HOSTNAME (StatefulSet pod gameservice-2 is shard 2),
    // or 0 with one shard.
    public int? ShardIndex { get; set; }
}

// Creates game ids that this pod owns: draws GUIDs until one hashes to this pod's shard
// (on average ShardCount draws). Whichever pod LobbyService's POST /api/games lands on owns the game.
public sealed class ShardedGameIds
{
    private static readonly Regex TrailingNumber = new(@"-(\d+)$", RegexOptions.Compiled);

    public ShardedGameIds(IOptions<ShardingOptions> options)
        : this(options.Value, Environment.GetEnvironmentVariable("HOSTNAME")) { }

    public ShardedGameIds(ShardingOptions options, string? hostName)
    {
        ShardCount = options.ShardCount;
        if (ShardCount < 1)
            throw new InvalidOperationException($"Sharding:ShardCount must be at least 1, was {ShardCount}.");

        ShardIndex = options.ShardIndex ?? IndexFromHostName(hostName, ShardCount);
        if (ShardIndex < 0 || ShardIndex >= ShardCount)
            throw new InvalidOperationException(
                $"Shard index {ShardIndex} is outside 0..{ShardCount - 1} (Sharding:ShardCount = {ShardCount}).");
    }

    public int ShardCount { get; }
    public int ShardIndex { get; }

    public bool Owns(Guid gameId) => GameShard.Of(gameId, ShardCount) == ShardIndex;

    public Guid NewGameId()
    {
        while (true)
        {
            var id = Guid.NewGuid();
            if (Owns(id)) return id;
        }
    }

    private static int IndexFromHostName(string? hostName, int shardCount)
    {
        if (shardCount == 1) return 0;
        var match = hostName is null ? null : TrailingNumber.Match(hostName);
        if (match is not { Success: true })
            throw new InvalidOperationException(
                $"Sharding:ShardCount is {shardCount}, but the shard index is not set and HOSTNAME '{hostName}' " +
                "does not end in -<number> (StatefulSet pod name). Set Sharding__ShardIndex.");
        return int.Parse(match.Groups[1].Value);
    }
}
