using Ascua.Application.Abstractions;
using Ascua.Application.Matches;
using Ascua.Domain.Model;
using Microsoft.Extensions.Logging;

namespace Ascua.Application.Rooms;

/// <summary>Una sala vista por un usuario: si es participante ve también los datos privados (código, partida).</summary>
public sealed record RoomView(Room Room, bool IsMember);

/// <summary>
/// Casos de uso de salas. Cada cambio es leer → aplicar la regla de <see cref="Room"/> → guardar con
/// control de versión; si otra petición ganó la carrera se relee y se reintenta.
/// </summary>
public sealed partial class RoomService(
    IRoomStore store,
    IInviteCodeGenerator inviteCodes,
    IMatchLauncher launcher,
    IClock clock,
    ILogger<RoomService> logger)
{
    /// <summary>Con 2-4 plazas y un ganador por ronda, 10 intentos sobran incluso con decenas de uniones simultáneas.</summary>
    internal const int MaxAttempts = 10;
    private const int MaxInviteCodeAttempts = 5;

    public async Task<LobbyResult<RoomView>> CreateAsync(
        Guid userId, string username, string name, RoomVisibility visibility, int capacity, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxInviteCodeAttempts; attempt++)
        {
            var code = visibility == RoomVisibility.Private ? inviteCodes.Next() : null;
            var room = Room.Create(Guid.NewGuid(), name, visibility, code, capacity, userId, username, clock.UtcNow);
            var result = await store.SaveAsync(null, room, cancellationToken);
            switch (result.Outcome)
            {
                case RoomSaveOutcome.Saved:
                    LogRoomCreated(room.Id, userId, visibility);
                    return new RoomView(result.Room!, true);
                case RoomSaveOutcome.UserBusy:
                    return result.BusyReason!.Value;
                case RoomSaveOutcome.InviteCodeTaken:
                case RoomSaveOutcome.VersionConflict:
                    continue;
                default:
                    throw new InvalidOperationException($"Resultado inesperado: {result.Outcome}.");
            }
        }

        return LobbyError.RoomBusy;
    }

    public async Task<LobbyResult<RoomView>> GetAsync(Guid userId, Guid roomId, CancellationToken cancellationToken)
    {
        var room = await store.GetAsync(roomId, cancellationToken);
        return View(room, userId);
    }

    public async Task<LobbyResult<RoomView>> GetCurrentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var roomId = await store.FindRoomIdOfUserAsync(userId, cancellationToken);
        return roomId is null ? LobbyError.RoomNotFound : await GetAsync(userId, roomId.Value, cancellationToken);
    }

    public Task<RoomPage> ListPublicAsync(int page, int pageSize, CancellationToken cancellationToken) =>
        store.ListPublicAsync((page - 1) * pageSize, pageSize, cancellationToken);

    /// <summary>Unirse por id: solo salas públicas (una privada responde "no existe" para no revelarla).</summary>
    public Task<LobbyResult<RoomView>> JoinAsync(Guid userId, string username, Guid roomId, CancellationToken cancellationToken) =>
        JoinCoreAsync(userId, username, roomId, byInviteCode: false, cancellationToken);

    public async Task<LobbyResult<RoomView>> JoinByCodeAsync(Guid userId, string username, string inviteCode, CancellationToken cancellationToken)
    {
        var roomId = await store.FindIdByInviteCodeAsync(inviteCode.ToUpperInvariant(), cancellationToken);
        return roomId is null
            ? LobbyError.RoomNotFound
            : await JoinCoreAsync(userId, username, roomId.Value, byInviteCode: true, cancellationToken);
    }

    public async Task<LobbyResult<bool>> LeaveAsync(Guid userId, Guid roomId, CancellationToken cancellationToken)
    {
        var result = await MutateAsync(userId, roomId, room => room.Leave(userId), cancellationToken);
        return result.Error is { } error ? error : true;
    }

    public async Task<LobbyResult<RoomView>> ChooseHeroAsync(Guid userId, Guid roomId, HeroClass hero, CancellationToken cancellationToken) =>
        ToView(await MutateAsync(userId, roomId, room => Widen(room.ChooseHero(userId, hero)), cancellationToken), userId);

    public async Task<LobbyResult<RoomView>> SetReadyAsync(Guid userId, Guid roomId, bool ready, CancellationToken cancellationToken) =>
        ToView(await MutateAsync(userId, roomId, room => Widen(room.SetReady(userId, ready)), cancellationToken), userId);

    /// <summary>
    /// El anfitrión inicia la partida: congela la sala, llama a <see cref="IMatchLauncher"/> (idempotente por sala)
    /// y, con el id de la partida, marca a cada jugador con su partida activa y lo saca del lobby.
    /// Si el lanzamiento falla, la sala vuelve a quedar abierta.
    /// </summary>
    public async Task<LobbyResult<Guid>> StartAsync(Guid userId, Guid roomId, CancellationToken cancellationToken)
    {
        var started = await MutateAsync(
            userId,
            roomId,
            room => room is { Status: RoomStatus.Started, MatchId: { } } && room.HasMember(userId)
                ? room
                : Widen(room.BeginStart(userId)),
            cancellationToken);
        if (started.Error is { } error)
        {
            return error;
        }

        var frozen = started.Value!;
        if (frozen is { Status: RoomStatus.Started, MatchId: { } existing })
        {
            return existing;
        }

        Guid matchId;
        try
        {
            var seats = frozen.Members.Select(m => new MatchSeat(m.UserId, m.Hero!.Value)).ToList();
            matchId = await launcher.LaunchAsync(new LaunchMatchRequest(roomId, seats), cancellationToken);
        }
        catch (Exception ex)
        {
            // Sin CancellationToken: la sala no debe quedar congelada porque el cliente cortó la petición.
            await MutateAsync(userId, roomId, room => room.AbortStart(), CancellationToken.None);
            if (ex is MatchLaunchUnavailableException)
            {
                LogLaunchUnavailable(roomId);
                return LobbyError.MatchesUnavailable;
            }

            throw;
        }

        var marked = await MutateAsync(userId, roomId, room => Widen(room.MarkStarted(matchId)), CancellationToken.None);
        if (marked.Error is { } markError)
        {
            return markError;
        }

        LogRoomStarted(roomId, matchId, frozen.Members.Count);
        return matchId;
    }

    private async Task<LobbyResult<RoomView>> JoinCoreAsync(
        Guid userId, string username, Guid roomId, bool byInviteCode, CancellationToken cancellationToken)
    {
        var result = await MutateAsync(
            userId,
            roomId,
            room => !byInviteCode && room.Visibility == RoomVisibility.Private && !room.HasMember(userId)
                ? LobbyError.RoomNotFound
                : Widen(room.Join(userId, username, clock.UtcNow)),
            cancellationToken);
        return ToView(result, userId);
    }

    /// <summary>Lee, aplica <paramref name="change"/> y guarda con control de versión, reintentando si hubo carrera.</summary>
    private async Task<LobbyResult<Room?>> MutateAsync(
        Guid userId, Guid roomId, Func<Room, LobbyResult<Room?>> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var current = await store.GetAsync(roomId, cancellationToken);
            if (current is null)
            {
                return LobbyError.RoomNotFound;
            }

            var next = change(current);
            if (next.Error == LobbyError.NotRoomMember && current.Visibility == RoomVisibility.Private)
            {
                // Una sala privada no se revela a quien no está en ella.
                return LobbyError.RoomNotFound;
            }

            if (next.Error is not null || ReferenceEquals(next.Value, current))
            {
                return next;
            }

            var saved = await store.SaveAsync(current, next.Value, cancellationToken);
            switch (saved.Outcome)
            {
                case RoomSaveOutcome.Saved:
                    return LobbyResult<Room?>.Ok(saved.Room);
                case RoomSaveOutcome.UserBusy:
                    return saved.BusyReason!.Value;
                case RoomSaveOutcome.VersionConflict:
                    // Pequeña espera aleatoria para que los perdedores no vuelvan a chocar todos a la vez.
                    await Task.Delay(Random.Shared.Next(1, 5 * (attempt + 1)), cancellationToken);
                    continue;
                default:
                    throw new InvalidOperationException($"Resultado inesperado: {saved.Outcome}.");
            }
        }

        LogRoomBusy(roomId);
        return LobbyError.RoomBusy;
    }

    private static LobbyResult<Room?> Widen(LobbyResult<Room> result) =>
        result.Error is { } error ? error : LobbyResult<Room?>.Ok(result.Value);

    private static LobbyResult<RoomView> ToView(LobbyResult<Room?> result, Guid userId) =>
        result.Error is { } error ? error : View(result.Value, userId);

    private static LobbyResult<RoomView> View(Room? room, Guid userId)
    {
        if (room is null)
        {
            return LobbyError.RoomNotFound;
        }

        var isMember = room.HasMember(userId);
        return room.Visibility == RoomVisibility.Private && !isMember
            ? LobbyError.RoomNotFound
            : new RoomView(room, isMember);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sala {RoomId} creada por {UserId} ({Visibility})")]
    private partial void LogRoomCreated(Guid roomId, Guid userId, RoomVisibility visibility);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sala {RoomId} inició la partida {MatchId} con {Players} jugadores")]
    private partial void LogRoomStarted(Guid roomId, Guid matchId, int players);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sala {RoomId}: el servicio de partidas no está disponible")]
    private partial void LogLaunchUnavailable(Guid roomId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sala {RoomId}: demasiados conflictos de versión seguidos")]
    private partial void LogRoomBusy(Guid roomId);
}
