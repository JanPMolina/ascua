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

    /// <summary>Jugadores de los que se espera orden en la ronda abierta.</summary>
    public IReadOnlyList<PlayerId> ActivePlayers() => [.. State.Heroes.Where(h => h.IsActive).Select(h => h.Player)];
}

/// <summary>ACK guardado de una orden: un reenvío con el mismo <c>clientActionId</c> devuelve exactamente este.</summary>
public sealed record OrderAck(Guid MatchId, int Round, Guid ClientActionId, Guid UserId, DateTimeOffset ReceivedAt);

/// <summary>ACK guardado de un <c>DraftPick</c>, para que repetir la misma elección devuelva lo mismo.</summary>
public sealed record DraftAck(Guid MatchId, Guid OfferId, Blessing Blessing, DateTimeOffset AcceptedAt);

public enum ConnectionState
{
    Connected,
    Disconnected,
    Abandoned,
}
