namespace Ascua.Application.Rooms;

/// <summary>Errores de negocio de salas y emparejamiento. La API los traduce a Problem Details con tipos estables.</summary>
public enum LobbyError
{
    RoomNotFound,
    RoomFull,
    AlreadyInRoom,
    AlreadyInQueue,
    AlreadyInMatch,
    NotRoomMember,
    NotRoomHost,
    RoomNotOpen,
    HeroTaken,
    HeroRequired,
    NotEnoughReady,

    /// <summary>La sala cambió demasiadas veces seguidas mientras se intentaba modificarla.</summary>
    RoomBusy,

    /// <summary>El emparejamiento ya agrupó al jugador y su partida se está creando.</summary>
    MatchStarting,

    /// <summary>El servicio de partidas (tiempo real) aún no está disponible.</summary>
    MatchesUnavailable,
}

/// <summary>Resultado de un caso de uso: un valor o un <see cref="LobbyError"/>.</summary>
public readonly record struct LobbyResult<T>
{
    private LobbyResult(T? value, LobbyError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public LobbyError? Error { get; }

    public static LobbyResult<T> Ok(T value) => new(value, null);

    public static LobbyResult<T> Fail(LobbyError error) => new(default, error);

    public static implicit operator LobbyResult<T>(T value) => Ok(value);

    public static implicit operator LobbyResult<T>(LobbyError error) => Fail(error);
}
