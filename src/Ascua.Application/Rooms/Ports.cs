namespace Ascua.Application.Rooms;

// Puertos que implementa Infrastructure (Redis). Ver docs/adr/0007-salas-y-emparejamiento.md.

public interface IRoomStore
{
    Task<Room?> GetAsync(Guid roomId, CancellationToken cancellationToken);

    Task<Guid?> FindIdByInviteCodeAsync(string inviteCode, CancellationToken cancellationToken);

    /// <summary>Sala abierta en la que está sentado el usuario, si hay una.</summary>
    Task<Guid?> FindRoomIdOfUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Salas con <see cref="Room.IsListed"/>, de la más antigua a la más nueva.</summary>
    Task<RoomPage> ListPublicAsync(int offset, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Escribe <paramref name="updated"/> (o borra la sala si es <c>null</c>) solo si la versión guardada sigue
    /// siendo la de <paramref name="expected"/> (<c>null</c> = la sala no debe existir). En la misma operación
    /// atómica reserva a cada miembro nuevo (no puede estar en otra sala, en la cola ni en una partida),
    /// libera a los que salieron y, si la sala pasó a <see cref="RoomStatus.Started"/>, marca la partida activa
    /// de cada jugador.
    /// </summary>
    Task<RoomSaveResult> SaveAsync(Room? expected, Room? updated, CancellationToken cancellationToken);
}

public sealed record RoomPage(IReadOnlyList<Room> Items, long Total);

public enum RoomSaveOutcome
{
    Saved,

    /// <summary>Otra operación cambió la sala entre la lectura y la escritura: hay que releer y reintentar.</summary>
    VersionConflict,

    /// <summary>Un miembro nuevo ya está en otra sala, en la cola o en una partida (<see cref="RoomSaveResult.BusyReason"/>).</summary>
    UserBusy,

    /// <summary>El código de invitación ya lo usa otra sala.</summary>
    InviteCodeTaken,
}

public sealed record RoomSaveResult(RoomSaveOutcome Outcome, Room? Room = null, LobbyError? BusyReason = null)
{
    public static RoomSaveResult Saved(Room? room) => new(RoomSaveOutcome.Saved, room);
}

/// <summary>Genera códigos de invitación (CSPRNG en Infrastructure).</summary>
public interface IInviteCodeGenerator
{
    string Next();
}

/// <summary>Tiempos de vida del lobby (configurables en <c>Ascua:Lobby</c>).</summary>
/// <param name="RoomTtl">Una sala sin cambios durante este tiempo se borra sola.</param>
/// <param name="ActiveMatchTtl">Tope de seguridad de la marca "partida activa" si el tiempo real nunca la libera.</param>
public sealed record LobbyPolicy(TimeSpan RoomTtl, TimeSpan ActiveMatchTtl);
