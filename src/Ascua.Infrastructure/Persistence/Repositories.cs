using Ascua.Application.Users;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ascua.Infrastructure.Persistence;

public sealed class UserRepository(AscuaDbContext db) : IUserRepository
{
    public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    public Task<User?> FindByNormalizedUsernameAsync(string normalizedUsername, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.NormalizedUsername == normalizedUsername, cancellationToken);

    public Task<bool> UsernameExistsAsync(string normalizedUsername, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.NormalizedUsername == normalizedUsername, cancellationToken);

    public Task<bool> EmailExistsAsync(string normalizedEmail, Guid? exceptUserId, CancellationToken cancellationToken) =>
        db.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail && u.Id != exceptUserId, cancellationToken);

    public void Add(User user) => db.Users.Add(user);
}

public sealed class RefreshTokenRepository(AscuaDbContext db) : IRefreshTokenRepository
{
    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public void Add(RefreshToken token) => db.RefreshTokens.Add(token);

    public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);

    public Task RevokeAllForUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), cancellationToken);
}

public sealed class EfUnitOfWork(AscuaDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            db.ChangeTracker.Clear();
            throw new ConcurrencyConflictException(ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            db.ChangeTracker.Clear();
            throw new UniqueConstraintViolationException(pg.ConstraintName, ex);
        }
    }
}
