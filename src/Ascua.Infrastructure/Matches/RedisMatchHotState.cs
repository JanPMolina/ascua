using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ascua.Application.Matches;
using Ascua.Domain.Model;
using StackExchange.Redis;
using Order = Ascua.Domain.Model.Order;

namespace Ascua.Infrastructure.Matches;

/// <summary>
/// Estado caliente en Redis. Las claves de una partida llevan la etiqueta <c>{m:id}</c> para caer en el
/// mismo slot si algún día se usa Redis en clúster; los scripts Lua solo tocan claves de una partida.
/// </summary>
public sealed class RedisMatchHotState(IConnectionMultiplexer redis, RealtimeOptions options) : IMatchHotState
{
    private const string DeadlinesKey = "ascua:deadlines";
    private const string GracesKey = "ascua:graces";

    /// <summary>Lo caliente expira solo; Postgres es la fuente de verdad si hace falta reconstruir.</summary>
    private static readonly TimeSpan HotTtl = TimeSpan.FromHours(2);

    private const string SaveScript = """
        local current = tonumber(redis.call('HGET', KEYS[1], 'v') or '-1')
        if tonumber(ARGV[1]) > current then
          redis.call('HSET', KEYS[1], 'v', ARGV[1], 'd', ARGV[2])
        end
        redis.call('EXPIRE', KEYS[1], ARGV[3])
        return 1
        """;

    // Idempotencia (clientActionId), ronda sellada por el cerrador y una sola orden por jugador, todo atómico.
    private const string SubmitScript = """
        local previous = redis.call('GET', KEYS[1])
        if previous then return {1, previous} end
        if redis.call('EXISTS', KEYS[3]) == 1 then return {3, ''} end
        if redis.call('HSETNX', KEYS[2], ARGV[1], ARGV[2]) == 0 then return {2, ''} end
        redis.call('EXPIRE', KEYS[2], ARGV[4])
        redis.call('SET', KEYS[1], ARGV[3], 'EX', ARGV[4])
        return {0, tostring(redis.call('HLEN', KEYS[2]))}
        """;

    private const string SealScript = """
        redis.call('SET', KEYS[1], '1', 'EX', ARGV[1])
        return redis.call('HGETALL', KEYS[2])
        """;

    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private const string ConnectScript = """
        local previous = redis.call('HGET', KEYS[1], ARGV[1])
        redis.call('HSET', KEYS[1], ARGV[1], ARGV[2])
        redis.call('EXPIRE', KEYS[1], ARGV[3])
        return previous or ''
        """;

    private const string DisconnectScript = """
        if redis.call('HGET', KEYS[1], ARGV[1]) == ARGV[2] then
          redis.call('HDEL', KEYS[1], ARGV[1])
          return 1
        end
        return 0
        """;

    private IDatabase Db => redis.GetDatabase();

    public async Task<MatchSnapshot?> GetAsync(Guid matchId, CancellationToken cancellationToken)
    {
        var json = await Db.HashGetAsync(Key(matchId, "snap"), "d");
        if (json.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return MatchJson.DeserializeSnapshot(json!);
        }
        catch (JsonException)
        {
            // Instantánea de otra versión (héroes viejos, ver ADR-0009): Redis no es la fuente de verdad.
            return null;
        }
    }

    public async Task SaveAsync(MatchSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await Db.ScriptEvaluateAsync(
            SaveScript,
            [Key(snapshot.MatchId, "snap")],
            [snapshot.Version, MatchJson.Serialize(snapshot), (long)HotTtl.TotalSeconds]);

        if (snapshot.Status.IsActive() && snapshot.WakeAt is { } deadline)
        {
            await Db.SortedSetAddAsync(DeadlinesKey, Member(snapshot.MatchId), deadline.ToUnixTimeMilliseconds());
        }
        else
        {
            await Db.SortedSetRemoveAsync(DeadlinesKey, Member(snapshot.MatchId));
        }
    }

