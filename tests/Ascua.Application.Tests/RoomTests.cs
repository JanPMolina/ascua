using Ascua.Application.Rooms;
using Ascua.Domain.Model;
using Room = Ascua.Application.Rooms.Room;

namespace Ascua.Application.Tests;

/// <summary>Reglas de la sala (agregado puro, sin almacén).</summary>
public sealed class RoomTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Host = Guid.NewGuid();

    [Fact]
    public void Join_respects_capacity_and_is_idempotent()
    {
        var room = NewRoom(capacity: 2);
        var guest = Guid.NewGuid();

        room = room.Join(guest, "guest", Now).Value!;

        Assert.Equal(2, room.Members.Count);
        Assert.False(room.IsListed);
        Assert.Same(room, room.Join(guest, "guest", Now).Value);
        Assert.Equal(LobbyError.RoomFull, room.Join(Guid.NewGuid(), "late", Now).Error);
    }

    [Fact]
    public void Heroes_are_not_repeated_and_changing_hero_clears_ready()
    {
        var guest = Guid.NewGuid();
        var room = NewRoom().Join(guest, "guest", Now).Value!
            .ChooseHero(Host, HeroClass.Errata).Value!
            .SetReady(Host, true).Value!;

        Assert.Equal(LobbyError.HeroTaken, room.ChooseHero(guest, HeroClass.Errata).Error);

        room = room.ChooseHero(Host, HeroClass.Hilvan).Value!;
        Assert.False(room.Members[0].Ready);
        Assert.Equal(HeroClass.Hilvan, room.Members[0].Hero);
    }

    [Fact]
    public void Ready_requires_a_hero()
    {
        Assert.Equal(LobbyError.HeroRequired, NewRoom().SetReady(Host, true).Error);
    }

    [Fact]
    public void Host_leaving_promotes_the_oldest_member_and_last_one_out_deletes_the_room()
    {
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var room = NewRoom().Join(second, "b", Now).Value!.Join(third, "c", Now.AddSeconds(1)).Value!;

        room = room.Leave(Host).Value!;
        Assert.Equal(second, room.HostId);
        Assert.Equal([second, third], room.Members.Select(m => m.UserId));

        room = room.Leave(second).Value!.Leave(third).Value!;
        Assert.Null(room);
    }

    [Fact]
    public void Only_the_host_starts_and_everyone_present_must_be_ready()
    {
        var guest = Guid.NewGuid();
        var room = NewRoom().ChooseHero(Host, HeroClass.Errata).Value!.SetReady(Host, true).Value!;
        Assert.Equal(LobbyError.NotEnoughReady, room.BeginStart(Host).Error);

        room = room.Join(guest, "guest", Now).Value!.ChooseHero(guest, HeroClass.Polizon).Value!;
        Assert.Equal(LobbyError.NotEnoughReady, room.BeginStart(Host).Error);

        room = room.SetReady(guest, true).Value!;
        Assert.Equal(LobbyError.NotRoomHost, room.BeginStart(guest).Error);

        var starting = room.BeginStart(Host).Value!;
        Assert.Equal(RoomStatus.Starting, starting.Status);
        Assert.Same(starting, starting.BeginStart(Host).Value);
        Assert.Equal(LobbyError.RoomNotOpen, starting.Join(Guid.NewGuid(), "x", Now).Error);
        Assert.Equal(LobbyError.RoomNotOpen, starting.Leave(guest).Error);
        Assert.Equal(RoomStatus.Open, starting.AbortStart().Status);

        var matchId = Guid.NewGuid();
        var started = starting.MarkStarted(matchId).Value!;
        Assert.Equal((RoomStatus.Started, matchId), (started.Status, started.MatchId!.Value));
    }

    [Fact]
    public void Non_members_cannot_change_the_room()
    {
        var stranger = Guid.NewGuid();
        var room = NewRoom();

        Assert.Equal(LobbyError.NotRoomMember, room.ChooseHero(stranger, HeroClass.Campanero).Error);
        Assert.Equal(LobbyError.NotRoomMember, room.SetReady(stranger, false).Error);
        Assert.Equal(LobbyError.NotRoomMember, room.Leave(stranger).Error);
        Assert.Equal(LobbyError.NotRoomMember, room.BeginStart(stranger).Error);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Capacity_must_be_between_two_and_four(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewRoom(capacity));
    }

    private static Room NewRoom(int capacity = 4) =>
        Room.Create(Guid.NewGuid(), "sala", RoomVisibility.Public, null, capacity, Host, "host", Now);
}
