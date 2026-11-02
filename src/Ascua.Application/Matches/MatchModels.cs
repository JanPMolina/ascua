using Ascua.Domain.Model;

namespace Ascua.Application.Matches;

/// <summary>Estado de la partida fuera del dominio (incluye los finales que el dominio no conoce).</summary>
public enum MatchStatus
{
    Drafting,
    InProgress,
    Finished,
    Abandoned,
    Expired,
    Faulted,

    /// <summary>Un administrador la detuvo (<c>POST /api/v1/admin/matches/{id}/terminate</c>, auditado).</summary>
    Terminated,
}

public static class MatchStatusExtensions
{
    /// <summary>¿Sigue viva (acepta órdenes, drafts o abandonos)?</summary>
    public static bool IsActive(this MatchStatus status) => status is MatchStatus.Drafting or MatchStatus.InProgress;

    /// <summary>Nombre estable para la base de datos y el contrato (<c>IN_PROGRESS</c>, <c>FAULTED</c>…).</summary>
    public static string ToCode(this MatchStatus status) => status switch
    {
        MatchStatus.Drafting => "DRAFTING",
        MatchStatus.InProgress => "IN_PROGRESS",
        MatchStatus.Finished => "FINISHED",
        MatchStatus.Abandoned => "ABANDONED",
        MatchStatus.Expired => "EXPIRED",
        MatchStatus.Faulted => "FAULTED",
        MatchStatus.Terminated => "TERMINATED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static MatchStatus FromCode(string code) => code switch
    {
        "DRAFTING" => MatchStatus.Drafting,
        "IN_PROGRESS" => MatchStatus.InProgress,
        "FINISHED" => MatchStatus.Finished,
        "ABANDONED" => MatchStatus.Abandoned,
        "EXPIRED" => MatchStatus.Expired,
        "FAULTED" => MatchStatus.Faulted,
        "TERMINATED" => MatchStatus.Terminated,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null),
    };

    /// <summary>Estado que corresponde a la fase del dominio.</summary>
    public static MatchStatus From(GameState state) => state.Phase switch
    {
        MatchPhase.Drafting => MatchStatus.Drafting,
        MatchPhase.InProgress => MatchStatus.InProgress,
        _ => state.Outcome?.Kind == OutcomeKind.Abandoned ? MatchStatus.Abandoned : MatchStatus.Finished,
    };
}

/// <summary>Un asiento de la partida. El <see cref="PlayerId"/> del dominio es el id del usuario.</summary>
public sealed record MatchParticipant(Guid UserId, int Seat, HeroClass Hero, string DisplayName)
{
    public PlayerId PlayerId => PlayerIds.Of(UserId);
}

public static class PlayerIds
{
    public static PlayerId Of(Guid userId) => new(userId.ToString("D"));

    public static Guid ToUserId(PlayerId player) => Guid.Parse(player.Value);
}

/// <summary>
/// Instantánea de una partida: la verdad completa (<see cref="State"/>, con información oculta) más
/// los datos de servidor. Vive en Redis (caliente) y en Postgres (durable); nunca sale a un cliente.
/// <see cref="Version"/> crece con cada confirmación y es el control de concurrencia optimista.
/// </summary>
public sealed record MatchSnapshot
{
    public required Guid MatchId { get; init; }
    public required Guid RoomId { get; init; }
    public required MatchStatus Status { get; init; }
    public required long Version { get; init; }
    public required GameState State { get; init; }
    public required IReadOnlyList<MatchParticipant> Participants { get; init; }

    /// <summary>Plazo vigente: del draft (en <see cref="MatchStatus.Drafting"/>) o de la ronda abierta.</summary>
    public DateTimeOffset? Deadline { get; init; }

