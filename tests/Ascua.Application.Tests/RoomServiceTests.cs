using Ascua.Application.Matches;
using Ascua.Application.Rooms;
using Ascua.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Room = Ascua.Application.Rooms.Room;

namespace Ascua.Application.Tests;

public sealed class RoomServiceTests
{
    private readonly InMemoryRoomStore _store = new();
    private readonly FakeMatchLauncher _launcher = new();
    private readonly RoomService _rooms;

    public RoomServiceTests() =>
        _rooms = new RoomService(_store, new SequentialCodes("AAAA2222", "BBBB3333"), _launcher, new FixedClock(), NullLogger<RoomService>.Instance);

    [Fact]
    public async Task Private_room_is_hidden_from_strangers_but_reachable_by_code()
    {
        var host = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var room = (await _rooms.CreateAsync(host, "host", "secreta", RoomVisibility.Private, 4, default)).Value!.Room;

        Assert.Equal("AAAA2222", room.InviteCode);
        Assert.Equal(LobbyError.RoomNotFound, (await _rooms.GetAsync(stranger, room.Id, default)).Error);
        Assert.Equal(LobbyError.RoomNotFound, (await _rooms.JoinAsync(stranger, "x", room.Id, default)).Error);
        Assert.Equal(LobbyError.RoomNotFound, (await _rooms.SetReadyAsync(stranger, room.Id, true, default)).Error);
        Assert.Empty((await _rooms.ListPublicAsync(1, 20, default)).Items);

        var joined = await _rooms.JoinByCodeAsync(stranger, "x", "aaaa2222", default);
        Assert.True(joined.Value!.IsMember);
    }

    [Fact]
    public async Task Invite_code_collision_retries_with_a_new_code()
    {
        await _rooms.CreateAsync(Guid.NewGuid(), "a", "una", RoomVisibility.Private, 4, default);
        var codes = new SequentialCodes("AAAA2222", "CCCC4444");
        var rooms = new RoomService(_store, codes, _launcher, new FixedClock(), NullLogger<RoomService>.Instance);

        var second = await rooms.CreateAsync(Guid.NewGuid(), "b", "otra", RoomVisibility.Private, 4, default);

        Assert.Equal("CCCC4444", second.Value!.Room.InviteCode);
    }

    [Fact]
    public async Task A_user_can_only_be_in_one_room()
    {
        var user = Guid.NewGuid();
        await _rooms.CreateAsync(user, "u", "primera", RoomVisibility.Public, 4, default);
        var other = (await _rooms.CreateAsync(Guid.NewGuid(), "o", "segunda", RoomVisibility.Public, 4, default)).Value!.Room;

        Assert.Equal(LobbyError.AlreadyInRoom, (await _rooms.CreateAsync(user, "u", "tercera", RoomVisibility.Public, 4, default)).Error);
        Assert.Equal(LobbyError.AlreadyInRoom, (await _rooms.JoinAsync(user, "u", other.Id, default)).Error);
    }

    [Fact]
    public async Task Concurrent_joins_never_exceed_capacity()
    {
        var room = (await _rooms.CreateAsync(Guid.NewGuid(), "host", "llena", RoomVisibility.Public, 4, default)).Value!.Room;

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => _rooms.JoinAsync(Guid.NewGuid(), $"u{i}", room.Id, default))));

        Assert.Equal(3, results.Count(r => r.Error is null));
        Assert.All(results.Where(r => r.Error is not null), r => Assert.Equal(LobbyError.RoomFull, r.Error));
        Assert.Equal(4, (await _store.GetAsync(room.Id, default))!.Members.Count);
    }

    [Fact]
    public async Task Start_launches_once_with_seats_in_join_order_and_marks_active_matches()
    {
        var (room, host, guest) = await ReadyRoomAsync();

        var first = await _rooms.StartAsync(host, room.Id, default);
        var second = await _rooms.StartAsync(guest, room.Id, default);

        Assert.Equal(first.Value, second.Value);
        var request = Assert.Single(_launcher.Requests);
        Assert.Equal(room.Id, request.RoomId);
        Assert.Equal([new MatchSeat(host, HeroClass.Yunque), new MatchSeat(guest, HeroClass.Chispa)], request.Seats);
        Assert.Equal(first.Value, _store.ActiveMatches[host]);
        Assert.Equal(first.Value, _store.ActiveMatches[guest]);
        Assert.Null(await _store.FindRoomIdOfUserAsync(host, default));
        Assert.Equal(LobbyError.AlreadyInMatch, (await _rooms.CreateAsync(host, "h", "otra", RoomVisibility.Public, 4, default)).Error);
    }

    [Fact]
    public async Task Unavailable_launcher_reopens_the_room()
    {
        var (room, host, _) = await ReadyRoomAsync();
        _launcher.Failure = new MatchLaunchUnavailableException();

        var result = await _rooms.StartAsync(host, room.Id, default);

        Assert.Equal(LobbyError.MatchesUnavailable, result.Error);
        Assert.Equal(RoomStatus.Open, (await _store.GetAsync(room.Id, default))!.Status);
        Assert.Empty(_store.ActiveMatches);
    }

    [Fact]
    public async Task Unexpected_launch_failure_reopens_the_room_and_propagates()
    {
        var (room, host, _) = await ReadyRoomAsync();
        _launcher.Failure = new InvalidOperationException("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _rooms.StartAsync(host, room.Id, default));

        Assert.Equal(RoomStatus.Open, (await _store.GetAsync(room.Id, default))!.Status);
    }

    [Fact]
    public async Task Queued_user_cannot_join_a_room()
    {
        var room = (await _rooms.CreateAsync(Guid.NewGuid(), "host", "sala", RoomVisibility.Public, 4, default)).Value!.Room;
        var queued = Guid.NewGuid();
        _store.Queued.Add(queued);

        Assert.Equal(LobbyError.AlreadyInQueue, (await _rooms.JoinAsync(queued, "q", room.Id, default)).Error);
    }

    private async Task<(Room Room, Guid Host, Guid Guest)> ReadyRoomAsync()
    {
        var host = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var room = (await _rooms.CreateAsync(host, "host", "lista", RoomVisibility.Public, 4, default)).Value!.Room;
        await _rooms.JoinAsync(guest, "guest", room.Id, default);
        await _rooms.ChooseHeroAsync(host, room.Id, HeroClass.Yunque, default);
        await _rooms.ChooseHeroAsync(guest, room.Id, HeroClass.Chispa, default);
        await _rooms.SetReadyAsync(host, room.Id, true, default);
        await _rooms.SetReadyAsync(guest, room.Id, true, default);
        return (room, host, guest);
    }
}
