namespace Ascua.Application.Users;

/// <summary>
/// Refresh token rotativo. Solo se guarda el hash SHA-256 del valor entregado al cliente.
/// Todos los tokens nacidos de un mismo login comparten <see cref="FamilyId"/>: si se presenta
/// uno ya rotado, se revoca la familia completa (detección de reutilización).
/// </summary>
public sealed class RefreshToken
{
    // Constructor para EF Core.
    private RefreshToken()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid FamilyId { get; private set; }

    /// <summary>SHA-256 del token en hexadecimal (el token tiene 256 bits de entropía, no necesita sal).</summary>
    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Token que lo reemplazó al rotar; null si se revocó por logout o por reutilización.</summary>
    public Guid? ReplacedByTokenId { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public static RefreshToken Issue(Guid userId, Guid familyId, string tokenHash, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = Guid.CreateVersion7(now),
        UserId = userId,
        FamilyId = familyId,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
    };

    public void MarkReplaced(Guid replacementId, DateTimeOffset now)
    {
        RevokedAt = now;
        ReplacedByTokenId = replacementId;
    }
}