    public async Task<IAsyncDisposable?> TryLockAsync(Guid matchId, TimeSpan wait, CancellationToken cancellationToken)
    {
        var key = Key(matchId, "lock");
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var giveUpAt = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            // SET match:{id}:lock {token} NX PX 5000: un solo cerrador aunque haya varias réplicas.
            if (await Db.StringSetAsync(key, token, options.LockLifetime, When.NotExists))
            {
                return new Lease(Db, key, token);
            }

            if (DateTimeOffset.UtcNow >= giveUpAt)
            {
                return null;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    public async Task<OrderAck?> FindAckAsync(Guid matchId, Guid clientActionId, CancellationToken cancellationToken)
    {
        var json = await Db.StringGetAsync(Key(matchId, $"act:{clientActionId:N}"));
        return json.IsNullOrEmpty ? null : MatchJson.Deserialize<OrderAck>(json!);
    }

    public async Task<SubmitResult> SubmitOrderAsync(OrderAck ack, PlayerId player, Order order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ack);
        ArgumentNullException.ThrowIfNull(order);
        var result = (RedisResult[])(await Db.ScriptEvaluateAsync(
            SubmitScript,
            [Key(ack.MatchId, $"act:{ack.ClientActionId:N}"), OrdersKey(ack.MatchId, ack.Round), SealedKey(ack.MatchId, ack.Round)],
            [player.Value, EncodeOrder(order), MatchJson.Serialize(ack), (long)HotTtl.TotalSeconds]))!;

        var code = (int)result[0];
        var payload = (string?)result[1];
        return code switch
        {
            0 => new SubmitResult(SubmitStatus.Accepted, ack, int.Parse(payload!, CultureInfo.InvariantCulture)),
            1 => new SubmitResult(SubmitStatus.Duplicate, MatchJson.Deserialize<OrderAck>(payload!), 0),
            2 => new SubmitResult(SubmitStatus.AlreadySubmitted, null, 0),
            _ => new SubmitResult(SubmitStatus.RoundClosed, null, 0),
        };
    }

    public async Task<IReadOnlyDictionary<PlayerId, Order>> SealOrdersAsync(Guid matchId, int round, CancellationToken cancellationToken)
    {
        var result = (RedisResult[])(await Db.ScriptEvaluateAsync(
            SealScript,
            [SealedKey(matchId, round), OrdersKey(matchId, round)],
            [(long)HotTtl.TotalSeconds]))!;

        var orders = new Dictionary<PlayerId, Order>();
        for (var i = 0; i + 1 < result.Length; i += 2)
        {
            orders[new PlayerId((string)result[i]!)] = DecodeOrder((string)result[i + 1]!);
        }

        return orders;
    }

    public async Task<IReadOnlyDictionary<PlayerId, Order>> GetOrdersAsync(Guid matchId, int round, CancellationToken cancellationToken)
    {
        var entries = await Db.HashGetAllAsync(OrdersKey(matchId, round));
        return entries.ToDictionary(e => new PlayerId(e.Name!), e => DecodeOrder(e.Value!));
    }

    public Task<bool> TryTakeSignalSlotAsync(Guid matchId, Guid userId, TimeSpan interval, CancellationToken cancellationToken) =>
        Db.StringSetAsync(Key(matchId, $"signal:{userId:N}"), "1", interval, When.NotExists);

    public async Task<DraftAck?> FindDraftAckAsync(Guid matchId, Guid userId, Guid offerId, CancellationToken cancellationToken)
    {
        var json = await Db.StringGetAsync(Key(matchId, $"draft:{userId:N}:{offerId:N}"));
        return json.IsNullOrEmpty ? null : MatchJson.Deserialize<DraftAck>(json!);
    }

    public Task SaveDraftAckAsync(Guid userId, DraftAck ack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ack);
        return Db.StringSetAsync(Key(ack.MatchId, $"draft:{userId:N}:{ack.OfferId:N}"), MatchJson.Serialize(ack), HotTtl);
    }

    public async Task<PathAck?> FindPathAckAsync(Guid matchId, Guid userId, Guid offerId, CancellationToken cancellationToken)
    {
        var json = await Db.StringGetAsync(Key(matchId, $"path:{userId:N}:{offerId:N}"));
        return json.IsNullOrEmpty ? null : MatchJson.Deserialize<PathAck>(json!);
    }

