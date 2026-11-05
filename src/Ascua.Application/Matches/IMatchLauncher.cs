using Ascua.Domain.Model;

namespace Ascua.Application.Matches;

/// <summary>Un jugador sentado en la partida con el héroe que eligió en la sala.</summary>
public sealed record MatchSeat(Guid UserId, HeroClass Hero);

/// <summary>Pedido de arranque cuando una sala tiene ≥2 jugadores listos (o el emparejamiento agrupó una).</summary>
public sealed record LaunchMatchRequest(Guid RoomId, IReadOnlyList<MatchSeat> Seats)
{
    /// <summary>Maldiciones que eligió el anfitrión (públicas y guardadas con la partida).</summary>
    public IReadOnlyList<Curse> Curses { get; init; } = [];
}

/// <summary>
/// Punto de unión entre salas/emparejamiento (lo llaman) y el tiempo real (lo implementa):
/// crea la partida, la persiste y abre la primera ronda. Devuelve el id de la partida.
/// Debe ser idempotente por <see cref="LaunchMatchRequest.RoomId"/>: lanzar dos veces la misma sala
/// devuelve la misma partida.
/// </summary>
public interface IMatchLauncher
{
    Task<Guid> LaunchAsync(LaunchMatchRequest request, CancellationToken cancellationToken);
}
