using System.Security.Claims;
using Ascua.Api.Security;
using Ascua.Application.Matchmaking;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ascua.Api.Endpoints;

/// <summary>
/// Cola de emparejamiento. El agrupador corre en el servidor (BackgroundService); el cliente puede consultar
/// el estado aquí, pero la partida y sus turnos llegan por el tiempo real. Ver docs/api/salas.md.
/// </summary>
public static class MatchmakingEndpoints
{
    public const string QueuePath = "/api/v1/matchmaking/queue";

    public static IEndpointRouteBuilder MapMatchmakingEndpoints(this IEndpointRouteBuilder app)
    {
        var queue = app.MapGroup(QueuePath)
            .WithTags("Matchmaking")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.UserPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        queue.MapPost("/", EnqueueAsync)
            .WithName("EnqueueMatchmaking")
            .WithSummary("Entra a la cola (idempotente: si ya estaba, conserva su lugar). Cuerpo opcional con el héroe preferido.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        queue.MapDelete("/", LeaveAsync)
            .WithName("LeaveMatchmaking")
            .WithSummary("Sale de la cola (idempotente). 409 si ya fue emparejado y su partida se está creando.")
            .ProducesProblem(StatusCodes.Status409Conflict);

        queue.MapGet("/", GetStatusAsync)
            .WithName("GetMatchmakingStatus")
            .WithSummary("Estado en la cola y, cuando la hay, la partida asignada.");

        return app;
    }

    private static async Task<Results<Ok<QueueStatusResponse>, ProblemHttpResult>> EnqueueAsync(
        [FromBody] EnqueueRequest? request, ClaimsPrincipal principal, [FromServices] MatchmakingService matchmaking, CancellationToken cancellationToken)
    {
        if (!RoomEndpoints.TryGetUser(principal, out var userId, out var username))
        {
            return Problems.Unauthorized.ToResult();
        }

        var result = await matchmaking.EnqueueAsync(userId, username, request?.PreferredHero, cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.Ok(QueueStatusResponse.From(result.Value!));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> LeaveAsync(
        ClaimsPrincipal principal, [FromServices] MatchmakingService matchmaking, CancellationToken cancellationToken)
    {
        if (!RoomEndpoints.TryGetUser(principal, out var userId, out _))
        {
            return Problems.Unauthorized.ToResult();
        }

        var result = await matchmaking.LeaveAsync(userId, cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.NoContent();
    }

    private static async Task<Results<Ok<QueueStatusResponse>, ProblemHttpResult>> GetStatusAsync(
        ClaimsPrincipal principal, [FromServices] MatchmakingService matchmaking, CancellationToken cancellationToken) =>
        !RoomEndpoints.TryGetUser(principal, out var userId, out _)
            ? Problems.Unauthorized.ToResult()
            : TypedResults.Ok(QueueStatusResponse.From(await matchmaking.GetStatusAsync(userId, cancellationToken)));
}
