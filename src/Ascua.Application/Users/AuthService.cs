using Ascua.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Ascua.Application.Users;

/// <summary>Casos de uso de autenticación: registro, login, refresh rotativo y logout.</summary>
public sealed partial class AuthService(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    IAccessTokenIssuer accessTokenIssuer,
    SessionPolicy sessionPolicy,
    IClock clock,
    ILogger<AuthService> logger)
{
    // Hash de relleno para que un usuario inexistente tarde lo mismo que una contraseña errónea.
    private static string? s_dummyHash;

    public async Task<UserResult<UserProfile>> RegisterAsync(
        string username, string email, string password, CancellationToken cancellationToken)
    {
        username = username.Trim();
        email = email.Trim();

        if (await users.UsernameExistsAsync(User.Normalize(username), cancellationToken))
        {
            return UserError.UsernameTaken;
        }

        if (await users.EmailExistsAsync(User.Normalize(email), null, cancellationToken))
        {
            return UserError.EmailTaken;
        }

        var user = User.Register(username, email, passwordHasher.Hash(password), clock.UtcNow);
        users.Add(user);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException ex)
        {
            // Carrera entre dos registros simultáneos con el mismo nombre o correo.
            return IsEmailConstraint(ex) ? UserError.EmailTaken : UserError.UsernameTaken;
        }

        LogRegistered(user.Id);
        return UserProfile.From(user);
    }

    public async Task<UserResult<AuthTokens>> LoginAsync(string username, string password, CancellationToken cancellationToken)
    {
        var user = await users.FindByNormalizedUsernameAsync(User.Normalize(username), cancellationToken);

        if (user is null)
        {
            passwordHasher.Verify(password, DummyHash());
            LogLoginFailed(null);
            return UserError.InvalidCredentials;
        }

        if (!passwordHasher.Verify(password, user.PasswordHash))
        {
            LogLoginFailed(user.Id);
            return UserError.InvalidCredentials;
        }

        if (user.IsSuspended)
        {
            LogSuspendedLogin(user.Id);
            return UserError.AccountSuspended;
        }

        var tokens = await StartFamilyAsync(user, cancellationToken);
        LogLoggedIn(user.Id);
        return tokens;
    }

    public async Task<UserResult<AuthTokens>> RefreshAsync(string presentedToken, CancellationToken cancellationToken)
    {
        if (presentedToken.Length > RefreshTokenSecret.MaxEncodedLength)
        {
            return UserError.InvalidRefreshToken;
        }

        var now = clock.UtcNow;
        var current = await refreshTokens.FindByHashAsync(RefreshTokenSecret.Hash(presentedToken), cancellationToken);

        if (current is null)
        {
            return UserError.InvalidRefreshToken;
        }

        if (current.IsRevoked)
        {
            // Un token ya rotado o revocado vuelve a aparecer: alguien lo copió. Se corta toda la familia.
            await refreshTokens.RevokeFamilyAsync(current.FamilyId, now, cancellationToken);
            LogReuseDetected(current.UserId, current.FamilyId);
            return UserError.RefreshTokenReused;
        }

        if (current.IsExpired(now))
        {
            return UserError.InvalidRefreshToken;
        }

        var user = await users.FindByIdAsync(current.UserId, cancellationToken);
        if (user is null || user.IsSuspended)
        {
            await refreshTokens.RevokeFamilyAsync(current.FamilyId, now, cancellationToken);
            return user is null ? UserError.InvalidRefreshToken : UserError.AccountSuspended;
        }

        var (replacement, secret) = NewToken(user.Id, current.FamilyId, now);
        current.MarkReplaced(replacement.Id, now);
        refreshTokens.Add(replacement);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Dos peticiones rotaron el mismo token a la vez: solo una puede ganar; se trata como reutilización.
            await refreshTokens.RevokeFamilyAsync(current.FamilyId, now, cancellationToken);
            LogReuseDetected(current.UserId, current.FamilyId);
            return UserError.RefreshTokenReused;
        }

        return Tokens(user, replacement, secret);
    }

    /// <summary>Revoca la familia del token presentado. Idempotente: un token desconocido no es un error.</summary>
    public async Task LogoutAsync(string presentedToken, CancellationToken cancellationToken)
    {
        if (presentedToken.Length > RefreshTokenSecret.MaxEncodedLength)
        {
            return;
        }

        var current = await refreshTokens.FindByHashAsync(RefreshTokenSecret.Hash(presentedToken), cancellationToken);
        if (current is not null)
        {
            await refreshTokens.RevokeFamilyAsync(current.FamilyId, clock.UtcNow, cancellationToken);
            LogLoggedOut(current.UserId);
        }
    }

    private async Task<AuthTokens> StartFamilyAsync(User user, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var (token, secret) = NewToken(user.Id, Guid.CreateVersion7(now), now);
        refreshTokens.Add(token);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Tokens(user, token, secret);
    }

    private (RefreshToken Token, string Secret) NewToken(Guid userId, Guid familyId, DateTimeOffset now)
    {
        var secret = RefreshTokenSecret.Generate();
        var token = RefreshToken.Issue(userId, familyId, RefreshTokenSecret.Hash(secret), now, sessionPolicy.RefreshTokenLifetime);
        return (token, secret);
    }

    private AuthTokens Tokens(User user, RefreshToken refreshToken, string secret)
    {
        var access = accessTokenIssuer.Issue(user);
        return new AuthTokens(access.Value, access.ExpiresAt, secret, refreshToken.ExpiresAt);
    }

    private string DummyHash() => s_dummyHash ??= passwordHasher.Hash(RefreshTokenSecret.Generate());

    private static bool IsEmailConstraint(UniqueConstraintViolationException ex) =>
        ex.ConstraintName?.Contains("email", StringComparison.OrdinalIgnoreCase) == true;

    [LoggerMessage(Level = LogLevel.Information, Message = "Usuario {UserId} registrado")]
    private partial void LogRegistered(Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Login correcto de {UserId}")]
    private partial void LogLoggedIn(Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Login fallido (usuario {UserId})")]
    private partial void LogLoginFailed(Guid? userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intento de login de la cuenta suspendida {UserId}")]
    private partial void LogSuspendedLogin(Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reutilización de refresh token: se revoca la familia {FamilyId} del usuario {UserId}")]
    private partial void LogReuseDetected(Guid userId, Guid familyId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Logout de {UserId}")]
    private partial void LogLoggedOut(Guid userId);
}
