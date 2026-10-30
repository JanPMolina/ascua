using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Ascua.Application.Matchmaking;
using Ascua.Application.Rooms;
using Ascua.Domain.Model;
using Ascua.Infrastructure.Lobby;
using Room = Ascua.Application.Rooms.Room;

namespace Ascua.Api.Endpoints;

// DTOs de salas y emparejamiento. Los enums viajan como texto en MAYÚSCULAS (igual que en el contrato AsyncAPI).

internal static class LobbyLimits
{
    public const int NameMin = 3;
    public const int NameMax = 32;
    public const string NamePattern = @"^[\p{L}\p{N} _\-]+$";
    public const string InviteCodePattern = "^[A-Za-z0-9]+$";
    public const int MaxPageSize = 50;
}

public sealed class CreateRoomRequest
{
    [Required, StringLength(LobbyLimits.NameMax, MinimumLength = LobbyLimits.NameMin)]
    [RegularExpression(LobbyLimits.NamePattern, ErrorMessage = "Solo letras, dígitos, espacios, guion y guion bajo.")]
    public string Name { get; init; } = string.Empty;

    [Description("PUBLIC aparece en el listado; PRIVATE solo se entra con el código de invitación.")]
    public RoomVisibility Visibility { get; init; } = RoomVisibility.Public;

    [Range(Room.MinPlayers, Room.MaxPlayers)]
    [Description("Capacidad de la sala (2 a 4).")]
    public int MaxPlayers { get; init; } = Room.MaxPlayers;
}

public sealed class JoinByCodeRequest
{
    [Required, StringLength(InviteCodeGenerator.Length, MinimumLength = InviteCodeGenerator.Length)]
    [RegularExpression(LobbyLimits.InviteCodePattern, ErrorMessage = "Código inválido.")]
    [Description("Código de invitación de la sala privada (no distingue mayúsculas).")]
    public string Code { get; init; } = string.Empty;
}

public sealed class ChooseHeroRequest
{
    [Required]
    [Description("Héroe; no se repite dentro de la sala. Cambiarlo quita el \"listo\".")]
    public HeroClass? Hero { get; init; }
}

public sealed class SetReadyRequest
{
    [Required]
    [Description("true para marcarse listo (exige héroe elegido), false para desmarcarse.")]
    public bool? Ready { get; init; }
}

public sealed class EnqueueRequest
{
    [Description("Héroe preferido. Si otro jugador del grupo llegó antes con el mismo, se asigna uno libre.")]
    public HeroClass? PreferredHero { get; init; }
}

public sealed record RoomMemberResponse(Guid UserId, string Username, HeroClass? Hero, bool Ready, bool IsHost);

public sealed record RoomResponse(
    Guid Id,
    string Name,
    RoomVisibility Visibility,
    RoomStatus Status,
    int Capacity,
    Guid HostId,
    IReadOnlyList<RoomMemberResponse> Members,
    [property: Description("Solo para participantes de salas privadas.")] string? InviteCode,
    [property: Description("Solo para participantes, cuando la partida ya arrancó.")] Guid? MatchId,
    DateTimeOffset CreatedAt)
{
    public static RoomResponse From(RoomView view)
    {
        var room = view.Room;
        return new RoomResponse(
            room.Id,
            room.Name,
            room.Visibility,
            room.Status,
            room.Capacity,
            room.HostId,
            [.. room.Members.Select(m => new RoomMemberResponse(m.UserId, m.Username, m.Hero, m.Ready, m.UserId == room.HostId))],
            view.IsMember ? room.InviteCode : null,
            view.IsMember ? room.MatchId : null,
            room.CreatedAt);
    }
}

public sealed record RoomSummaryResponse(Guid Id, string Name, int Capacity, int Players, string HostUsername, DateTimeOffset CreatedAt)
{
    public static RoomSummaryResponse From(Room room) => new(
        room.Id,
        room.Name,
        room.Capacity,
        room.Members.Count,
        room.Members.Find(m => m.UserId == room.HostId)?.Username ?? string.Empty,
        room.CreatedAt);
}

public sealed record RoomPageResponse(
    IReadOnlyList<RoomSummaryResponse> Items,
    int Page,
    int PageSize,
    [property: Description("Total aproximado de salas públicas abiertas.")] long Total);

public sealed record MatchStartedResponse(Guid MatchId);

public sealed record QueueStatusResponse(
    [property: Description("IDLE, QUEUED, MATCHING (grupo formado, creando partida), MATCHED o EXPIRED.")] QueueState Status,
    DateTimeOffset? EnqueuedAt,
    [property: Description("Lugar en la cola (1 = el siguiente).")] long? Position,
    long PlayersInQueue,
    HeroClass? PreferredHero,
    [property: Description("Partida asignada cuando el estado es MATCHED.")] Guid? MatchId)
{
    public static QueueStatusResponse From(QueueStatus status) =>
        new(status.State, status.EnqueuedAt, status.Position, status.PlayersInQueue, status.PreferredHero, status.MatchId);
}
