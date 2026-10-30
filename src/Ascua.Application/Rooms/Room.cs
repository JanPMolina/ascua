using System.Collections.Immutable;
using Ascua.Domain.Model;

namespace Ascua.Application.Rooms;

public enum RoomVisibility
{
    /// <summary>Aparece en el listado y cualquiera puede unirse por id.</summary>
    Public,

    /// <summary>No se lista; solo se entra con el código de invitación.</summary>
    Private,
}

public enum RoomStatus
{
    /// <summary>Aceptando jugadores, héroes y "listo".</summary>
    Open,

    /// <summary>El anfitrión pidió iniciar y se está creando la partida; la sala queda congelada.</summary>
    Starting,

    /// <summary>La partida existe (<see cref="Room.MatchId"/>); los jugadores ya no están en la sala.</summary>
    Started,
}

/// <summary>Un jugador sentado en la sala. El orden de <see cref="Room.Members"/> es el orden de asiento.</summary>
public sealed record RoomMember(Guid UserId, string Username, HeroClass? Hero, bool Ready, DateTimeOffset JoinedAt);

/// <summary>
/// Sala de espera (lobby). Inmutable: cada operación devuelve una sala nueva o un <see cref="LobbyError"/>.
/// Las reglas viven aquí; la atomicidad frente a operaciones concurrentes la da <see cref="IRoomStore.SaveAsync"/>
/// (compara la <see cref="Version"/> leída antes de escribir).
/// </summary>
public sealed record Room
{
    public const int MinPlayers = 2;
    public const int MaxPlayers = 4;

    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required RoomVisibility Visibility { get; init; }

    /// <summary>Solo las salas privadas tienen código.</summary>
    public string? InviteCode { get; init; }

    public required Guid HostId { get; init; }

    public required int Capacity { get; init; }

    public RoomStatus Status { get; init; } = RoomStatus.Open;

    public Guid? MatchId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Versión optimista; la asigna el almacén al guardar (0 = nunca guardada).</summary>
    public long Version { get; init; }

    public ImmutableList<RoomMember> Members { get; init; } = [];

    public bool IsFull => Members.Count >= Capacity;

    /// <summary>Se lista en <c>GET /rooms</c>: pública, abierta y con lugar.</summary>
    public bool IsListed => Visibility == RoomVisibility.Public && Status == RoomStatus.Open && !IsFull;

    public bool HasMember(Guid userId) => Members.Exists(m => m.UserId == userId);

    public static Room Create(
        Guid id, string name, RoomVisibility visibility, string? inviteCode, int capacity, Guid hostId, string hostUsername, DateTimeOffset now)
    {
        if (capacity is < MinPlayers or > MaxPlayers)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, $"La capacidad va de {MinPlayers} a {MaxPlayers}.");
        }

        if ((visibility == RoomVisibility.Private) != (inviteCode is not null))
        {
            throw new ArgumentException("Solo las salas privadas llevan código de invitación.", nameof(inviteCode));
        }

        return new Room
        {
            Id = id,
            Name = name,
            Visibility = visibility,
            InviteCode = inviteCode,
            HostId = hostId,
            Capacity = capacity,
            CreatedAt = now,
            Members = [new RoomMember(hostId, hostUsername, null, false, now)],
        };
    }

    public LobbyResult<Room> Join(Guid userId, string username, DateTimeOffset now)
    {
        if (Status != RoomStatus.Open)
        {
            return LobbyError.RoomNotOpen;
        }

        if (HasMember(userId))
        {
            return this;
        }

        if (IsFull)
        {
            return LobbyError.RoomFull;
        }

        return this with { Members = Members.Add(new RoomMember(userId, username, null, false, now)) };
    }

    /// <summary>Sale de la sala. Si sale el anfitrión, lo reemplaza el que lleva más tiempo. Una sala vacía devuelve <c>null</c>.</summary>
    public LobbyResult<Room?> Leave(Guid userId)
    {
        if (!HasMember(userId))
        {
            return LobbyError.NotRoomMember;
        }

        if (Status != RoomStatus.Open)
        {
            return LobbyError.RoomNotOpen;
        }

        var remaining = Members.RemoveAll(m => m.UserId == userId);
        if (remaining.IsEmpty)
        {
            return LobbyResult<Room?>.Ok(null);
        }

        return this with { Members = remaining, HostId = HostId == userId ? remaining[0].UserId : HostId };
    }

    /// <summary>Elige héroe. No se repiten héroes en la sala; cambiarlo quita el "listo".</summary>
    public LobbyResult<Room> ChooseHero(Guid userId, HeroClass hero)
    {
        if (CheckEditable(userId) is { } error)
        {
            return error;
        }

        if (Members.Exists(m => m.UserId != userId && m.Hero == hero))
        {
            return LobbyError.HeroTaken;
        }

        return Update(userId, m => m.Hero == hero ? m : m with { Hero = hero, Ready = false });
    }

    public LobbyResult<Room> SetReady(Guid userId, bool ready)
    {
        if (CheckEditable(userId) is { } error)
        {
            return error;
        }

        var member = Members.Find(m => m.UserId == userId)!;
        if (ready && member.Hero is null)
        {
            return LobbyError.HeroRequired;
        }

        return Update(userId, m => m with { Ready = ready });
    }

    /// <summary>
    /// El anfitrión inicia: hacen falta al menos <see cref="MinPlayers"/> jugadores y que <b>todos</b> los presentes
    /// estén listos. Repetirlo mientras la sala está <see cref="RoomStatus.Starting"/> es idempotente (reintento).
    /// </summary>
    public LobbyResult<Room> BeginStart(Guid userId)
    {
        if (!HasMember(userId))
        {
            return LobbyError.NotRoomMember;
        }

        if (HostId != userId)
        {
            return LobbyError.NotRoomHost;
        }

        if (Status == RoomStatus.Starting)
        {
            return this;
        }

        if (Status != RoomStatus.Open)
        {
            return LobbyError.RoomNotOpen;
        }

        if (Members.Count < MinPlayers || !Members.TrueForAll(m => m.Ready && m.Hero is not null))
        {
            return LobbyError.NotEnoughReady;
        }

        return this with { Status = RoomStatus.Starting };
    }

    /// <summary>Vuelve a abrir la sala si no se pudo crear la partida.</summary>
    public Room AbortStart() => Status == RoomStatus.Starting ? this with { Status = RoomStatus.Open } : this;

    public LobbyResult<Room> MarkStarted(Guid matchId) => Status switch
    {
        RoomStatus.Starting => this with { Status = RoomStatus.Started, MatchId = matchId },
        RoomStatus.Started when MatchId == matchId => this,
        _ => LobbyError.RoomNotOpen,
    };

    private LobbyError? CheckEditable(Guid userId)
    {
        if (!HasMember(userId))
        {
            return LobbyError.NotRoomMember;
        }

        return Status == RoomStatus.Open ? null : LobbyError.RoomNotOpen;
    }

    private Room Update(Guid userId, Func<RoomMember, RoomMember> change)
    {
        var index = Members.FindIndex(m => m.UserId == userId);
        return this with { Members = Members.SetItem(index, change(Members[index])) };
    }
}
