using Ascua.Application.Matches;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ascua.Infrastructure.Matches;

/// <summary>
/// <see cref="IMatchStore"/> sobre Postgres. Es singleton (lo usan el hub y los temporizadores), así que
/// cada operación abre su propio scope con su <see cref="AscuaDbContext"/>.
/// </summary>
public sealed class EfMatchStore(IServiceScopeFactory scopes) : IMatchStore
{
    private static readonly string[] ActiveStatuses = [MatchStatus.Drafting.ToCode(), MatchStatus.InProgress.ToCode()];

    public async Task<MatchSnapshot?> FindByRoomAsync(Guid roomId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var match = await db.Matches.AsNoTracking().FirstOrDefaultAsync(m => m.RoomId == roomId, cancellationToken);
        return match is null ? null : await ToSnapshotAsync(db, match, cancellationToken);
    }

    public async Task<MatchSnapshot?> LoadAsync(Guid matchId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var match = await db.Matches.AsNoTracking().FirstOrDefaultAsync(m => m.Id == matchId, cancellationToken);
        return match is null ? null : await ToSnapshotAsync(db, match, cancellationToken);
    }

    public async Task<MatchSnapshot> CreateAsync(MatchSnapshot snapshot, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(events);
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
            db.Matches.Add(new MatchRecord
            {
                Id = snapshot.MatchId,
                RoomId = snapshot.RoomId,
                Status = snapshot.Status.ToCode(),
                Version = snapshot.Version,
                LastSeq = snapshot.State.LastSeq,
                Round = snapshot.State.Round,
                State = MatchJson.Serialize(snapshot.State),
                Deadline = snapshot.Deadline,
                RoundDurationMs = (int)snapshot.RoundDuration.TotalMilliseconds,
                CreatedAt = snapshot.CreatedAt,
                UpdatedAt = snapshot.CreatedAt,
            });
            db.MatchPlayers.AddRange(snapshot.Participants.Select(p => new MatchPlayerRecord
            {
                MatchId = snapshot.MatchId,
                UserId = p.UserId,
                Seat = p.Seat,
                Hero = p.Hero.ToString(),
                DisplayName = p.DisplayName,
                CreatedAt = snapshot.CreatedAt,
            }));
            db.MatchEvents.AddRange(events.Select(e => ToRecord(snapshot.MatchId, e, snapshot.CreatedAt)));
            db.MatchStates.Add(ToStateRecord(snapshot, round: 0, fromSeq: 0, snapshot.CreatedAt));

            try
            {
                // SaveChanges ya es transaccional: partida, asientos y eventos entran juntos o nada.
                await db.SaveChangesAsync(cancellationToken);
                return snapshot;
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Otra réplica lanzó la misma sala al mismo tiempo: vale la suya.
            }
        }

        return await FindByRoomAsync(snapshot.RoomId, cancellationToken)
            ?? throw new InvalidOperationException($"La sala {snapshot.RoomId} chocó al crear la partida pero no se encontró.");
    }

    public async Task<bool> CommitAsync(MatchSnapshot previous, MatchSnapshot next, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(events);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.MatchEvents.AddRange(events.Select(e => ToRecord(next.MatchId, e, now)));

        // La instantánea de esta confirmación entra en la misma transacción que sus eventos (reproducción).
        db.MatchStates.Add(ToStateRecord(next, StepRound(previous, events), previous.State.LastSeq, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // UNIQUE(match_id, seq) o (match_id, version): otra réplica ya confirmó. Se aborta sin efectos.
            return false;
        }

        var status = next.Status.ToCode();
        var state = MatchJson.Serialize(next.State);
        var finished = !next.Status.IsActive();
        var result = finished && next.State.Outcome is { } outcome ? MatchJson.Serialize(outcome) : null;
        var finishedAt = finished ? now : (DateTimeOffset?)null;
        var updated = await db.Matches
            .Where(m => m.Id == next.MatchId && m.Version == previous.Version)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(m => m.Status, status)
                    .SetProperty(m => m.Version, next.Version)
                    .SetProperty(m => m.LastSeq, next.State.LastSeq)
                    .SetProperty(m => m.Round, next.State.Round)
                    .SetProperty(m => m.State, state)
                    .SetProperty(m => m.Deadline, next.Deadline)
                    .SetProperty(m => m.Result, m => result ?? m.Result)
                    .SetProperty(m => m.FinishedAt, m => finishedAt ?? m.FinishedAt)
                    .SetProperty(m => m.UpdatedAt, now),
                cancellationToken);
        if (updated == 0)
        {
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkStatusAsync(Guid matchId, MatchStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var code = status.ToCode();
        await db.Matches
            .Where(m => m.Id == matchId && ActiveStatuses.Contains(m.Status))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(m => m.Status, code)
                    .SetProperty(m => m.Version, m => m.Version + 1)
                    .SetProperty(m => m.Deadline, (DateTimeOffset?)null)
                    .SetProperty(m => m.FinishedAt, now)
                    .SetProperty(m => m.UpdatedAt, now),
                cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> FindInactiveAsync(DateTimeOffset before, int limit, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        return await db.Matches
            .Where(m => ActiveStatuses.Contains(m.Status) && m.UpdatedAt < before)
            .OrderBy(m => m.UpdatedAt)
            .Select(m => m.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> FindOverdueAsync(DateTimeOffset before, int limit, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        return await db.Matches
            .Where(m => ActiveStatuses.Contains(m.Status) && m.Deadline < before)
            .OrderBy(m => m.Deadline)
            .Select(m => m.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        return await db.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Username, cancellationToken);
    }

    private static async Task<MatchSnapshot> ToSnapshotAsync(AscuaDbContext db, MatchRecord match, CancellationToken cancellationToken)
    {
        var players = await db.MatchPlayers.AsNoTracking()
            .Where(p => p.MatchId == match.Id)
            .OrderBy(p => p.Seat)
            .ToListAsync(cancellationToken);

        return new MatchSnapshot
        {
            MatchId = match.Id,
            RoomId = match.RoomId,
            Status = MatchStatusExtensions.FromCode(match.Status),
            Version = match.Version,
            State = MatchJson.DeserializeState(match.State),
            Participants = [.. players.Select(p => new MatchParticipant(p.UserId, p.Seat, Enum.Parse<HeroClass>(p.Hero), p.DisplayName))],
            Deadline = match.Deadline,
            RoundDuration = TimeSpan.FromMilliseconds(match.RoundDurationMs),
            CreatedAt = match.CreatedAt,
        };
    }

    private static MatchEventRecord ToRecord(Guid matchId, DomainEvent domainEvent, DateTimeOffset now) => new()
    {
        MatchId = matchId,
        Seq = domainEvent.Seq,
        Round = domainEvent.Round,
        Type = domainEvent.GetType().Name,
        Payload = MatchJson.Serialize(domainEvent),
        CreatedAt = now,
    };

    private static MatchStateRecord ToStateRecord(MatchSnapshot snapshot, int round, long fromSeq, DateTimeOffset now) => new()
    {
        MatchId = snapshot.MatchId,
        Version = snapshot.Version,
        Round = round,
        FromSeq = fromSeq,
        LastSeq = snapshot.State.LastSeq,
        State = MatchJson.Serialize(snapshot.State),
        CreatedAt = now,
    };

    /// <summary>Ronda de una confirmación: 0 mientras dura el draft inicial; si no, la de sus eventos.</summary>
    private static int StepRound(MatchSnapshot previous, IReadOnlyList<DomainEvent> events) =>
        previous.Status == MatchStatus.Drafting ? 0 : events.Count > 0 ? events[^1].Round : previous.State.Round;

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
