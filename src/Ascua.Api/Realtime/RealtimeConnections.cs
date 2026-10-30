using System.Collections.Concurrent;
using Ascua.Application.Admin;
using Ascua.Application.Matches;
using Microsoft.AspNetCore.SignalR;
using StackExchange.Redis;

namespace Ascua.Api.Realtime;

/// <summary>
/// Conexiones abiertas en ESTA réplica. Sirve para cerrar una conexión desde cualquier réplica
/// (<c>SESSION_REPLACED</c>: se publica en el canal de control de Redis y la réplica dueña la aborta),
/// para avisar <c>ServerDraining</c> solo a las propias y para renovar sus arriendos del límite por usuario.
/// </summary>
public sealed class RealtimeConnections(
    IConnectionMultiplexer redis,
    IHubContext<MatchHub> hub,
    ILogger<RealtimeConnections> logger) : IRealtimeKicker
{
    public const string KickedByAdmin = "KICKED_BY_ADMIN";

    /// <summary>Margen entre avisar el fin de una partida terminada por un admin y cortar sus conexiones.</summary>
    public static readonly TimeSpan MatchKickDelay = TimeSpan.FromSeconds(2);

    private static readonly RedisChannel ControlChannel = RedisChannel.Literal("ascua:realtime:control");

    // Las órdenes kick-* se aplican primero en la réplica que las emite; al volver por Redis se ignoran aquí.
    private readonly string _replicaId = Guid.NewGuid().ToString("N");

    private readonly ConcurrentDictionary<string, HubCallerContext> _connections = new();
    private readonly ConcurrentDictionary<string, Guid> _leases = new();

    public IReadOnlyCollection<string> LocalConnectionIds => [.. _connections.Keys];

    public IEnumerable<(Guid UserId, string LeaseId)> Leases => _leases.Select(l => (l.Value, l.Key));

    public void Register(HubCallerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _connections[context.ConnectionId] = context;
    }

    public void Unregister(string connectionId) => _connections.TryRemove(connectionId, out _);

    public void TrackLease(Guid userId, string leaseId) => _leases[leaseId] = userId;

    public void ForgetLease(string leaseId) => _leases.TryRemove(leaseId, out _);

    /// <summary>Cierra la conexión esté en la réplica que esté, avisando antes la razón con un mensaje <c>Error</c>.</summary>
    public async Task AbortAsync(string connectionId, string reason)
    {
        if (!await AbortLocalAsync(connectionId, reason))
        {
            await redis.GetSubscriber().PublishAsync(ControlChannel, $"abort|{connectionId}|{reason}");
        }
    }

    /// <summary>Cierra en todas las réplicas las conexiones del usuario (suspendido) con <c>KICKED_BY_ADMIN</c>.</summary>
    public async Task KickUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await KickLocalAsync(c => UserOf(c) == userId);
        await redis.GetSubscriber().PublishAsync(ControlChannel, $"kick-user|{_replicaId}|{userId:D}");
    }

    /// <summary>
    /// Cierra en todas las réplicas las conexiones de la partida terminada. Espera <see cref="MatchKickDelay"/>
    /// para que antes lleguen <c>MatchFinished</c> y el <c>Error</c> <c>KICKED_BY_ADMIN</c> (van por el backplane).
    /// </summary>
    public Task KickMatchAsync(Guid matchId, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(MatchKickDelay, CancellationToken.None);
                await KickLocalAsync(c => MatchOf(c) == matchId, notify: false);
                await redis.GetSubscriber().PublishAsync(ControlChannel, $"kick-match|{_replicaId}|{matchId:D}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudieron cerrar las conexiones de la partida {MatchId}", matchId);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task SubscribeAsync() =>
        redis.GetSubscriber().SubscribeAsync(ControlChannel, async (_, message) =>
        {
            var parts = ((string?)message)?.Split('|');
            try
            {
                switch (parts)
                {
                    case ["abort", var connectionId, var reason]:
                        await AbortLocalAsync(connectionId, reason);
                        break;
                    case ["kick-user", var origin, var user] when origin != _replicaId && Guid.TryParse(user, out var userId):
                        await KickLocalAsync(c => UserOf(c) == userId);
                        break;
                    case ["kick-match", var origin, var match] when origin != _replicaId && Guid.TryParse(match, out var matchId):
                        await KickLocalAsync(c => MatchOf(c) == matchId, notify: false);
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo procesar el mensaje de control {Message}", parts?.FirstOrDefault());
            }
        });

    public Task UnsubscribeAsync() => redis.GetSubscriber().UnsubscribeAsync(ControlChannel);

    private async Task KickLocalAsync(Func<HubCallerContext, bool> predicate, bool notify = true)
    {
        foreach (var context in _connections.Values.Where(predicate).ToList())
        {
            if (notify)
            {
                await AbortLocalAsync(context.ConnectionId, KickedByAdmin);
            }
            else
            {
                logger.LogInformation("Cerrando la conexión {ConnectionId}: {Reason}", context.ConnectionId, KickedByAdmin);
                context.Abort();
            }
        }
    }

    private async Task<bool> AbortLocalAsync(string connectionId, string reason)
    {
        if (!_connections.TryGetValue(connectionId, out var context))
        {
            return false;
        }

        // SignalR no deja al servidor poner la razón en Close.error: se envía antes como Error{code}.
        logger.LogInformation("Cerrando la conexión {ConnectionId}: {Reason}", connectionId, reason);
        var message = reason == KickedByAdmin
            ? "Un administrador cerró esta conexión."
            : "La conexión se cierra: se abrió otra sesión de este usuario en la partida.";
        await hub.Clients.Client(connectionId).SendAsync("Error", RealtimeReplies.Error(reason, message));
        context.Abort();
        return true;
    }

    private static Guid? UserOf(HubCallerContext context) =>
        Guid.TryParse(context.UserIdentifier, out var id) ? id : null;

    private static Guid? MatchOf(HubCallerContext context) =>
        Guid.TryParse(context.User?.FindFirst(TicketAuthentication.MatchClaim)?.Value, out var id) ? id : null;
}

/// <summary>
/// Token bucket por conexión (capacidad 20, recarga 5/s) y conversión de excepciones en
/// <c>INTERNAL_ERROR</c> tipado: el cliente nunca recibe una excepción de SignalR sin tipo.
/// El abuso sostenido (más de 50 rechazos en 60 s) cierra la conexión, no la partida.
/// </summary>
public sealed class RealtimeHubFilter(RealtimeOptions options, ILogger<RealtimeHubFilter> logger) : IHubFilter
{
    private const string StateKey = "ascua:limiter";

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        ArgumentNullException.ThrowIfNull(invocationContext);
        ArgumentNullException.ThrowIfNull(next);
        var items = invocationContext.Context.Items;
        if (items.TryGetValue(StateKey, out var stored) is false || stored is not ConnectionBucket state)
        {
            state = new ConnectionBucket(options);
            items[StateKey] = state;
        }

        if (!state.TryTake(DateTimeOffset.UtcNow, out var retryAfter, out var abusive))
        {
            if (abusive)
            {
                logger.LogWarning("Conexión {ConnectionId} cerrada por abuso de mensajes", invocationContext.Context.ConnectionId);
                invocationContext.Context.Abort();
            }

            return RealtimeReplies.Error(RealtimeErrors.RateLimited, "Demasiados mensajes en esta conexión.", (int)retryAfter.TotalMilliseconds);
        }

        try
        {
            return await next(invocationContext);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Error en {Method} de la conexión {ConnectionId}", invocationContext.HubMethodName, invocationContext.Context.ConnectionId);
            return RealtimeReplies.Error(RealtimeErrors.InternalError, "Error del servidor; no se aplicó nada.", 1000);
        }
    }

    /// <summary>Token bucket simple (sin temporizadores): se recarga al consultar.</summary>
    private sealed class ConnectionBucket(RealtimeOptions options)
    {
        private readonly Lock _gate = new();
        private readonly Queue<DateTimeOffset> _rejections = new();
        private double _tokens = options.HubTokenLimit;
        private DateTimeOffset _last = DateTimeOffset.UtcNow;

        public bool TryTake(DateTimeOffset now, out TimeSpan retryAfter, out bool abusive)
        {
            lock (_gate)
            {
                _tokens = Math.Min(options.HubTokenLimit, _tokens + ((now - _last).TotalSeconds * options.HubTokensPerSecond));
                _last = now;
                abusive = false;
                if (_tokens >= 1)
                {
                    _tokens -= 1;
                    retryAfter = TimeSpan.Zero;
                    return true;
                }

                retryAfter = TimeSpan.FromSeconds((1 - _tokens) / options.HubTokensPerSecond);
                _rejections.Enqueue(now);
                while (_rejections.Count > 0 && now - _rejections.Peek() > TimeSpan.FromSeconds(60))
                {
                    _rejections.Dequeue();
                }

                abusive = _rejections.Count > options.HubAbuseThreshold;
                return false;
            }
        }
    }
}
