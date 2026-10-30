namespace Ascua.Application.Admin;

// Puertos de la administración. Los implementan Infrastructure (Postgres/Redis) y la API (conexiones en vivo).

public sealed record AdminMatchPlayer(Guid UserId, string DisplayName, string Hero, int Seat);

/// <summary>Fila de una partida viva tal como está en Postgres (la presencia en vivo se agrega aparte).</summary>
public sealed record AdminMatchRow(
    Guid MatchId,
    string Status,
    int Round,
    IReadOnlyList<AdminMatchPlayer> Players,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminMatchPage(IReadOnlyList<AdminMatchRow> Items, long Total);

public interface IAdminQueries
{
    /// <summary>Partidas en DRAFTING o IN_PROGRESS, la actividad más reciente primero.</summary>
    Task<AdminMatchPage> ListActiveMatchesAsync(int skip, int take, CancellationToken cancellationToken);
}

/// <summary>
/// Estado de acceso de un usuario consultado en cada petición autenticada (caché corta en Redis con
/// respaldo en Postgres). Al suspender se escribe de inmediato, así que un access token vigente deja de
/// servir en la siguiente petición, en cualquier réplica.
/// </summary>
public interface IUserAccessGate
{
    Task<bool> IsSuspendedAsync(Guid userId, CancellationToken cancellationToken);

    Task SetSuspendedAsync(Guid userId, bool suspended, CancellationToken cancellationToken);
}

/// <summary>Corta conexiones de tiempo real en todas las réplicas avisando antes <c>KICKED_BY_ADMIN</c>.</summary>
public interface IRealtimeKicker
{
    Task KickUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Corta las conexiones de la partida tras un breve margen para que llegue <c>MatchFinished</c>.</summary>
    Task KickMatchAsync(Guid matchId, CancellationToken cancellationToken);
}
