using Ascua.Application.Audit;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Application.Matches;

/// <summary>Persistencia durable (Postgres): partidas, asientos y eventos append-only.</summary>
public interface IMatchStore
{
    Task<MatchSnapshot?> FindByRoomAsync(Guid roomId, CancellationToken cancellationToken);

    Task<MatchSnapshot?> LoadAsync(Guid matchId, CancellationToken cancellationToken);

    /// <summary>
    /// Inserta la partida, sus asientos y sus eventos iniciales. Si otra llamada ya creó la partida de
    /// esa sala (<c>room_id</c> único), no inserta nada y devuelve la existente.
    /// </summary>
    Task<MatchSnapshot> CreateAsync(MatchSnapshot snapshot, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken);

    /// <summary>
    /// En una transacción: inserta <paramref name="events"/> (<c>UNIQUE(match_id, seq)</c>) y actualiza
    /// la partida si su versión sigue siendo la de <paramref name="previous"/>. Devuelve <c>false</c>
    /// sin efectos si otra réplica confirmó antes (versión distinta o seq repetido).
    /// </summary>
    Task<bool> CommitAsync(MatchSnapshot previous, MatchSnapshot next, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken);

    /// <summary>
    /// Marca un estado terminal fuera del dominio (FAULTED, EXPIRED, TERMINATED) sin exigir versión. Si se
    /// pasa <paramref name="audit"/>, se inserta en la misma transacción. Devuelve si la partida seguía viva.
    /// </summary>
    Task<bool> MarkStatusAsync(Guid matchId, MatchStatus status, DateTimeOffset now, AuditEntry? audit, CancellationToken cancellationToken);

    /// <summary>Partidas vivas sin cambios desde <paramref name="before"/> (para expirarlas).</summary>
    Task<IReadOnlyList<Guid>> FindInactiveAsync(DateTimeOffset before, int limit, CancellationToken cancellationToken);