    public Task SavePathAckAsync(Guid userId, PathAck ack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ack);
        return Db.StringSetAsync(Key(ack.MatchId, $"path:{userId:N}:{ack.OfferId:N}"), MatchJson.Serialize(ack), HotTtl);
    }

    public async Task<IReadOnlyList<Guid>> DueDeadlinesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var members = await Db.SortedSetRangeByScoreAsync(DeadlinesKey, double.NegativeInfinity, now.ToUnixTimeMilliseconds(), take: limit);
        return [.. members.Select(m => Guid.Parse((string)m!))];
    }

    public Task RemoveDeadlineAsync(Guid matchId, CancellationToken cancellationToken) =>
        Db.SortedSetRemoveAsync(DeadlinesKey, Member(matchId));

    public Task<long> ScheduledMatchCountAsync(CancellationToken cancellationToken) => Db.SortedSetLengthAsync(DeadlinesKey);

    public async Task<(string? PreviousConnectionId, bool WasAway)> MarkConnectedAsync(
        Guid matchId, Guid userId, string connectionId, CancellationToken cancellationToken)
    {
        var previous = (string?)await Db.ScriptEvaluateAsync(
            ConnectScript,
            [Key(matchId, "presence")],
            [Member(userId), connectionId, (long)HotTtl.TotalSeconds]);
        var graceCleared = await Db.SortedSetRemoveAsync(GracesKey, GraceMember(matchId, userId));
        var hadConnection = !string.IsNullOrEmpty(previous);
        return (hadConnection ? previous : null, graceCleared || !hadConnection);
    }

    public async Task<bool> MarkDisconnectedAsync(
        Guid matchId, Guid userId, string connectionId, DateTimeOffset graceDeadline, CancellationToken cancellationToken)
    {
        var removed = (int)await Db.ScriptEvaluateAsync(DisconnectScript, [Key(matchId, "presence")], [Member(userId), connectionId]);
        if (removed == 0)
        {
            return false;
        }

        await ScheduleGraceAsync(matchId, userId, graceDeadline, cancellationToken);
        return true;
    }

    public Task ScheduleGraceAsync(Guid matchId, Guid userId, DateTimeOffset deadline, CancellationToken cancellationToken) =>
        Db.SortedSetAddAsync(GracesKey, GraceMember(matchId, userId), deadline.ToUnixTimeMilliseconds());

    public async Task<IReadOnlySet<Guid>> ConnectedUsersAsync(Guid matchId, CancellationToken cancellationToken)
    {
        var users = await Db.HashKeysAsync(Key(matchId, "presence"));
        return users.Select(u => Guid.Parse((string)u!)).ToHashSet();
    }

    public async Task<IReadOnlyList<(Guid MatchId, Guid UserId)>> ClaimDueGracesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        var members = await Db.SortedSetRangeByScoreAsync(GracesKey, double.NegativeInfinity, now.ToUnixTimeMilliseconds(), take: limit);
        var claimed = new List<(Guid, Guid)>();
        foreach (var member in members)
        {
            // ZREM es el reclamo: si otra réplica lo quitó antes, devuelve false y no se procesa dos veces.
            if (await Db.SortedSetRemoveAsync(GracesKey, member))
            {
                var parts = ((string)member!).Split('|');
                claimed.Add((Guid.Parse(parts[0]), Guid.Parse(parts[1])));
            }
        }

        return claimed;
    }

    private static RedisKey Key(Guid matchId, string suffix) => $"ascua:{{m:{matchId:N}}}:{suffix}";

    private static RedisKey OrdersKey(Guid matchId, int round) => Key(matchId, $"r:{round}:orders");

    private static RedisKey SealedKey(Guid matchId, int round) => Key(matchId, $"r:{round}:sealed");

    private static string Member(Guid id) => id.ToString("D");

    private static string GraceMember(Guid matchId, Guid userId) => $"{matchId:D}|{userId:D}";

    private static string EncodeOrder(Order order) => $"{order.Type}|{order.Direction}";

    private static Order DecodeOrder(string value)
    {
        var parts = value.Split('|');
        Direction? direction = parts.Length > 1 && parts[1].Length > 0 ? Enum.Parse<Direction>(parts[1]) : null;
        return new Order(Enum.Parse<OrderType>(parts[0]), direction);
    }

    private sealed class Lease(IDatabase db, RedisKey key, string token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await db.ScriptEvaluateAsync(ReleaseScript, [key], [token]);
    }
}