    /// <summary>
    /// Planificación de cada ronda de ESTA partida. Se fija al crearla para que una réplica con otra
    /// configuración (p. ej. durante un despliegue) no cambie el ritmo de una partida en curso.
    /// </summary>
    public TimeSpan RoundDuration { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Plazo para elegir camino tras extinguirse (15 s). Cuenta desde que abre la ronda: la oferta nace al
    /// resolverse la anterior, así que se deriva de <see cref="Deadline"/> y <see cref="RoundDuration"/>, que son
    /// durables (sobrevive a reinicios sin columnas nuevas).
    /// </summary>
    public TimeSpan PathChoiceDuration { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Plazo reducido del Repique. Como <see cref="RoundDuration"/>, se fija al crear la partida y es durable: el plazo
    /// sale de <see cref="Deadline"/>, de esto y del estado, así que sobrevive a un reinicio.
    /// </summary>
    public TimeSpan BellDuration { get; init; } = TimeSpan.FromSeconds(8);

    public required DateTimeOffset CreatedAt { get; init; }

    public MatchParticipant? FindParticipant(Guid userId)
    {
        foreach (var participant in Participants)
        {
            if (participant.UserId == userId)
            {
                return participant;
            }
        }

        return null;
    }

    /// <summary>
    /// Vence la elección de camino abierta (si alguien está eligiendo): 15 s desde que abrió la ronda, nunca
    /// después del cierre de la ronda. Al vencer se toma el Camino de la Amistad.
    /// </summary>
    public DateTimeOffset? PathDeadline =>
        Status == MatchStatus.InProgress && Deadline is { } deadline && State.Heroes.Any(h => h.IsChoosingPath)
            ? Min(deadline, deadline - RoundDuration + PathChoiceDuration)
            : null;

    /// <summary>
    /// Vence el plazo reducido del Repique en la ronda abierta: los demás ya no pueden decidir. <c>null</c> si no
    /// suena o ya venció (ya se confirmó en el estado).
    /// </summary>
    public DateTimeOffset? BellDeadline =>
        Status == MatchStatus.InProgress && Deadline is { } deadline && State.IsBellRound && !State.BellClosed
            ? Min(deadline, deadline - RoundDuration + BellDuration)
            : null;

    /// <summary>Próximo instante en que el temporizador debe procesar la partida (elección de camino, Repique o plazo).</summary>
    public DateTimeOffset? WakeAt
    {
        get
        {
            var wake = Deadline;
            if (wake is { } deadline && PathDeadline is { } path)
            {
                wake = Min(deadline, path);
            }

            return wake is { } current && BellDeadline is { } bell ? Min(current, bell) : wake;
        }
    }

    /// <summary>Plazo de este jugador en la ronda abierta: con el Repique, los demás tienen menos que quien lo tocó.</summary>
    public DateTimeOffset? DeadlineFor(PlayerId player)
    {
        if (!State.IsBellRound || State.BellRinger == player || Deadline is not { } deadline)
        {
            return Deadline;
        }

        return Min(deadline, deadline - RoundDuration + BellDuration);
    }

    /// <summary>¿Se le acabó el tiempo del Repique a este jugador?</summary>
    public bool IsBellOverFor(PlayerId player, DateTimeOffset now) =>
        State.IsBellRound && State.BellRinger != player && (State.BellClosed || DeadlineFor(player) <= now);

    /// <summary>Jugadores de los que se espera orden en la ronda abierta (héroes activos, Brasas y Renegados presentes).</summary>
    public IReadOnlyList<PlayerId> ActivePlayers() => [.. State.Heroes.Where(h => h.CanAct).Select(h => h.Player)];

    /// <summary>Jugadores con la elección de camino abierta.</summary>
    public IReadOnlyList<PlayerId> ChoosingPlayers() => [.. State.Heroes.Where(h => h.IsChoosingPath).Select(h => h.Player)];

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

/// <summary>ACK guardado de una orden: un reenvío con el mismo <c>clientActionId</c> devuelve exactamente este.</summary>
public sealed record OrderAck(Guid MatchId, int Round, Guid ClientActionId, Guid UserId, DateTimeOffset ReceivedAt);

/// <summary>ACK guardado de un <c>DraftPick</c>, para que repetir la misma elección devuelva lo mismo.</summary>
public sealed record DraftAck(Guid MatchId, Guid OfferId, Blessing Blessing, DateTimeOffset AcceptedAt);

/// <summary>ACK guardado de un <c>ChoosePath</c>: repetir la misma elección devuelve lo mismo.</summary>
public sealed record PathAck(Guid MatchId, Guid OfferId, PathKind Path, DateTimeOffset AcceptedAt);

/// <summary>Órdenes que oye el Campanero (Último en hablar). Como Renegado solo oye los tipos.</summary>
public sealed record LastWordHeard(int Round, IReadOnlyList<(PlayerId Player, Order Order)> Orders, bool TypesOnly);

/// <summary>Frases fijas de las señales rápidas (05-heroes.md §13).</summary>
public enum SignalKind
{
    /// <summary>«¡Aquí!»</summary>
    Here,

    /// <summary>«¡Cuidado!»</summary>
    Danger,

    /// <summary>«Ayuda»</summary>
    Help,

    /// <summary>«Es mía»</summary>
    Mine,
}

/// <summary>Señal rápida: una frase fija y, si quiere, una casilla marcada. Es información libre: también se puede mentir.</summary>
public sealed record QuickSignal(SignalKind Kind, Position? Tile);

public enum ConnectionState
{
    Connected,
    Disconnected,
    Abandoned,
}
