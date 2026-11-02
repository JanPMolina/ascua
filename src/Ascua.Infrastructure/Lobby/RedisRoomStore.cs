using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ascua.Application.Rooms;
using Ascua.Domain.Model;
using Ascua.Infrastructure.Matches;
using StackExchange.Redis;
using Room = Ascua.Application.Rooms.Room;

namespace Ascua.Infrastructure.Lobby;

internal static class LobbyKeys
{
    public const string Prefix = "ascua:";

    public static RedisKey Room(Guid roomId) => $"{Prefix}room:{roomId:N}";

    public static RedisKey InviteCode(string code) => $"{Prefix}roomcode:{code}";

    public static RedisKey PublicRooms => $"{Prefix}rooms:public";

    public static RedisKey UserLobby(Guid userId) => $"{Prefix}user:{userId:N}:lobby";

    public static RedisKey ActiveMatch(Guid userId) => $"{Prefix}user:{userId:N}:active_match";

    public static RedisKey QueueExpired(Guid userId) => $"{Prefix}user:{userId:N}:mm_expired";

    public static RedisKey Queue => $"{Prefix}mm:queue";

    public static RedisKey Tickets => $"{Prefix}mm:tickets";

    public static RedisKey Group(Guid groupId) => $"{Prefix}mm:group:{groupId:N}";

    public static RedisKey PendingGroups => $"{Prefix}mm:pending";

    public static string Id(Guid id) => id.ToString("N");

    public static LobbyError BusyReason(string reason) => reason switch
    {
        "MATCH" => LobbyError.AlreadyInMatch,
        "ROOM" => LobbyError.AlreadyInRoom,
        "QUEUE" => LobbyError.AlreadyInQueue,
        _ => throw new InvalidOperationException($"Motivo desconocido del script: {reason}."),
    };

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Salas en Redis: un hash por sala (versión + JSON) y escrituras con <see cref="LobbyScripts.SaveRoom"/>.</summary>
public sealed class RedisRoomStore(IConnectionMultiplexer redis, LobbyPolicy policy) : IRoomStore
{
    private IDatabase Db => redis.GetDatabase();

    public async Task<Room?> GetAsync(Guid roomId, CancellationToken cancellationToken)
    {
        var fields = await Db.HashGetAsync(LobbyKeys.Room(roomId), ["v", "data"]);
        if (fields[1].IsNull)
        {
            return null;
        }

        try
        {
            return Deserialize(roomId, (long)fields[0], fields[1]!);
        }
        catch (JsonException)
        {
            // Sala guardada con héroes que ya no existen (ADR-0008): para el lobby es como si no estuviera.
            return null;
        }
    }

    public async Task<Guid?> FindIdByInviteCodeAsync(string inviteCode, CancellationToken cancellationToken)
    {
        var value = await Db.StringGetAsync(LobbyKeys.InviteCode(inviteCode));
        return Guid.TryParse(value.ToString(), out var id) ? id : null;
    }

    public async Task<Guid?> FindRoomIdOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var lobby = (string?)await Db.StringGetAsync(LobbyKeys.UserLobby(userId));
        return lobby is not null && lobby.StartsWith("room:", StringComparison.Ordinal) && Guid.TryParse(lobby.AsSpan(5), out var id)
            ? id
            : null;
    }

    public async Task<RoomPage> ListPublicAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        var db = Db;
        var ids = await db.SortedSetRangeByRankAsync(LobbyKeys.PublicRooms, offset, offset + limit - 1);
        var rooms = new List<Room>(ids.Length);
        foreach (var raw in ids)
        {
            var id = Guid.Parse(raw.ToString());
            var room = await GetAsync(id, cancellationToken);
            if (room is { IsListed: true })
            {
                rooms.Add(room);
            }
            else if (room is null)
            {
                // La sala venció por TTL: se limpia el índice de forma perezosa.
                await db.SortedSetRemoveAsync(LobbyKeys.PublicRooms, raw);
            }
        }

