namespace Ascua.Application.Matches;

/// <summary>Sección <c>Ascua:Realtime</c>: plazos y límites del tiempo real.</summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Ascua:Realtime";

    /// <summary>Planificación de cada ronda (20 s según las reglas).</summary>
    public TimeSpan RoundDuration { get; set; } = TimeSpan.FromSeconds(11);

    /// <summary>Plazo del draft inicial; al vencer se elige la primera opción.</summary>
    public TimeSpan DraftDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Plazo para elegir camino tras extinguirse; al vencer se toma Amistad (04-caminos-tras-morir.md).</summary>
    public TimeSpan PathChoiceDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Repique del Campanero: en esa ronda los demás solo tienen este tiempo para decidir (05-heroes.md §12).</summary>
    public TimeSpan BellDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Señales rápidas: como mucho 1 por jugador cada este tiempo (además del límite del hub).</summary>
    public TimeSpan SignalInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Gracia de reconexión; al vencer se aplica la regla de abandono.</summary>
    public TimeSpan ReconnectGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Plazo para entrar a una partida recién creada antes de contar como abandono.</summary>
    public TimeSpan JoinTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Una partida viva sin cambios durante este tiempo pasa a EXPIRED.</summary>
    public TimeSpan InactivityTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Vida del lock de cierre (<c>PX</c>); acota cuánto bloquea una réplica que muere con él.</summary>
    public TimeSpan LockLifetime { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Cada cuánto el temporizador de cada réplica revisa plazos y gracias vencidos.</summary>
    public TimeSpan TimerInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Además del sondeo, la réplica que abre un plazo programa un temporizador en proceso para cerrarlo
    /// en el instante exacto (sin esperar hasta <see cref="TimerInterval"/>). El sondeo queda de respaldo
    /// si esa réplica cae. Ver docs/rendimiento.md.
    /// </summary>
    public bool PreciseDeadlines { get; set; } = true;

    /// <summary>Cada cuánto se buscan partidas inactivas o con plazos perdidos en Postgres.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxConnectionsPerUser { get; set; } = 3;

    /// <summary>Token bucket por conexión del hub: capacidad.</summary>
    public int HubTokenLimit { get; set; } = 20;

    /// <summary>Token bucket por conexión del hub: recarga por segundo.</summary>
    public int HubTokensPerSecond { get; set; } = 5;

    /// <summary>Rechazos <c>RATE_LIMITED</c> en 60 s a partir de los cuales se cierra la conexión.</summary>
    public int HubAbuseThreshold { get; set; } = 50;

    /// <summary>Base pública para armar <c>replayUrl</c> en <c>MatchFinished</c>.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>Espera entre <c>ServerDraining</c> y el cierre de la réplica.</summary>
    public TimeSpan DrainDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Habilita <c>POST /api/v1/dev/matches</c> (solo local/dev; arranca una partida sin sala).</summary>
    public bool EnableDevEndpoints { get; set; }

    /// <summary>
    /// Solo para pruebas de integración: la partida termina en derrota al resolver esta ronda. Por defecto no hay límite
    /// (07-version-4.md §7); no se configura en ningún entorno real.
    /// </summary>
    public int? TestRoundLimit { get; set; }
}

/// <summary>Códigos de error del contrato (contracts/asyncapi.yaml, sección 3).</summary>
public static class RealtimeErrors
{
    public const string InvalidPayload = "INVALID_PAYLOAD";
    public const string InvalidOrder = "INVALID_ORDER";
    public const string StaleRound = "STALE_ROUND";
    public const string OrderAlreadySubmitted = "ORDER_ALREADY_SUBMITTED";
    public const string OrderTooLate = "ORDER_TOO_LATE";
    public const string NotParticipant = "NOT_PARTICIPANT";
    public const string NotJoined = "NOT_JOINED";
    public const string PlayerDown = "PLAYER_DOWN";
    public const string MatchNotFound = "MATCH_NOT_FOUND";
    public const string MatchNotActive = "MATCH_NOT_ACTIVE";
    public const string DraftClosed = "DRAFT_CLOSED";
    public const string InvalidPick = "INVALID_PICK";
    public const string PathClosed = "PATH_CLOSED";
    public const string PactClosed = "PACT_CLOSED";
    public const string RateLimited = "RATE_LIMITED";
    public const string InternalError = "INTERNAL_ERROR";
}

/// <summary>Error tipado de una operación de partida. Nunca cierra la partida.</summary>
public sealed record MatchError(string Code, string Message)
{
    public int? CurrentRound { get; init; }
    public MatchStatus? Status { get; init; }
    public string? Field { get; init; }
}

/// <summary>Resultado de una operación: valor o error tipado.</summary>
public sealed record MatchResult<T>(T? Value, MatchError? Error)
{
    public static implicit operator MatchResult<T>(MatchError error) => new(default, error);

    public static MatchResult<T> Ok(T value) => new(value, null);
}
