using System.Text.Json;
using GameService.Domain.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace GameService.Repository.State;

// ADR-0004: the games in Redis (the same Redis as LobbyService, redis-master in the cluster), so every
// GameService pod can serve every game. Keys:
//   slopster:game:{id}        the GameSession as JSON. 30 minutes after it finished, else 3 hours.
//   slopster:game:{id}:lock   the game's lock (SET NX PX), value = the holder's token.
//   slopster:games:playing    sorted set of Playing games, score = last save (ms), for the gauge.
//   slopster:deadlines        sorted set of "{gameId}|{token}", score = due time (ms).
public static class RedisKeys
{
    public const string Prefix = "slopster:";
    public static RedisKey Game(Guid id) => $"{Prefix}game:{id:N}";
    public static RedisKey Lock(Guid id) => $"{Prefix}game:{id:N}:lock";
    public static readonly RedisKey Playing = $"{Prefix}games:playing";
    public static readonly RedisKey Deadlines = $"{Prefix}deadlines";
}

public sealed class RedisGameSessionStore(IConnectionMultiplexer redis, TimeProvider time) : IGameSessionStore
{
    public static readonly TimeSpan FinishedRetention = TimeSpan.FromMinutes(30);
    // A game that is never finished (e.g. every player left) is forgotten after this.
    public static readonly TimeSpan ActiveRetention = TimeSpan.FromHours(3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private IDatabase Db => redis.GetDatabase();

    public async Task<GameSession?> GetAsync(Guid gameId)
    {
        var json = await Db.StringGetAsync(RedisKeys.Game(gameId));
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<GameSession>((string)json!, Json);
    }

    public async Task SaveAsync(GameSession session)
    {
        var game = session.Game;
        var finished = game.Status == GameStatus.Finished;
        var json = JsonSerializer.Serialize(session, Json);

        var batch = Db.CreateBatch();
        var save = batch.StringSetAsync(RedisKeys.Game(game.Id), json, finished ? FinishedRetention : ActiveRetention);
        var playing = game.Status == GameStatus.Playing
            ? batch.SortedSetAddAsync(RedisKeys.Playing, game.Id.ToString("N"), time.GetUtcNow().ToUnixTimeMilliseconds())
            : batch.SortedSetRemoveAsync(RedisKeys.Playing, game.Id.ToString("N"));
        batch.Execute();
        await Task.WhenAll(save, playing);
    }

    // Entries older than ActiveRetention belong to games that were abandoned while Playing.
    public long CountPlaying() =>
        Db.SortedSetLength(RedisKeys.Playing,
            (time.GetUtcNow() - ActiveRetention).ToUnixTimeMilliseconds(), double.PositiveInfinity);
}

public sealed class RedisGameLocks(IConnectionMultiplexer redis) : IGameLocks
{
    // Longer than anything done under the lock (a RabbitMQ publish may take up to 10 s).
    public static readonly TimeSpan LeaseTime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan WaitTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(10);

    // Deletes the lock only if we still hold it (it may have expired and been taken by another pod).
    private const string ReleaseScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    public async Task<IAsyncDisposable> AcquireAsync(Guid gameId)
    {
        var db = redis.GetDatabase();
        var key = RedisKeys.Lock(gameId);
        var token = Guid.NewGuid().ToString("N");
        var giveUp = DateTime.UtcNow + WaitTime;

        while (!await db.StringSetAsync(key, token, LeaseTime, When.NotExists))
        {
            if (DateTime.UtcNow > giveUp)
                throw new TimeoutException($"Could not lock game {gameId} within {WaitTime.TotalSeconds} s.");
            await Task.Delay(Retry);
        }
        return new Releaser(db, key, token);
    }

    private sealed class Releaser(IDatabase db, RedisKey key, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await db.ScriptEvaluateAsync(ReleaseScript, [key], [token]);
    }
}

// Deadlines in a sorted set that every pod polls. ZREM decides who handles a due deadline: only the
// pod whose ZREM removed it (returns true) calls the handler, so each deadline runs once.
public sealed class RedisDeadlineQueue(IConnectionMultiplexer redis, TimeProvider time, ILogger<RedisDeadlineQueue> logger)
    : IDeadlineQueue
{
    private Func<Guid, string, Task>? _handler;

    public void SetHandler(Func<Guid, string, Task> handler) => _handler = handler;

    public Task ScheduleAsync(Guid gameId, string token, DateTimeOffset dueAt) =>
        redis.GetDatabase().SortedSetAddAsync(RedisKeys.Deadlines, $"{gameId:N}|{token}", dueAt.ToUnixTimeMilliseconds());

    // Claims and runs the deadlines that are due now. Claimed = how many this pod handled; Pending = whether
    // any deadline is left (due or not), so the poller can slow down when no game is running.
    // Costs one read (the earliest deadline) when nothing is due, two when something is.
    public async Task<PollResult> PollAsync()
    {
        if (_handler is not { } handler) return new PollResult(0, false);
        var db = redis.GetDatabase();
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();

        var earliest = await db.SortedSetRangeByRankWithScoresAsync(RedisKeys.Deadlines, 0, 0);
        if (earliest.Length == 0)
            return new PollResult(0, false); // no game is waiting for anything
        if (earliest[0].Score > now)
            return new PollResult(0, true);  // something is coming, nothing is due yet

        var due = await db.SortedSetRangeByScoreAsync(RedisKeys.Deadlines, double.NegativeInfinity, now, take: 50);

        var claimed = new List<Task>();
        foreach (var member in due)
        {
            if (!await db.SortedSetRemoveAsync(RedisKeys.Deadlines, member))
                continue; // another pod got it
            var parts = ((string)member!).Split('|');
            claimed.Add(RunAsync(handler, Guid.Parse(parts[0]), parts[1]));
        }
        await Task.WhenAll(claimed);
        return new PollResult(claimed.Count, true);
    }

    private async Task RunAsync(Func<Guid, string, Task> handler, Guid gameId, string token)
    {
        try
        {
            await handler(gameId, token);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Deadline {Token} for game {GameId} failed", token, gameId);
        }
    }
}

public readonly record struct PollResult(int Claimed, bool Pending);

// Polls the deadlines in every pod: every 100 ms while there are deadlines (a game is running), every
// second while there are none. Deadlines are at least a second apart in a game, so 100 ms is precise
// enough (a round may end up to that much after its EndsAt + grace). When idle, the first deadline of a
// new game (the 15 s join timeout) may come up to a second late, which does not matter; Redis then
// sees about 1 read per second per pod instead of 10.
public sealed class DeadlinePollingService(RedisDeadlineQueue queue, ILogger<DeadlinePollingService> logger)
    : BackgroundService
{
    public static readonly TimeSpan BusyInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(1);

    public static TimeSpan NextDelay(PollResult result) => result.Pending ? BusyInterval : IdleInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = IdleInterval;
            try
            {
                delay = NextDelay(await queue.PollAsync());
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Polling the game deadlines in Redis failed; trying again");
            }
            await Task.Delay(delay, stoppingToken);
        }
    }
}
