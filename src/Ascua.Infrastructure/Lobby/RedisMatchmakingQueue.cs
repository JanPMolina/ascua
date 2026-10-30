using System.Text.Json;
using Ascua.Application.Matches;
using Ascua.Application.Matchmaking;
using Ascua.Application.Rooms;
using Ascua.Domain.Model;
using StackExchange.Redis;

namespace Ascua.Infrastructure.Lobby;

/// <summary>Cola de emparejamiento: sorted set por hora de llegada y scripts Lua atómicos (<see cref="LobbyScripts"/>).</summary>
public sealed class RedisMatchmakingQueue(IConnectionMultiplexer redis) : IMatchmakingQueue
{
    /// <summary>Cuánto tiempo responde <c>EXPIRED</c> el estado de quien salió de la cola por tiempo.</summary>
    private static readonly TimeSpan ExpiredMarkerTtl = TimeSpan.FromMinutes(10);

    private IDatabase Db => redis.GetDatabase();

    public async Task<LobbyError?> EnqueueAsync(QueueTicket ticket, CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new TicketData(ticket.Username, ticket.PreferredHero), LobbyKeys.Json);
        var result = (string?)await Db.ScriptEvaluateAsync(
            LobbyScripts.Enqueue,
            values: [LobbyKeys.Prefix, LobbyKeys.Id(ticket.UserId), ticket.EnqueuedAt.ToUnixTimeMilliseconds(), data]);
        return result == "OK" ? null : LobbyKeys.BusyReason(result!);
    }

    public async Task<LobbyError?> LeaveAsync(Guid userId, CancellationToken cancellationToken)
    {
        var result = (string?)await Db.ScriptEvaluateAsync(LobbyScripts.Leave, values: [LobbyKeys.Prefix, LobbyKeys.Id(userId)]);
        return result == "MATCHING" ? LobbyError.MatchStarting : null;
    }

    public async Task<QueueStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var db = Db;
        var id = LobbyKeys.Id(userId);
        var size = await db.SortedSetLengthAsync(LobbyKeys.Queue);

        if (Guid.TryParse((string?)await db.StringGetAsync(LobbyKeys.ActiveMatch(userId)), out var matchId))
        {
            return new QueueStatus(QueueState.Matched, null, null, size, null, matchId);
        }

        var lobby = (string?)await db.StringGetAsync(LobbyKeys.UserLobby(userId));
        if (lobby is not null && lobby.StartsWith("pending:", StringComparison.Ordinal) && Guid.TryParse(lobby.AsSpan(8), out var groupId))
        {
            var group = await ReadGroupAsync(groupId);
            var me = group?.Players.FirstOrDefault(p => p.UserId == userId);
            if (me is not null)
            {
                return new QueueStatus(QueueState.Matching, me.EnqueuedAt, null, size, me.PreferredHero, null);
            }
        }

        var score = await db.SortedSetScoreAsync(LobbyKeys.Queue, id);
        if (score is { } arrivedAt)
        {
            var rank = await db.SortedSetRankAsync(LobbyKeys.Queue, id);
            var ticket = ParseTicket(await db.HashGetAsync(LobbyKeys.Tickets, id));
            return new QueueStatus(
                QueueState.Queued, DateTimeOffset.FromUnixTimeMilliseconds((long)arrivedAt), rank + 1, size, ticket.PreferredHero, null);
        }

        var state = await db.KeyExistsAsync(LobbyKeys.QueueExpired(userId)) ? QueueState.Expired : QueueState.Idle;
        return new QueueStatus(state, null, null, size, null, null);
    }

    public async Task<MatchGroup?> TryFormGroupAsync(DateTimeOffset now, TimeSpan maxWait, Guid groupId, CancellationToken cancellationToken)
    {
        var result = await Db.ScriptEvaluateAsync(
            LobbyScripts.FormGroup,
            values: [LobbyKeys.Prefix, now.ToUnixTimeMilliseconds(), (long)maxWait.TotalMilliseconds, LobbyKeys.Id(groupId)]);
        return result.IsNull ? null : ParseGroup(groupId, (string)result!);
    }

    public async Task<IReadOnlyList<MatchGroup>> GetStalePendingGroupsAsync(DateTimeOffset formedBefore, CancellationToken cancellationToken)
    {
        var ids = await Db.SortedSetRangeByScoreAsync(LobbyKeys.PendingGroups, double.NegativeInfinity, formedBefore.ToUnixTimeMilliseconds());
        var groups = new List<MatchGroup>(ids.Length);
        foreach (var id in ids)
        {
            if (await ReadGroupAsync(Guid.Parse(id.ToString())) is { } group)
            {
                groups.Add(group);
            }
        }

        return groups;
    }

    public Task CompleteGroupAsync(Guid groupId, Guid matchId, TimeSpan activeMatchTtl, CancellationToken cancellationToken) =>
        Db.ScriptEvaluateAsync(
            LobbyScripts.CompleteGroup,
            values: [LobbyKeys.Prefix, LobbyKeys.Id(groupId), LobbyKeys.Id(matchId), (long)activeMatchTtl.TotalMilliseconds]);

    public Task AbortGroupAsync(Guid groupId, CancellationToken cancellationToken) =>
        Db.ScriptEvaluateAsync(LobbyScripts.AbortGroup, values: [LobbyKeys.Prefix, LobbyKeys.Id(groupId)]);

    public async Task<int> ExpireAsync(DateTimeOffset enqueuedBefore, CancellationToken cancellationToken)
    {
        var result = await Db.ScriptEvaluateAsync(
            LobbyScripts.Expire,
            values: [LobbyKeys.Prefix, enqueuedBefore.ToUnixTimeMilliseconds(), (long)ExpiredMarkerTtl.TotalMilliseconds]);
        return (int)result;
    }

    private async Task<MatchGroup?> ReadGroupAsync(Guid groupId)
    {
        var raw = (string?)await Db.StringGetAsync(LobbyKeys.Group(groupId));
        return raw is null ? null : ParseGroup(groupId, raw);
    }

    /// <summary>Lee el JSON que arma <see cref="LobbyScripts.FormGroup"/>: <c>{formedAt, players: [{id, at, ticket}]}</c>.</summary>
    private static MatchGroup ParseGroup(Guid groupId, string json)
    {
        using var document = JsonDocument.Parse(json);
        var players = new List<QueueTicket>();
        foreach (var player in document.RootElement.GetProperty("players").EnumerateArray())
        {
            var ticket = ParseTicket(player.GetProperty("ticket").GetString());
            var arrivedAt = (long)double.Parse(player.GetProperty("at").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            players.Add(new QueueTicket(
                Guid.Parse(player.GetProperty("id").GetString()!),
                ticket.Username,
                ticket.PreferredHero,
                DateTimeOffset.FromUnixTimeMilliseconds(arrivedAt)));
        }

        return new MatchGroup(groupId, players);
    }

    private static TicketData ParseTicket(RedisValue raw) =>
        raw.IsNullOrEmpty
            ? new TicketData(string.Empty, null)
            : JsonSerializer.Deserialize<TicketData>(raw.ToString(), LobbyKeys.Json) ?? new TicketData(string.Empty, null);

    private sealed record TicketData(string Username, HeroClass? PreferredHero);
}

/// <summary>Marca "partida activa" por jugador (<c>user:{id}:active_match</c>).</summary>
public sealed class RedisActiveMatchRegistry(IConnectionMultiplexer redis) : IActiveMatchRegistry
{
    public async Task<Guid?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        Guid.TryParse((string?)await redis.GetDatabase().StringGetAsync(LobbyKeys.ActiveMatch(userId)), out var id) ? id : null;

    public Task ReleaseAsync(Guid userId, Guid matchId, CancellationToken cancellationToken) =>
        redis.GetDatabase().ScriptEvaluateAsync(
            LobbyScripts.ReleaseActiveMatch, [LobbyKeys.ActiveMatch(userId)], [LobbyKeys.Id(matchId)]);
}
