namespace Ascua.Application.Users;

/// <summary>Cuenta de jugador o administrador. El mapeo a Postgres vive en Infrastructure/Persistence.</summary>
public sealed class User
{
    // Constructor para EF Core.
    private User()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Nombre visible tal como lo escribió el usuario.</summary>
    public string Username { get; private set; } = null!;

    /// <summary>Forma canónica (minúsculas) con índice único: "Ana" y "ana" son el mismo usuario.</summary>
    public string NormalizedUsername { get; private set; } = null!;

    public string Email { get; private set; } = null!;

    public string NormalizedEmail { get; private set; } = null!;

    /// <summary>Hash Argon2id en formato PHC; la contraseña en claro nunca se guarda.</summary>
    public string PasswordHash { get; private set; } = null!;

    public UserRole Role { get; private set; }

    public bool IsSuspended { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static User Register(string username, string email, string passwordHash, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Username = username,
        NormalizedUsername = Normalize(username),
        Email = email,
        NormalizedEmail = Normalize(email),
        PasswordHash = passwordHash,
        Role = UserRole.Player,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public static string Normalize(string value) => value.Trim().ToLowerInvariant();

    public void ChangeEmail(string email, DateTimeOffset now)
    {
        Email = email;
        NormalizedEmail = Normalize(email);
        UpdatedAt = now;
    }

    public void ChangePasswordHash(string passwordHash, DateTimeOffset now)
    {
        PasswordHash = passwordHash;
        UpdatedAt = now;
    }

    public void Suspend(DateTimeOffset now)
    {
        IsSuspended = true;
        UpdatedAt = now;
    }

    public void Reinstate(DateTimeOffset now)
    {
        IsSuspended = false;
        UpdatedAt = now;
    }
}