    /// <summary>Partidas vivas cuyo plazo ya venció hace rato (Redis las perdió o nadie las cerró).</summary>
    Task<IReadOnlyList<Guid>> FindOverdueAsync(DateTimeOffset before, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

public enum SubmitStatus
{
    Accepted,
    Duplicate,
    AlreadySubmitted,
    RoundClosed,
}

public sealed record SubmitResult(SubmitStatus Status, OrderAck? Ack, int DecidedCount);

/// <summary>Estado caliente compartido entre réplicas (Redis).</summary>
public interface IMatchHotState
{
    Task<MatchSnapshot?> GetAsync(Guid matchId, CancellationToken cancellationToken);

    /// <summary>Guarda la instantánea si es más nueva que la guardada y (re)programa su plazo (<see cref="MatchSnapshot.WakeAt"/>).</summary>
    Task SaveAsync(MatchSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>Lock exclusivo por partida (<c>SET NX PX</c>). <c>null</c> si no se obtuvo dentro de <paramref name="wait"/>.</summary>
    Task<IAsyncDisposable?> TryLockAsync(Guid matchId, TimeSpan wait, CancellationToken cancellationToken);

    Task<OrderAck?> FindAckAsync(Guid matchId, Guid clientActionId, CancellationToken cancellationToken);

    /// <summary>
    /// Registra la orden de forma atómica: idempotencia por <c>clientActionId</c>, una sola orden por
    /// jugador y ronda, y rechazo si la ronda ya fue sellada por el cerrador.
    /// </summary>
    Task<SubmitResult> SubmitOrderAsync(OrderAck ack, PlayerId player, Order order, CancellationToken cancellationToken);

    /// <summary>Sella la ronda (las órdenes que lleguen después se rechazan) y devuelve las registradas.</summary>
    Task<IReadOnlyDictionary<PlayerId, Order>> SealOrdersAsync(Guid matchId, int round, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<PlayerId, Order>> GetOrdersAsync(Guid matchId, int round, CancellationToken cancellationToken);

    Task<DraftAck?> FindDraftAckAsync(Guid matchId, Guid userId, Guid offerId, CancellationToken cancellationToken);

    Task SaveDraftAckAsync(Guid userId, DraftAck ack, CancellationToken cancellationToken);

    Task<PathAck?> FindPathAckAsync(Guid matchId, Guid userId, Guid offerId, CancellationToken cancellationToken);

    Task SavePathAckAsync(Guid userId, PathAck ack, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> DueDeadlinesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task RemoveDeadlineAsync(Guid matchId, CancellationToken cancellationToken);

    Task<long> ScheduledMatchCountAsync(CancellationToken cancellationToken);

    /// <summary>Asocia la conexión vigente del usuario en la partida; devuelve la anterior (si había) y si estaba en gracia.</summary>
    Task<(string? PreviousConnectionId, bool WasAway)> MarkConnectedAsync(Guid matchId, Guid userId, string connectionId, CancellationToken cancellationToken);

    /// <summary>Si <paramref name="connectionId"/> era la vigente, la quita y abre la gracia. Devuelve si lo hizo.</summary>
    Task<bool> MarkDisconnectedAsync(Guid matchId, Guid userId, string connectionId, DateTimeOffset graceDeadline, CancellationToken cancellationToken);

    /// <summary>Programa una gracia sin conexión previa (al crear la partida: plazo para entrar).</summary>
    Task ScheduleGraceAsync(Guid matchId, Guid userId, DateTimeOffset deadline, CancellationToken cancellationToken);

    Task<IReadOnlySet<Guid>> ConnectedUsersAsync(Guid matchId, CancellationToken cancellationToken);

    /// <summary>Gracias vencidas. Cada una se reclama de forma atómica: solo una réplica la procesa.</summary>
    Task<IReadOnlyList<(Guid MatchId, Guid UserId)>> ClaimDueGracesAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

/// <summary>Difusión a los jugadores. Cada jugador recibe solo su vista (la proyecta la implementación).</summary>
public interface IMatchNotifier
{
    Task PlayerDecidedAsync(MatchSnapshot snapshot, Guid userId, int decidedCount, int expectedPlayers, CancellationToken cancellationToken);

    Task MatchChangedAsync(MatchChange change, CancellationToken cancellationToken);

    Task PlayerConnectionChangedAsync(Guid matchId, Guid userId, ConnectionState state, DateTimeOffset? graceDeadline, CancellationToken cancellationToken);

    Task MatchStoppedAsync(MatchSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>Una confirmación: estado anterior, nuevo y eventos (verdad completa; se filtran por jugador al difundir).</summary>
public sealed record MatchChange(
    MatchSnapshot Before,
    MatchSnapshot After,
    IReadOnlyList<DomainEvent> Events,
    bool RoundResolved,
    IReadOnlySet<PlayerId> DefaultGuards);

/// <summary>Tickets de un solo uso para el handshake WebSocket (ADR-0006).</summary>
public interface IRealtimeTicketStore
{
    Task IssueAsync(string ticket, Guid userId, Guid matchId, TimeSpan lifetime, CancellationToken cancellationToken);

    /// <summary>Consume el ticket (<c>GETDEL</c>): un segundo intento devuelve <c>null</c>.</summary>
    Task<(Guid UserId, Guid MatchId)?> ConsumeAsync(string ticket, CancellationToken cancellationToken);
}

/// <summary>Máximo de conexiones simultáneas por usuario, compartido entre réplicas.</summary>
public interface IConnectionLimiter
{
    Task<bool> TryAcquireAsync(Guid userId, string leaseId, int max, CancellationToken cancellationToken);

    Task ReleaseAsync(Guid userId, string leaseId, CancellationToken cancellationToken);

    /// <summary>Conexiones vivas del usuario en todas las réplicas (arriendos sin vencer).</summary>
    Task<int> CountAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Renueva las conexiones vivas de esta réplica (las de una réplica caída expiran solas).</summary>
    Task RenewAsync(IEnumerable<(Guid UserId, string LeaseId)> leases, CancellationToken cancellationToken);
}
