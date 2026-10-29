namespace Ascua.Application.Users;

/// <summary>Errores de negocio de usuarios. La API los traduce a Problem Details con tipos estables.</summary>
public enum UserError
{
    InvalidCredentials,
    InvalidRefreshToken,
    RefreshTokenReused,
    AccountSuspended,
    UsernameTaken,
    EmailTaken,
    InvalidCurrentPassword,
    UserNotFound,
}

/// <summary>Resultado de un caso de uso: un valor o un <see cref="UserError"/>.</summary>
public readonly record struct UserResult<T>
{
    private UserResult(T? value, UserError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }

    public UserError? Error { get; }

    public static UserResult<T> Ok(T value) => new(value, null);

    public static UserResult<T> Fail(UserError error) => new(default, error);

    public static implicit operator UserResult<T>(T value) => Ok(value);

    public static implicit operator UserResult<T>(UserError error) => Fail(error);
}

public sealed record UserProfile(Guid Id, string Username, string Email, string Role, DateTimeOffset CreatedAt)
{
    public static UserProfile From(User user) =>
        new(user.Id, user.Username, user.Email, user.Role.ToClaimValue(), user.CreatedAt);
}

public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);
