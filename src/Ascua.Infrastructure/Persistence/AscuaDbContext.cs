using Ascua.Application.Users;
using Microsoft.EntityFrameworkCore;

namespace Ascua.Infrastructure.Persistence;

/// <summary>Contexto de EF Core sobre Postgres. Tablas y columnas en snake_case.</summary>
public sealed class AscuaDbContext(DbContextOptions<AscuaDbContext> options) : DbContext(options)
{
    public const int UsernameMaxLength = 32;
    public const int EmailMaxLength = 254;

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.HasKey(u => u.Id);
            user.Property(u => u.Username).HasMaxLength(UsernameMaxLength).IsRequired();
            user.Property(u => u.NormalizedUsername).HasMaxLength(UsernameMaxLength).IsRequired();
            user.Property(u => u.Email).HasMaxLength(EmailMaxLength).IsRequired();
            user.Property(u => u.NormalizedEmail).HasMaxLength(EmailMaxLength).IsRequired();
            user.Property(u => u.PasswordHash).HasMaxLength(256).IsRequired();
            user.Property(u => u.Role)
                .HasConversion(role => role.ToClaimValue(), value => value == UserRoleNames.Admin ? UserRole.Admin : UserRole.Player)
                .HasMaxLength(16)
                .IsRequired();
            user.HasIndex(u => u.NormalizedUsername).IsUnique().HasDatabaseName("ux_users_normalized_username");
            user.HasIndex(u => u.NormalizedEmail).IsUnique().HasDatabaseName("ux_users_normalized_email");
            user.ToTable(t => t.HasCheckConstraint("ck_users_role", "role IN ('player', 'admin')"));
        });

        modelBuilder.Entity<RefreshToken>(token =>
        {
            token.ToTable("refresh_tokens");
            token.HasKey(t => t.Id);
            token.Property(t => t.TokenHash).HasMaxLength(64).IsFixedLength().IsRequired();
            token.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("ux_refresh_tokens_token_hash");
            token.HasIndex(t => t.FamilyId).HasDatabaseName("ix_refresh_tokens_family_id");
            token.HasIndex(t => t.UserId).HasDatabaseName("ix_refresh_tokens_user_id");
            token.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            token.HasOne<RefreshToken>().WithMany().HasForeignKey(t => t.ReplacedByTokenId).OnDelete(DeleteBehavior.SetNull);
            token.Ignore(t => t.IsRevoked);

            // Columna de sistema xmin como token de concurrencia: dos rotaciones simultáneas del mismo
            // refresh token no pueden ganar ambas (la segunda recibe DbUpdateConcurrencyException).
            token.Property<uint>("xmin").IsRowVersion();
        });
    }
}
