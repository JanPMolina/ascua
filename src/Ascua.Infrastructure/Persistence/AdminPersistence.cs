using Ascua.Application.Admin;
using Ascua.Application.Audit;
using Ascua.Application.Matches;
using Microsoft.EntityFrameworkCore;

namespace Ascua.Infrastructure.Persistence;

/// <summary>
/// <see cref="IAuditLog"/> sobre el <see cref="AscuaDbContext"/> del scope: <see cref="Append"/> solo agrega
/// la entrada; se confirma con el mismo <c>SaveChanges</c> que la acción auditada.
/// </summary>
public sealed class EfAuditLog(AscuaDbContext db) : IAuditLog
{
    public void Append(AuditEntry entry) => db.AuditLog.Add(entry);

    public async Task<AuditPage> ListAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var total = await db.AuditLog.LongCountAsync(cancellationToken);
        var items = await db.AuditLog.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return new AuditPage(items, total);
    }
}

public sealed class EfAdminQueries(AscuaDbContext db) : IAdminQueries
{
    private static readonly string[] ActiveStatuses = [MatchStatus.Drafting.ToCode(), MatchStatus.InProgress.ToCode()];

    public async Task<AdminMatchPage> ListActiveMatchesAsync(int skip, int take, CancellationToken cancellationToken)
    {
        var active = db.Matches.AsNoTracking().Where(m => ActiveStatuses.Contains(m.Status));
        var total = await active.LongCountAsync(cancellationToken);
        var matches = await active
            .OrderByDescending(m => m.UpdatedAt)
            .Skip(skip)
            .Take(take)
            .Select(m => new { m.Id, m.Status, m.Round, m.CreatedAt, m.UpdatedAt })
            .ToListAsync(cancellationToken);

        var ids = matches.Select(m => m.Id).ToList();
        var players = await db.MatchPlayers.AsNoTracking()
            .Where(p => ids.Contains(p.MatchId))
            .OrderBy(p => p.Seat)
            .ToListAsync(cancellationToken);
        var byMatch = players.ToLookup(p => p.MatchId);

        var rows = matches
            .Select(m => new AdminMatchRow(
                m.Id,
                m.Status,
                m.Round,
                [.. byMatch[m.Id].Select(p => new AdminMatchPlayer(p.UserId, p.DisplayName, p.Hero, p.Seat))],
                m.CreatedAt,
                m.UpdatedAt))
            .ToList();
        return new AdminMatchPage(rows, total);
    }
}
