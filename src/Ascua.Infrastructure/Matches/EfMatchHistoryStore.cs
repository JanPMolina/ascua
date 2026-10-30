using System.Text.Json;
using Ascua.Application.Matches;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ascua.Infrastructure.Matches;

/// <summary><see cref="IMatchHistoryStore"/> sobre Postgres (singleton: cada lectura abre su propio scope).</summary>
public sealed class EfMatchHistoryStore(IServiceScopeFactory scopes) : IMatchHistoryStore
{
    private static readonly Dictionary<string, Type> EventTypes = typeof(DomainEvent).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(DomainEvent)) && !t.IsAbstract)
        .ToDictionary(t => t.Name, StringComparer.Ordinal);

    public async Task<MatchHistoryPage> ListForUserAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();

        // Recorre ix_match_players_user_id_created_at (user_id, created_at DESC, match_id DESC).
        var mine = db.MatchPlayers.AsNoTracking().Where(p => p.UserId == userId);
        var total = await mine.LongCountAsync(cancellationToken);
        var rows = await mine
            .OrderByDescending(p => p.CreatedAt)
            .ThenByDescending(p => p.MatchId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Join(db.Matches.AsNoTracking(), p => p.MatchId, m => m.Id, (p, m) => new
            {
                Player = p,
                m.Status,
                m.CreatedAt,
                m.FinishedAt,
                m.Round,
                m.Result,
                PlayerCount = db.MatchPlayers.Count(other => other.MatchId == m.Id),
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Player.MatchId)
            .Select(r => new MatchHistoryEntry(
                r.Player.MatchId,
                MatchStatusExtensions.FromCode(r.Status),
                r.CreatedAt,
                r.FinishedAt,
                r.Round,
                r.PlayerCount,
                new MatchParticipant(r.Player.UserId, r.Player.Seat, Enum.Parse<HeroClass>(r.Player.Hero), r.Player.DisplayName),
                r.Result is null ? null : MatchJson.Deserialize<GameOutcome>(r.Result)))
            .ToList();
        return new MatchHistoryPage(items, page, pageSize, total);
    }

    public async Task<DateTimeOffset?> GetFinishedAtAsync(Guid matchId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        return await db.Matches.AsNoTracking().Where(m => m.Id == matchId).Select(m => m.FinishedAt).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MatchStep>> GetStepsAsync(Guid matchId, int fromRound, int toRound, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var states = await db.MatchStates.AsNoTracking()
            .Where(s => s.MatchId == matchId && s.Round >= fromRound && s.Round <= toRound)
            .OrderBy(s => s.Version)
            .ToListAsync(cancellationToken);
        if (states.Count == 0)
        {
            return [];
        }

        var firstSeq = states[0].FromSeq;
        var lastSeq = states[^1].LastSeq;
        var events = await db.MatchEvents.AsNoTracking()
            .Where(e => e.MatchId == matchId && e.Seq > firstSeq && e.Seq <= lastSeq)
            .OrderBy(e => e.Seq)
            .ToListAsync(cancellationToken);

        return [.. states.Select(s => new MatchStep(
            s.Version,
            s.Round,
            s.FromSeq,
            MatchJson.DeserializeState(s.State),
            [.. events.Where(e => e.Seq > s.FromSeq && e.Seq <= s.LastSeq).Select(ToDomainEvent)]))];
    }

    private static DomainEvent ToDomainEvent(MatchEventRecord record) =>
        EventTypes.TryGetValue(record.Type, out var type)
            ? (DomainEvent)(JsonSerializer.Deserialize(record.Payload, type, MatchJson.Options)
                ?? throw new InvalidOperationException($"Evento vacío (partida {record.MatchId}, seq {record.Seq})."))
            : throw new InvalidOperationException($"Tipo de evento desconocido '{record.Type}' (partida {record.MatchId}, seq {record.Seq}).");
}
