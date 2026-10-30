using System.Security.Cryptography;
using System.Text;
using Ascua.Application.Matches;
using StackExchange.Redis;

namespace Ascua.Infrastructure.Matches;

/// <summary>
/// Tickets WS en Redis. Se guarda el SHA-256 del ticket (no el ticket): quien lea Redis no puede
/// usarlos. <c>GETDEL</c> los consume en el handshake, así que no sirven dos veces.
/// </summary>
public sealed class RedisTicketStore(IConnectionMultiplexer redis) : IRealtimeTicketStore
{
    public Task IssueAsync(string ticket, Guid userId, Guid matchId, TimeSpan lifetime, CancellationToken cancellationToken) =>
        redis.GetDatabase().StringSetAsync(Key(ticket), $"{userId:D}|{matchId:D}", lifetime, When.NotExists);

    public async Task<(Guid UserId, Guid MatchId)?> ConsumeAsync(string ticket, CancellationToken cancellationToken)
    {
        var value = await redis.GetDatabase().StringGetDeleteAsync(Key(ticket));
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        var parts = ((string)value!).Split('|');
        return (Guid.Parse(parts[0]), Guid.Parse(parts[1]));
    }

    private static RedisKey Key(string ticket) =>
        $"ascua:ticket:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)))}";
}

/// <summary>
/// Conexiones por usuario como un sorted set de arriendos con vencimiento: las de una réplica que
/// murió sin liberarlas expiran solas, porque solo las réplicas vivas las renuevan.
/// </summary>
public sealed class RedisConnectionLimiter(IConnectionMultiplexer redis) : IConnectionLimiter
{
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(2);

    private const string AcquireScript = """
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[3]) then return 0 end
        redis.call('ZADD', KEYS[1], ARGV[2], ARGV[4])
        redis.call('PEXPIRE', KEYS[1], ARGV[5])
        return 1
        """;

    public async Task<bool> TryAcquireAsync(Guid userId, string leaseId, int max, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var acquired = (int)await redis.GetDatabase().ScriptEvaluateAsync(
            AcquireScript,
            [Key(userId)],
            [now.ToUnixTimeMilliseconds(), (now + LeaseLifetime).ToUnixTimeMilliseconds(), max, leaseId, (long)LeaseLifetime.TotalMilliseconds]);
        return acquired == 1;
    }

    public Task ReleaseAsync(Guid userId, string leaseId, CancellationToken cancellationToken) =>
        redis.GetDatabase().SortedSetRemoveAsync(Key(userId), leaseId);

    public async Task RenewAsync(IEnumerable<(Guid UserId, string LeaseId)> leases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leases);
        var db = redis.GetDatabase();
        var expiry = (DateTimeOffset.UtcNow + LeaseLifetime).ToUnixTimeMilliseconds();
        foreach (var (userId, leaseId) in leases)
        {
            await db.SortedSetAddAsync(Key(userId), leaseId, expiry, SortedSetWhen.Exists);
            await db.KeyExpireAsync(Key(userId), LeaseLifetime);
        }
    }

    private static RedisKey Key(Guid userId) => $"ascua:user:{userId:N}:conns";
}
