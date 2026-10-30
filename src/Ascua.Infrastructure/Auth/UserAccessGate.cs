using Ascua.Application.Admin;
using Ascua.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Ascua.Infrastructure.Auth;

/// <summary>
/// Estado "suspendido" por usuario en Redis (<c>ascua:user:{id:N}:access</c> = <c>1</c>/<c>0</c>, TTL corto) con
/// respaldo en Postgres. Se consulta al validar cada JWT; al suspender o rehabilitar se escribe de inmediato,
/// así que el cambio vale en la siguiente petición en todas las réplicas. Si Redis falla se consulta
/// Postgres directamente (nunca se deja pasar sin saber).
/// </summary>
public sealed partial class RedisUserAccessGate(
    IConnectionMultiplexer redis,
    IServiceScopeFactory scopes,
    ILogger<RedisUserAccessGate> logger) : IUserAccessGate
{
    /// <summary>Cuánto se recuerda el estado leído de Postgres; los cambios del admin se escriben sin esperar.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    public async Task<bool> IsSuspendedAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var cached = await redis.GetDatabase().StringGetAsync(Key(userId));
            if (!cached.IsNullOrEmpty)
            {
                return cached == "1";
            }
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(ex);
            return await LoadAsync(userId, cancellationToken);
        }

        var suspended = await LoadAsync(userId, cancellationToken);
        await TryWriteAsync(userId, suspended, When.NotExists);
        return suspended;
    }

    public async Task SetSuspendedAsync(Guid userId, bool suspended, CancellationToken cancellationToken) =>
        await redis.GetDatabase().StringSetAsync(Key(userId), suspended ? "1" : "0", CacheLifetime);

    private async Task<bool> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();

        // Un usuario borrado no tiene acceso: se trata igual que uno suspendido.
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsSuspended })
            .FirstOrDefaultAsync(cancellationToken);
        return user?.IsSuspended ?? true;
    }

    private async Task TryWriteAsync(Guid userId, bool suspended, When when)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(Key(userId), suspended ? "1" : "0", CacheLifetime, when);
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(ex);
        }
    }

    private static RedisKey Key(Guid userId) => $"ascua:user:{userId:N}:access";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Caché de acceso no disponible; se consulta Postgres")]
    private partial void LogCacheUnavailable(Exception exception);
}