        return new RoomPage(rooms, await db.SortedSetLengthAsync(LobbyKeys.PublicRooms));
    }

    public async Task<RoomSaveResult> SaveAsync(Room? expected, Room? updated, CancellationToken cancellationToken)
    {
        var room = updated ?? expected ?? throw new ArgumentException("Hace falta la sala anterior o la nueva.");
        var before = expected?.Members.Select(m => m.UserId).ToHashSet() ?? [];
        var after = updated?.Members.Select(m => m.UserId).ToList() ?? [];
        var added = after.Where(id => !before.Contains(id)).ToList();
        var kept = after.Where(before.Contains).ToList();
        var removed = before.Where(id => !after.Contains(id)).ToList();
        var startedMatch = updated is { Status: RoomStatus.Started, MatchId: { } matchId } ? LobbyKeys.Id(matchId) : string.Empty;

        var args = new List<RedisValue>
        {
            LobbyKeys.Prefix,
            LobbyKeys.Id(room.Id),
            (expected?.Version ?? 0).ToString(CultureInfo.InvariantCulture),
            updated is null ? string.Empty : JsonSerializer.Serialize(RoomData.From(updated), LobbyKeys.Json),
            (long)policy.RoomTtl.TotalMilliseconds,
            updated?.IsListed == true ? "1" : "0",
            room.CreatedAt.ToUnixTimeMilliseconds(),
            room.InviteCode ?? string.Empty,
            startedMatch,
            (long)policy.ActiveMatchTtl.TotalMilliseconds,
            added.Count,
            kept.Count,
            removed.Count,
        };
        args.AddRange(added.Concat(kept).Concat(removed).Select(id => (RedisValue)LobbyKeys.Id(id)));

        var result = (RedisResult[])(await Db.ScriptEvaluateAsync(LobbyScripts.SaveRoom, values: [.. args]))!;
        return (string)result[0]! switch
        {
            "OK" => RoomSaveResult.Saved(updated is null ? null : updated with { Version = (long)result[1] }),
            "CONFLICT" => new RoomSaveResult(RoomSaveOutcome.VersionConflict),
            "CODE" => new RoomSaveResult(RoomSaveOutcome.InviteCodeTaken),
            "BUSY" => new RoomSaveResult(RoomSaveOutcome.UserBusy, BusyReason: LobbyKeys.BusyReason((string)result[1]!)),
            var other => throw new InvalidOperationException($"Respuesta inesperada del script: {other}."),
        };
    }

    private static Room Deserialize(Guid id, long version, string json)
    {
        var data = JsonSerializer.Deserialize<RoomData>(json, LobbyKeys.Json)
            ?? throw new InvalidOperationException($"Sala {id} corrupta.");
        return data.ToRoom(version);
    }

    /// <summary>Forma persistida de la sala (la versión vive aparte, en el campo <c>v</c> del hash).</summary>
    private sealed record RoomData(
        Guid Id,
        string Name,
        RoomVisibility Visibility,
        string? InviteCode,
        Guid HostId,
        int Capacity,
        RoomStatus Status,
        Guid? MatchId,
        DateTimeOffset CreatedAt,
        IReadOnlyList<MemberData> Members)
    {
        public static RoomData From(Room room) => new(
            room.Id,
            room.Name,
            room.Visibility,
            room.InviteCode,
            room.HostId,
            room.Capacity,
            room.Status,
            room.MatchId,
            room.CreatedAt,
            [.. room.Members.Select(m => new MemberData(m.UserId, m.Username, m.Hero, m.Ready, m.JoinedAt))]);

        public Room ToRoom(long version) => new()
        {
            Id = Id,
            Name = Name,
            Visibility = Visibility,
            InviteCode = InviteCode,
            HostId = HostId,
            Capacity = Capacity,
            Status = Status,
            MatchId = MatchId,
            CreatedAt = CreatedAt,
            Version = version,
            Members = [.. Members.Select(m => new RoomMember(m.UserId, m.Username, m.Hero, m.Ready, m.JoinedAt))],
        };
    }

    private sealed record MemberData(Guid UserId, string Username, HeroClass? Hero, bool Ready, DateTimeOffset JoinedAt);
}
