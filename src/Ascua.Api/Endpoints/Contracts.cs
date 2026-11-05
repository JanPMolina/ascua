using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Ascua.Application.Users;
using Ascua.Infrastructure.Persistence;

namespace Ascua.Api.Endpoints;

// DTOs de la API REST de usuarios. Las anotaciones se validan con ValidationFilter
// y Microsoft.AspNetCore.OpenApi las publica como minLength/maxLength/pattern en el esquema.

internal static class InputLimits
{
    public const int UsernameMin = 3;
    public const int UsernameMax = AscuaDbContext.UsernameMaxLength;
    public const string UsernamePattern = "^[A-Za-z0-9_]+$";
    public const int EmailMax = AscuaDbContext.EmailMaxLength;

    // NIST SP 800-63B: mínimo 8; se piden 10. El máximo acota el costo de Argon2id.
    public const int PasswordMin = 10;
    public const int PasswordMax = 128;
}

public sealed class RegisterRequest
{
    [Required, StringLength(InputLimits.UsernameMax, MinimumLength = InputLimits.UsernameMin)]
    [RegularExpression(InputLimits.UsernamePattern, ErrorMessage = "Solo letras, dígitos y guion bajo.")]
    [Description("Nombre de usuario único (no distingue mayúsculas).")]
    public string Username { get; init; } = string.Empty;

    [Required, EmailAddress, StringLength(InputLimits.EmailMax)]
    public string Email { get; init; } = string.Empty;

    [Required, StringLength(InputLimits.PasswordMax, MinimumLength = InputLimits.PasswordMin)]
    public string Password { get; init; } = string.Empty;
}

public sealed class LoginRequest
{
    [Required, StringLength(InputLimits.UsernameMax, MinimumLength = 1)]
    public string Username { get; init; } = string.Empty;

    [Required, StringLength(InputLimits.PasswordMax, MinimumLength = 1)]
    public string Password { get; init; } = string.Empty;
}

public sealed class RefreshRequest
{
    [Required, StringLength(RefreshTokenSecret.MaxEncodedLength, MinimumLength = 1)]
    [Description("Refresh token opaco recibido en el último login o refresh.")]
    public string RefreshToken { get; init; } = string.Empty;
}

public sealed class UpdateProfileRequest
{
    [EmailAddress, StringLength(InputLimits.EmailMax)]
    public string? Email { get; init; }

    [StringLength(InputLimits.PasswordMax, MinimumLength = 1)]
    [Description("Obligatoria si se envía newPassword.")]
    public string? CurrentPassword { get; init; }

    [StringLength(InputLimits.PasswordMax, MinimumLength = InputLimits.PasswordMin)]
    [Description("Al cambiarla se cierran todas las sesiones (refresh tokens) del usuario.")]
    public string? NewPassword { get; init; }
}

public sealed record TokenResponse(
    [property: Description("JWT ES256 para el header Authorization: Bearer.")] string AccessToken,
    string TokenType,
    [property: Description("Segundos de vida del token de acceso.")] int ExpiresIn,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt)
{
    public static TokenResponse From(AuthTokens tokens, DateTimeOffset now) => new(
        tokens.AccessToken,
        "Bearer",
        (int)Math.Round((tokens.AccessTokenExpiresAt - now).TotalSeconds),
        tokens.RefreshToken,
        tokens.RefreshTokenExpiresAt);
}

public sealed record UserProfileResponse(
    Guid Id,
    string Username,
    string Email,
    [property: Description("player o admin.")] string Role,
    DateTimeOffset CreatedAt)
{
    public static UserProfileResponse From(UserProfile profile) =>
        new(profile.Id, profile.Username, profile.Email, profile.Role, profile.CreatedAt);
}

public sealed record ActiveMatchResponse(Guid? MatchId);
