using Ascua.Application.Rooms;
using Ascua.Domain.Model;

namespace Ascua.Application.Matchmaking;

// Cola de emparejamiento: la implementa Infrastructure con un sorted set de Redis y scripts Lua.
// Ver docs/adr/0007-salas-y-emparejamiento.md.

public enum QueueState
{
    /// <summary>No está en la cola.</summary>
    Idle,

    /// <summary>Esperando a que se forme un grupo.</summary>
    Queued,

    /// <summary>Ya está en un grupo y se está creando la partida.</summary>
    Matching,

    /// <summary>Tiene partida activa (<see cref="QueueStatus.MatchId"/>).</summary>
    Matched,

    /// <summary>Salió de la cola porque esperó más que el tiempo máximo sin encontrar rivales.</summary>
    Expired,
}

public sealed record QueueStatus(
    QueueState State,
    DateTimeOffset? EnqueuedAt,
    long? Position,
    long PlayersInQueue,
    HeroClass? PreferredHero,
    Guid? MatchId);

public sealed record QueueTicket(Guid UserId, string Username, HeroClass? PreferredHero, DateTimeOffset EnqueuedAt);

/// <summary>Jugadores que el agrupador sacó de la cola, en orden de llegada. <see cref="GroupId"/> hace de RoomId al lanzar.</summary>
public sealed record MatchGroup(Guid GroupId, IReadOnlyList<QueueTicket> Players);

public interface IMatchmakingQueue
{
    /// <summary>
    /// Entra a la cola. Devuelve el error si el usuario está en una sala, en una partida o ya agrupado.
    /// Si ya estaba esperando no hace nada (idempotente, conserva su lugar).
    /// </summary>
    Task<LobbyError?> EnqueueAsync(QueueTicket ticket, CancellationToken cancellationToken);

    /// <summary>Sale de la cola (idempotente). Falla con <see cref="LobbyError.MatchStarting"/> si ya fue agrupado.</summary>
    Task<LobbyError?> LeaveAsync(Guid userId, CancellationToken cancellationToken);

    Task<QueueStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Saca de forma atómica un grupo de 2 a 5 jugadores: 5 en cuanto los haya, o los que haya (≥2) si el más
    /// antiguo esperó al menos <paramref name="maxWait"/>. Los deja en estado "agrupado" hasta
    /// <see cref="CompleteGroupAsync"/> o <see cref="AbortGroupAsync"/>.
    /// </summary>
    Task<MatchGroup?> TryFormGroupAsync(DateTimeOffset now, TimeSpan maxWait, Guid groupId, CancellationToken cancellationToken);

    /// <summary>Grupos formados antes de <paramref name="formedBefore"/> que nadie completó (p. ej. se cayó la réplica).</summary>
    Task<IReadOnlyList<MatchGroup>> GetStalePendingGroupsAsync(DateTimeOffset formedBefore, CancellationToken cancellationToken);

    /// <summary>Marca la partida activa de cada jugador (<c>SET NX</c>) y cierra el grupo. Idempotente.</summary>
    Task CompleteGroupAsync(Guid groupId, Guid matchId, TimeSpan activeMatchTtl, CancellationToken cancellationToken);

    /// <summary>Devuelve los jugadores del grupo a la cola con su antigüedad original.</summary>
    Task AbortGroupAsync(Guid groupId, CancellationToken cancellationToken);

    /// <summary>Saca de la cola a quienes entraron antes de <paramref name="enqueuedBefore"/>. Devuelve cuántos.</summary>
    Task<int> ExpireAsync(DateTimeOffset enqueuedBefore, CancellationToken cancellationToken);
}

/// <summary>Configuración del emparejamiento (<c>Ascua:Matchmaking</c>).</summary>
/// <param name="MaxWait">Pasado este tiempo, el más antiguo acepta un grupo de 2-3 en lugar de esperar a 4.</param>
/// <param name="QueueTimeout">Quien espera más que esto sin rivales sale de la cola (estado <see cref="QueueState.Expired"/>).</param>
/// <param name="PollInterval">Cada cuánto el servicio en segundo plano procesa la cola.</param>
public sealed record MatchmakingPolicy(TimeSpan MaxWait, TimeSpan QueueTimeout, TimeSpan PollInterval);
