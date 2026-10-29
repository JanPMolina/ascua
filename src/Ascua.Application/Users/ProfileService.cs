using Ascua.Application.Abstractions;

namespace Ascua.Application.Users;

/// <summary>Casos de uso del perfil propio (<c>/api/v1/users/me</c>).</summary>
public sealed class ProfileService(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IUnitOfWork unitOfWork,
    IPasswordHasher passwordHasher,
    IClock clock)
{
    public async Task<UserResult<UserProfile>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return UserError.UserNotFound;
        }

        return user.IsSuspended ? UserError.AccountSuspended : UserProfile.From(user);
    }

    /// <summary>
    /// Cambia el correo y/o la contraseña. Cambiar la contraseña exige la actual y revoca
    /// todas las sesiones (refresh tokens) del usuario.
    /// </summary>
    public async Task<UserResult<UserProfile>> UpdateAsync(
        Guid userId, string? email, string? currentPassword, string? newPassword, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return UserError.UserNotFound;
        }

        if (user.IsSuspended)
        {
            return UserError.AccountSuspended;
        }

        var now = clock.UtcNow;

        if (newPassword is not null)
        {
            if (currentPassword is null || !passwordHasher.Verify(currentPassword, user.PasswordHash))
            {
                return UserError.InvalidCurrentPassword;
            }

            user.ChangePasswordHash(passwordHasher.Hash(newPassword), now);
        }

        if (email is not null)
        {
            email = email.Trim();
            var normalized = User.Normalize(email);
            if (normalized != user.NormalizedEmail)
            {
                if (await users.EmailExistsAsync(normalized, user.Id, cancellationToken))
                {
                    return UserError.EmailTaken;
                }

                user.ChangeEmail(email, now);
            }
        }

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (UniqueConstraintViolationException)
        {
            return UserError.EmailTaken;
        }

        if (newPassword is not null)
        {
            await refreshTokens.RevokeAllForUserAsync(user.Id, now, cancellationToken);
        }

        return UserProfile.From(user);
    }
}
