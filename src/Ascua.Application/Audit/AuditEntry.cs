namespace Ascua.Application.Audit;

/// <summary>
/// Registro de <c>audit_log</c>: quién hizo qué, sobre qué y por qué. Es append-only: la app solo
/// inserta (no hay métodos para cambiarlo) y un trigger de Postgres rechaza UPDATE y DELETE.
/// <see cref="Metadata"/> es JSON sin datos sensibles (nunca contraseñas, hashes ni tokens).
/// </summary>
public sealed class AuditEntry
{
    public const int ActionMaxLength = 64;
    public const int TargetTypeMaxLength = 32;
    public const int TargetIdMaxLength = 64;
    public const int ReasonMaxLength = 500;
    public const int IpMaxLength = 64;
    public const int CorrelationIdMaxLength = 64;

    // Constructor para EF Core.
    private AuditEntry()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Usuario que actuó; <c>null</c> si fue el sistema (p. ej. el bootstrap del primer admin).</summary>
    public Guid? ActorId { get; private set; }

    public string Action { get; private set; } = null!;

    public string TargetType { get; private set; } = null!;

    public string TargetId { get; private set; } = null!;

    public string? Reason { get; private set; }

    /// <summary>Objeto JSON con datos de contexto no sensibles (ronda, estado anterior…).</summary>
    public string Metadata { get; private set; } = "{}";

    public string? Ip { get; private set; }

    public string? CorrelationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditEntry Create(
        AuditActor actor, string action, string targetType, string targetId, string? reason, string metadataJson, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new AuditEntry
        {
            Id = Guid.CreateVersion7(now),
            ActorId = actor.UserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Reason = reason,
            Metadata = metadataJson,
            Ip = Truncate(actor.Ip, IpMaxLength),
            CorrelationId = Truncate(actor.CorrelationId, CorrelationIdMaxLength),
            CreatedAt = now,
        };
    }

    private static string? Truncate(string? value, int max) => value is { Length: > 0 } v && v.Length > max ? v[..max] : value;
}

/// <summary>Quién origina una acción auditada y desde dónde.</summary>
public sealed record AuditActor(Guid? UserId, string? Ip, string? CorrelationId)
{
    public static readonly AuditActor System = new(null, null, null);
}

/// <summary>Acciones auditadas (estables: se consultan y se documentan en docs/api/administracion.md).</summary>
public static class AuditActions
{
    public const string MatchTerminated = "match.terminated";
    public const string UserSuspended = "user.suspended";
    public const string UserUnsuspended = "user.unsuspended";
    public const string UserPromoted = "user.promoted";
    public const string MatchFullReplayViewed = "match.full_replay_viewed";
}

public static class AuditTargets
{
    public const string Match = "match";
    public const string User = "user";
}

public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, long Total);

/// <summary>
/// Puerto de auditoría. <see cref="Append"/> solo agrega la entrada a la unidad de trabajo actual: se
/// confirma con el mismo <c>IUnitOfWork.SaveChangesAsync</c> que la acción auditada (todo o nada).
/// </summary>
public interface IAuditLog
{
    void Append(AuditEntry entry);

    /// <summary>Más recientes primero.</summary>
    Task<AuditPage> ListAsync(int skip, int take, CancellationToken cancellationToken);
}
