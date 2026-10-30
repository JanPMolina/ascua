using System.Text.Json;
using Ascua.Application.Abstractions;
using Ascua.Application.Audit;
using Ascua.Application.Matches;
using Ascua.Application.Matchmaking;
using Ascua.Application.Rooms;
using Ascua.Application.Users;
using Microsoft.Extensions.Logging;

namespace Ascua.Application.Admin;

/// <summary>
/// Casos de uso de administración (solo rol <c>admin</c>; la API lo exige en cada endpoint). Cada acción
/// que cambia algo escribe en <c>audit_log</c> dentro de la misma transacción que el cambio:
/// suspender/rehabilitar con el mismo <see cref="IUnitOfWork"/>; terminar con la transacción de
/// <see cref="IMatchStore.MarkStatusAsync"/>. Los efectos posteriores (revocar sesiones, cortar
/// conexiones, sacar de salas) son idempotentes y se apoyan en el estado ya confirmado en Postgres.
/// </summary>
public sealed partial class AdminService(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IUnitOfWork unitOfWork,
    IAuditLog auditLog,
    IUserAccessGate accessGate,
    IRealtimeKicker kicker,
    IAdminQueries queries,
    MatchEngine engine,
    IMatchHotState hot,
    IConnectionLimiter connectionLimiter,
    IActiveMatchRegistry activeMatches,
    RoomService rooms,
    MatchmakingService matchmaking,
    IClock clock,
    ILogger<AdminService> logger)
{
    /// <summary>Máximo de partidas que revisa el resumen de conexiones (es un panel, no un export).</summary>
    public const int ConnectionsScanLimit = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AdminMatchList> ListActiveMatchesAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var rows = await queries.ListActiveMatchesAsync((page - 1) * pageSize, pageSize, cancellationToken);
        var items = new List<AdminMatch>(rows.Items.Count);
        foreach (var row in rows.Items)
        {
            items.Add(new AdminMatch(row, await hot.ConnectedUsersAsync(row.MatchId, cancellationToken)));
        }

        return new AdminMatchList(items, rows.Total);
    }

    public async Task<ConnectionsOverview> GetConnectionsAsync(CancellationToken cancellationToken)
    {
        var rows = await queries.ListActiveMatchesAsync(0, ConnectionsScanLimit, cancellationToken);
        var counts = new Dictionary<Guid, int>();
        var matches = new List<MatchConnections>();
        foreach (var row in rows.Items)
        {
            var connected = await hot.ConnectedUsersAsync(row.MatchId, cancellationToken);
            var players = new List<ConnectedPlayer>();
            foreach (var player in row.Players.Where(p => connected.Contains(p.UserId)))
            {
                if (!counts.TryGetValue(player.UserId, out var count))
                {
                    // Está en la presencia de la partida: al menos una conexión, aunque el arriendo aún no se vea.
                    count = Math.Max(1, await connectionLimiter.CountAsync(player.UserId, cancellationToken));
                    counts[player.UserId] = count;
                }

                players.Add(new ConnectedPlayer(player.UserId, player.DisplayName, count));
            }

            if (players.Count > 0)
            {
                matches.Add(new MatchConnections(row.MatchId, row.Status, players));
            }
        }

        return new ConnectionsOverview(clock.UtcNow, counts.Values.Sum(), counts.Count, matches);
    }

    public async Task<AdminResult<TerminationResult>> TerminateMatchAsync(
        AuditActor actor, Guid matchId, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var now = clock.UtcNow;
        var result = await engine.TerminateAsync(
            matchId,
            snapshot => AuditEntry.Create(
                actor,
                AuditActions.MatchTerminated,
                AuditTargets.Match,
                matchId.ToString("D"),
                reason,
                Json(new
                {
                    previousStatus = snapshot.Status.ToCode(),
                    round = snapshot.State.Round,
                    players = snapshot.Participants.Select(p => p.UserId).ToArray(),
                }),
                now),
            cancellationToken);

        if (result.Error is { } error)
        {
            return error.Code switch
            {
                RealtimeErrors.MatchNotFound => AdminError.MatchNotFound,
                RealtimeErrors.MatchNotActive => AdminError.MatchNotActive,
                _ => AdminError.Busy,
            };
        }

        LogMatchTerminated(actor.UserId, matchId);
        await BestEffortAsync(() => kicker.KickMatchAsync(matchId, CancellationToken.None), "cortar las conexiones de la partida", matchId);
        var stopped = result.Value!;
        return new TerminationResult(matchId, stopped.Status.ToCode(), stopped.State.Round, now);
    }

    /// <summary>
    /// Suspende la cuenta: en Postgres (con su auditoría) y luego, sobre ese estado ya confirmado, revoca todas
    /// sus familias de refresh, bloquea el acceso (el access token vigente deja de servir), corta sus
    /// conexiones WS, lo retira de su partida activa y lo saca de su sala y de la cola.
    /// </summary>
    public async Task<AdminResult<SuspensionResult>> SuspendAsync(
        AuditActor actor, Guid userId, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.UserId == userId)
        {
            return AdminError.CannotTargetSelf;
        }

        var user = await users.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AdminError.UserNotFound;
        }

        var now = clock.UtcNow;
        var wasSuspended = user.IsSuspended;
        user.Suspend(now);
        auditLog.Append(AuditEntry.Create(
            actor, AuditActions.UserSuspended, AuditTargets.User, userId.ToString("D"), reason,
            Json(new { username = user.Username, wasSuspended }), now));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        LogSuspended(actor.UserId, userId);

        await refreshTokens.RevokeAllForUserAsync(userId, now, cancellationToken);
        await accessGate.SetSuspendedAsync(userId, true, cancellationToken);
        await BestEffortAsync(() => kicker.KickUserAsync(userId, CancellationToken.None), "cortar las conexiones del usuario", userId);
        await BestEffortAsync(() => RemoveFromLobbyAndMatchAsync(userId), "sacar al usuario de salas, cola y partida", userId);

        return new SuspensionResult(user.Id, user.Username, true, user.UpdatedAt);
    }

    public async Task<AdminResult<SuspensionResult>> UnsuspendAsync(
        AuditActor actor, Guid userId, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var user = await users.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return AdminError.UserNotFound;
        }

        var now = clock.UtcNow;
        var wasSuspended = user.IsSuspended;
        user.Reinstate(now);
        auditLog.Append(AuditEntry.Create(
            actor, AuditActions.UserUnsuspended, AuditTargets.User, userId.ToString("D"), reason,
            Json(new { username = user.Username, wasSuspended }), now));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await accessGate.SetSuspendedAsync(userId, false, cancellationToken);
        LogUnsuspended(actor.UserId, userId);

        return new SuspensionResult(user.Id, user.Username, false, user.UpdatedAt);
    }

    public Task<AuditPage> ListAuditAsync(int page, int pageSize, CancellationToken cancellationToken) =>
        auditLog.ListAsync((page - 1) * pageSize, pageSize, cancellationToken);

    private async Task RemoveFromLobbyAndMatchAsync(Guid userId)
    {
        var cancellationToken = CancellationToken.None;
        if (await activeMatches.GetAsync(userId, cancellationToken) is { } matchId)
        {
            // Mismo efecto que un abandono: su héroe se retira y la partida sigue para los demás.
            await engine.RetireAsync(matchId, userId, cancellationToken);
        }

        var room = await rooms.GetCurrentAsync(userId, cancellationToken);
        if (room.Value is { } view)
        {
            await rooms.LeaveAsync(userId, view.Room.Id, cancellationToken);
        }

        await matchmaking.LeaveAsync(userId, cancellationToken);
    }

    private async Task BestEffortAsync(Func<Task> action, string what, Guid target)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // La acción ya quedó confirmada y auditada; lo que falte se corrige solo (TTL, gracia, validación del JWT).
            LogSideEffectFailed(ex, what, target);
        }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin {ActorId} terminó la partida {MatchId}")]
    private partial void LogMatchTerminated(Guid? actorId, Guid matchId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin {ActorId} suspendió al usuario {UserId}")]
    private partial void LogSuspended(Guid? actorId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin {ActorId} rehabilitó al usuario {UserId}")]
    private partial void LogUnsuspended(Guid? actorId, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo {What} ({Target}); la acción ya está confirmada")]
    private partial void LogSideEffectFailed(Exception exception, string what, Guid target);
}
