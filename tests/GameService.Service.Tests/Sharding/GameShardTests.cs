using GameService.Service.Sharding;

namespace GameService.Service.Tests.Sharding;

// ADR-0004: shard(gameId) = FNV-1a-32(lowercase "D" string) % N. EdgeService must get the same numbers.
public class GameShardTests
{
    // Reference values computed independently (Python) from the ADR's definition.
    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", 3192360657u)]
    [InlineData("3F1C2A8E-0000-4000-8000-000000000001", 2145607093u)] // upper case input hashes as lower case
    public void Fnv1a32_MatchesTheContract(string gameId, uint expected) =>
        Assert.Equal(expected, GameShard.Fnv1a32(Guid.Parse(gameId)));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Of_IsBetweenZeroAndShardCount(int shardCount)
    {
        for (var i = 0; i < 500; i++)
            Assert.InRange(GameShard.Of(Guid.NewGuid(), shardCount), 0, shardCount - 1);
    }

    [Fact]
    public void Of_SpreadsGamesEvenly()
    {
        var counts = new int[4];
        for (var i = 0; i < 8000; i++)
            counts[GameShard.Of(Guid.NewGuid(), 4)]++;

        Assert.All(counts, c => Assert.InRange(c, 1700, 2300)); // 2000 each, expected
    }

    [Theory]
    [InlineData(3, "gameservice-0", 0)]
    [InlineData(3, "gameservice-2", 2)]
    [InlineData(1, null, 0)]
    [InlineData(1, "my-laptop", 0)]
    public void ShardIndex_ComesFromThePodName(int count, string? hostName, int expected) =>
        Assert.Equal(expected, new ShardedGameIds(new ShardingOptions { ShardCount = count }, hostName).ShardIndex);

    [Fact]
    public void ShardIndex_FromConfiguration_WinsOverThePodName() =>
        Assert.Equal(1, new ShardedGameIds(new ShardingOptions { ShardCount = 3, ShardIndex = 1 }, "gameservice-2").ShardIndex);

    [Theory]
    [InlineData(3, null, null)]           // several shards, no index anywhere
    [InlineData(3, "my-laptop", null)]    // host name without a number
    [InlineData(3, "gameservice-3", null)] // pod outside 0..N-1
    [InlineData(2, null, -1)]
    [InlineData(0, null, null)]
    public void InvalidSettings_FailAtStartup(int count, string? hostName, int? index) =>
        Assert.Throws<InvalidOperationException>(() =>
            new ShardedGameIds(new ShardingOptions { ShardCount = count, ShardIndex = index }, hostName));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NewGameId_IsAlwaysOwnedByThisPod(int index)
    {
        var ids = new ShardedGameIds(new ShardingOptions { ShardCount = 3, ShardIndex = index }, null);

        for (var i = 0; i < 200; i++)
        {
            var id = ids.NewGameId();
            Assert.Equal(index, GameShard.Of(id, 3));
            Assert.True(ids.Owns(id));
        }
    }
}
