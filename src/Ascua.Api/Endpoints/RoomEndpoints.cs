using System.Security.Claims;
using Ascua.Api.Security;
using Ascua.Application.Rooms;
using Ascua.Infrastructure.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ascua.Api.Endpoints;

/// <summary>Salas (lobby): crear, listar, unirse, elegir héroe, marcarse listo e iniciar. Ver docs/api/salas.md.</summary>
public static class RoomEndpoints
{
    public const string BasePath = "/api/v1/rooms";

    public static IEndpointRouteBuilder MapRoomEndpoints(this IEndpointRouteBuilder app)
    {
        var rooms = app.MapGroup(BasePath)
            .WithTags("Rooms")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.UserPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        rooms.MapPost("/", CreateAsync)
            .WithName("CreateRoom")
            .WithSummary("Crea una sala pública o privada (con código de invitación) y entra como anfitrión.")
            .WithValidation<CreateRoomRequest>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        rooms.MapGet("/", ListAsync)
            .WithName("ListRooms")
            .WithSummary("Salas públicas abiertas y con lugar, de la más antigua a la más nueva.")
            .ProducesValidationProblem();

        rooms.MapGet("/current", GetCurrentAsync)
            .WithName("GetCurrentRoom")
            .WithSummary("Sala en la que está el usuario (404 si no está en ninguna).")
            .ProducesProblem(StatusCodes.Status404NotFound);

        rooms.MapGet("/{roomId:guid}", GetAsync)
            .WithName("GetRoom")
            .WithSummary("Detalle de una sala. Las privadas solo las ven sus participantes; el código y la partida, solo los participantes.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        rooms.MapPost("/{roomId:guid}/join", JoinAsync)
            .WithName("JoinRoom")
            .WithSummary("Se une a una sala pública. Idempotente si ya está en ella.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        rooms.MapPost("/join", JoinByCodeAsync)
            .WithName("JoinRoomByCode")
            .WithSummary("Se une a una sala (pública o privada) con su código de invitación.")
            .WithValidation<JoinByCodeRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        rooms.MapPost("/{roomId:guid}/leave", LeaveAsync)
            .WithName("LeaveRoom")
            .WithSummary("Sale de la sala. Si sale el anfitrión lo reemplaza el más antiguo; una sala vacía se borra.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        rooms.MapPut("/{roomId:guid}/hero", ChooseHeroAsync)
            .WithName("ChooseHero")
            .WithSummary("Elige héroe (sin repetir dentro de la sala).")
            .WithValidation<ChooseHeroRequest>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        rooms.MapPut("/{roomId:guid}/ready", SetReadyAsync)
            .WithName("SetReady")
            .WithSummary("Marca o desmarca \"listo\".")
            .WithValidation<SetReadyRequest>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        rooms.MapPost("/{roomId:guid}/start", StartAsync)
            .WithName("StartRoom")
            .WithSummary("El anfitrión inicia la partida: ≥2 jugadores y todos listos. Idempotente; devuelve el id de la partida.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return app;
    }

    private static async Task<Results<Created<RoomResponse>, ProblemHttpResult>> CreateAsync(
        CreateRoomRequest request, ClaimsPrincipal principal, [FromServices] RoomService rooms, CancellationToken cancellationToken)
    {
        if (!TryGetUser(principal, out var userId, out var username))
        {
            return Problems.Unauthorized.ToResult();
        }

        var result = await rooms.CreateAsync(userId, username, request.Name.Trim(), request.Visibility, request.MaxPlayers, cancellationToken);
        return result.Error is { } error
            ? Problems.Result(error)
            : TypedResults.Created($"{BasePath}/{result.Value!.Room.Id}", RoomResponse.From(result.Value));
    }

    private static async Task<Results<Ok<RoomPageResponse>, ProblemHttpResult>> ListAsync(
        [FromServices] RoomService rooms, CancellationToken cancellationToken, int page = 1, int pageSize = 20)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["Debe ser 1 o mayor."];
        }

        if (pageSize is < 1 or > LobbyLimits.MaxPageSize)
        {
            errors["pageSize"] = [$"Debe estar entre 1 y {LobbyLimits.MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return Problems.ValidationResult(errors);
        }

        var result = await rooms.ListPublicAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(new RoomPageResponse([.. result.Items.Select(RoomSummaryResponse.From)], page, pageSize, result.Total));
    }

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult>> GetCurrentAsync(
        ClaimsPrincipal principal, [FromServices] RoomService rooms, CancellationToken cancellationToken) =>
        !TryGetUser(principal, out var userId, out _)
            ? Problems.Unauthorized.ToResult()
            : ToResponse(await rooms.GetCurrentAsync(userId, cancellationToken));

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult>> GetAsync(
        Guid roomId, ClaimsPrincipal principal, [FromServices] RoomService rooms, CancellationToken cancellationToken) =>
        !TryGetUser(principal, out var userId, out _)
            ? Problems.Unauthorized.ToResult()
            : ToResponse(await rooms.GetAsync(userId, roomId, cancellationToken));

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult>> JoinAsync(
        Guid roomId, ClaimsPrincipal principal, [FromServices] RoomService rooms, CancellationToken cancellationToken) =>
        !TryGetUser(principal, out var userId, out var username)
            ? Problems.Unauthorized.ToResult()
            : ToResponse(await rooms.JoinAsync(userId, username, roomId, cancellationToken));

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult>> JoinByCodeAsync(
        JoinByCodeRequest request, ClaimsPrincipal principal, [FromServices] RoomService rooms, CancellationToken cancellationToken) =>
        !TryGetUser(principal, out var userId, out var username)
            ? Problems.Unauthorized.ToResult()
            : ToResponse(await rooms.JoinByCodeAsync(userId, username, request.Code, cancellationToken));

    private static async Task<Results<NoContent, ProblemHttpResult>> LeaveAsync(
        Guid roomId, ClaimsPrincipal principal, [FromServices] RoomService rooms, CancellationToken cancellationToken)
    {
        if (!TryGetUser(principal, out var userId, out _))
        {
            return Problems.Unauthorized.ToResult();
        }

        var result = await rooms.LeaveAsync(userId, roomId, cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult>> ChooseHeroAsync(
        Guid roomId, ChooseHeroRequest request, ClaimsPrincipal principal, [FromServices] RoomService rooms, CancellationToken cancellationToken) =>
        !TryGetUser(principal, out var userId, out _)
            ? Problems.Unauthorized.ToResult()
            : ToResponse(await rooms.ChooseHeroAsync(userId, roomId, request.Hero!.Value, cancellationToken));

    private static async Task<Results<Ok<RoomResponse>, ProblemHttpResult>> SetReadyAsync(
        Guid roomId, SetReadyRequest request, ClaimsPrincipal principal, [FromServices] RoomService rooms, CancellationToken cancellationToken) =>
        !TryGetUser(principal, out var userId, out _)
            ? Problems.Unauthorized.ToResult()
            : ToResponse(await rooms.SetReadyAsync(userId, roomId, request.Ready!.Value, cancellationToken));

    private static async Task<Results<Ok<MatchStartedResponse>, ProblemHttpResult>> StartAsync(
        Guid roomId, ClaimsPrincipal principal, [FromServices] RoomService rooms, CancellationToken cancellationToken)
    {
        if (!TryGetUser(principal, out var userId, out _))
        {
            return Problems.Unauthorized.ToResult();
        }

        var result = await rooms.StartAsync(userId, roomId, cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.Ok(new MatchStartedResponse(result.Value));
    }

    private static Results<Ok<RoomResponse>, ProblemHttpResult> ToResponse(LobbyResult<RoomView> result) =>
        result.Error is { } error ? Problems.Result(error) : TypedResults.Ok(RoomResponse.From(result.Value!));

    internal static bool TryGetUser(ClaimsPrincipal principal, out Guid userId, out string username)
    {
        username = principal.FindFirstValue(AscuaClaims.Name) ?? string.Empty;
        return UserEndpoints.TryGetUserId(principal, out userId) && username.Length > 0;
    }
}
