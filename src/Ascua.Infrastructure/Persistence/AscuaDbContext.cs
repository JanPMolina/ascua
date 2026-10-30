using Ascua.Application.Audit;
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

    public DbSet<MatchRecord> Matches => Set<MatchRecord>();

    public DbSet<MatchPlayerRecord> MatchPlayers => Set<MatchPlayerRecord>();

    public DbSet<MatchEventRecord> MatchEvents => Set<MatchEventRecord>();

    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();

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

        modelBuilder.Entity<MatchRecord>(match =>
        {
            match.ToTable("matches", t =>
                t.HasCheckConstraint("ck_matches_status", "status IN ('DRAFTING', 'IN_PROGRESS', 'FINISHED', 'ABANDONED', 'EXPIRED', 'FAULTED', 'TERMINATED')"));
            match.HasKey(m => m.Id);
            match.Property(m => m.Status).HasMaxLength(16).IsRequired();
            match.Property(m => m.State).HasColumnType("jsonb").IsRequired();
            match.Property(m => m.Result).HasColumnType("jsonb");
            match.HasIndex(m => m.RoomId).IsUnique().HasDatabaseName("ux_matches_room_id");
            match.HasIndex(m => new { m.Status, m.UpdatedAt }).HasDatabaseName("ix_matches_status_updated_at");
        });

        modelBuilder.Entity<MatchPlayerRecord>(player =>
        {
            player.ToTable("match_players");
            player.HasKey(p => new { p.MatchId, p.UserId });
            player.Property(p => p.Hero).HasMaxLength(16).IsRequired();
            player.Property(p => p.DisplayName).HasMaxLength(UsernameMaxLength).IsRequired();
            player.HasIndex(p => p.UserId).HasDatabaseName("ix_match_players_user_id");
            player.HasOne<MatchRecord>().WithMany().HasForeignKey(p => p.MatchId).OnDelete(DeleteBehavior.Cascade);
            player.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MatchEventRecord>(matchEvent =>
        {
            matchEvent.ToTable("match_events");

            // La clave primaria es UNIQUE(match_id, seq): la garantía final de un solo cerrador por ronda.
            matchEvent.HasKey(e => new { e.MatchId, e.Seq }).HasName("ux_match_events_match_id_seq");
            matchEvent.Property(e => e.Type).HasMaxLength(64).IsRequired();
            matchEvent.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
            matchEvent.HasOne<MatchRecord>().WithMany().HasForeignKey(e => e.MatchId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditEntry>(audit =>
        {
            // Append-only: un trigger (migración AdminAudit) rechaza UPDATE y DELETE. Sin FK a users a propósito:
            // la auditoría debe sobrevivir aunque se borre la cuenta del actor o del objetivo.
            audit.ToTable("audit_log");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Action).HasMaxLength(AuditEntry.ActionMaxLength).IsRequired();
            audit.Property(a => a.TargetType).HasMaxLength(AuditEntry.TargetTypeMaxLength).IsRequired();
            audit.Property(a => a.TargetId).HasMaxLength(AuditEntry.TargetIdMaxLength).IsRequired();
            audit.Property(a => a.Reason).HasMaxLength(AuditEntry.ReasonMaxLength);
            audit.Property(a => a.Metadata).HasColumnType("jsonb").IsRequired();
            audit.Property(a => a.Ip).HasMaxLength(AuditEntry.IpMaxLength);
            audit.Property(a => a.CorrelationId).HasMaxLength(AuditEntry.CorrelationIdMaxLength);
            audit.HasIndex(a => a.CreatedAt).HasDatabaseName("ix_audit_log_created_at");
            audit.HasIndex(a => new { a.TargetType, a.TargetId }).HasDatabaseName("ix_audit_log_target");
            audit.HasIndex(a => a.ActorId).HasDatabaseName("ix_audit_log_actor_id");
        });
    }
}
