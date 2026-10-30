namespace Ascua.Application.Matches;

/// <summary>
/// Marca "partida activa" de cada jugador (<c>SET NX</c> en Redis). La ponen salas y emparejamiento al lanzar
/// una partida; la quita el tiempo real cuando la partida termina o el jugador se retira. Mientras exista,
/// el jugador no puede crear ni unirse a salas ni entrar a la cola.
/// </summary>
public interface IActiveMatchRegistry
{
    Task<Guid?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Quita la marca solo si sigue apuntando a <paramref name="matchId"/>. Idempotente.</summary>
    Task ReleaseAsync(Guid userId, Guid matchId, CancellationToken cancellationToken);
}

/// <summary>El servicio de partidas no puede crear partidas ahora (p. ej. el tiempo real aún no está desplegado).</summary>
public sealed class MatchLaunchUnavailableException(string message = "El servicio de partidas aún no está disponible.")
    : Exception(message);

/// <summary>
/// Implementación de reserva mientras no exista la del tiempo real: siempre responde "no disponible"
/// (la API lo traduce a 503). Se registra con <c>TryAdd</c> para que la real la reemplace sin conflicto.
/// </summary>
public sealed class UnavailableMatchLauncher : IMatchLauncher
{
    public Task<Guid> LaunchAsync(LaunchMatchRequest request, CancellationToken cancellationToken) =>
        throw new MatchLaunchUnavailableException();
}
