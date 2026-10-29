namespace Ascua.Application.Users;

// Puertos que implementa Infrastructure. Los casos de uso solo conocen estas interfaces.

public interface IUserRepository
{
    Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<User?> FindByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken);

    Task<bool> UsernameExistsAsync(string normalizedUsername, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string normalizedEmail, Guid? exceptUserId, CancellationToken cancellationToken);

    void Add(User user);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    void Add(RefreshToken token);

    /// <summary>Revoca de inmediato (sin esperar a <see cref="IUnitOfWork"/>) todos los tokens vivos de la familia.</summary>
    Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Revoca de inmediato todos los tokens vivos del usuario (cambio de contraseña).</summary>
    Task RevokeAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">Otra petición modificó la misma fila.</exception>
    /// <exception cref="UniqueConstraintViolationException">Se violó un índice único.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string passwordHash);
}

public interface IAccessTokenIssuer
{
    AccessToken Issue(User user);
}

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>Duración de las sesiones (configurable en <c>Ascua:Jwt</c>).</summary>
public sealed record SessionPolicy(TimeSpan RefreshTokenLifetime);

public sealed class ConcurrencyConflictException(Exception? inner = null)
    : Exception("Otra operación modificó el mismo registro.", inner);

public sealed class UniqueConstraintViolationException(string? constraintName, Exception? inner = null)
    : Exception($"Violación de índice único ({constraintName}).", inner)
{
    public string? ConstraintName { get; } = constraintName;
}
