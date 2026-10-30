using System.Collections.Concurrent;
using Ascua.Application.Abstractions;
using Ascua.Application.Matches;
using Ascua.Application.Rooms;

namespace Ascua.Application.Tests;

/// <summary>Almacén en memoria con la misma semántica que el de Redis: versión optimista y un lobby por usuario.</summary>
internal sealed class InMemoryRoomStore : IRoomStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Room> _rooms = [];
    private readonly Dictionary<string, Guid> _codes = [];
    private readonly Dictionary<Guid, Guid> _lobby = [];

    public Dictionary<Guid, Guid> ActiveMatches { get; } = [];

    public HashSet<Guid> Queued { get; } = [];

    public int Saves { get; private set; }

    public Task<Room?> GetAsync(Guid roomId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_rooms.GetValueOrDefault(roomId));
        }
    }

    public Task<Guid?> FindIdByInviteCodeAsync(string inviteCode, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_codes.TryGetValue(inviteCode, out var id) ? id : (Guid?)null);
        }
    }

    public Task<Guid?> FindRoomIdOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_lobby.TryGetValue(userId, out var id) ? id : (Guid?)null);
        }
    }

    public Task<RoomPage> ListPublicAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var listed = _rooms.Values.Where(r => r.IsListed).OrderBy(r => r.CreatedAt).ToList();
            return Task.FromResult(new RoomPage([.. listed.Skip(offset).Take(limit)], listed.Count));
        }
    }

    public async Task<RoomSaveResult> SaveAsync(Room? expected, Room? updated, CancellationToken cancellationToken)
    {
        await Task.Yield();
        lock (_gate)
        {
            var room = updated ?? expected!;
            var stored = _rooms.GetValueOrDefault(room.Id);
            if ((stored?.Version ?? 0) != (expected?.Version ?? 0))
            {
                return new RoomSaveResult(RoomSaveOutcome.VersionConflict);
            }

            var before = expected?.Members.Select(m => m.UserId).ToHashSet() ?? [];
            var after = updated?.Members.Select(m => m.UserId).ToList() ?? [];
            foreach (var added in after.Where(id => !before.Contains(id)))
            {
                if (ActiveMatches.ContainsKey(added))
                {
                    return new RoomSaveResult(RoomSaveOutcome.UserBusy, BusyReason: LobbyError.AlreadyInMatch);
                }

                if (Queued.Contains(added))
                {
                    return new RoomSaveResult(RoomSaveOutcome.UserBusy, BusyReason: LobbyError.AlreadyInQueue);
                }

                if (_lobby.TryGetValue(added, out var other) && other != room.Id)
                {
                    return new RoomSaveResult(RoomSaveOutcome.UserBusy, BusyReason: LobbyError.AlreadyInRoom);
                }
            }

            if (expected is null && room.InviteCode is { } code && !_codes.TryAdd(code, room.Id))
            {
                return new RoomSaveResult(RoomSaveOutcome.InviteCodeTaken);
            }

            Saves++;
            foreach (var removed in before.Where(id => !after.Contains(id)))
            {
                _lobby.Remove(removed);
            }

            if (updated is null)
            {
                _rooms.Remove(room.Id);
                return RoomSaveResult.Saved(null);
            }

            var saved = updated with { Version = (stored?.Version ?? 0) + 1 };
            _rooms[room.Id] = saved;
            foreach (var id in after)
            {
                if (saved is { Status: RoomStatus.Started, MatchId: { } matchId })
                {
                    ActiveMatches.TryAdd(id, matchId);
                    _lobby.Remove(id);
                }
                else
                {
                    _lobby[id] = room.Id;
                }
            }

            return RoomSaveResult.Saved(saved);
        }
    }
}

/// <summary>Lanzador falso, idempotente por sala, que cuenta las llamadas.</summary>
internal sealed class FakeMatchLauncher : IMatchLauncher
{
    private readonly ConcurrentDictionary<Guid, Guid> _matches = new();

    public ConcurrentQueue<LaunchMatchRequest> Requests { get; } = new();

    public Exception? Failure { get; set; }

    public Task<Guid> LaunchAsync(LaunchMatchRequest request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Failure is { } failure
            ? Task.FromException<Guid>(failure)
            : Task.FromResult(_matches.GetOrAdd(request.RoomId, _ => Guid.NewGuid()));
    }
}

internal sealed class SequentialCodes : IInviteCodeGenerator
{
    private readonly Queue<string> _codes;

    public SequentialCodes(params string[] codes) => _codes = new Queue<string>(codes);

    public string Next() => _codes.Count > 1 ? _codes.Dequeue() : _codes.Peek();
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
}
