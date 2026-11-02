using System.Security.Claims;
using Ascua.Api.Realtime;
using Ascua.Api.Security;
using Ascua.Application.Abstractions;
using Ascua.Application.Matches;
using Ascua.Application.Users;
using Ascua.Domain.Model;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ascua.Api.Endpoints;

/// <summary>
/// Historial, detalle y reproducción de partidas. Una partida inexistente y una ajena responden igual
/// (404 <c>not-found</c>) para no revelar qué partidas existen; <c>?as=full</c> sin rol admin es 403.
/// </summary>
public static class MatchHistoryEndpoints
{
    public const string BasePath = "/api/v1/matches";

    public static IEndpointRouteBuilder MapMatchHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var matches = app.MapGroup(BasePath)
            .WithTags("Matches")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.UserPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        matches.MapGet("/history", ListAsync)
            .WithName("GetMatchHistory")
            .WithSummary("Partidas del usuario autenticado, de la más reciente a la más antigua (paginado).")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        matches.MapGet("/{matchId:guid}", GetAsync)
            .WithName("GetMatch")
            .WithSummary("Detalle y resultado final de una partida en la que participaste.")
            .ProducesProblem(StatusCodes.Status404NotFound);

        matches.MapGet("/{matchId:guid}/replay", ReplayAsync)
            .WithName("GetMatchReplay")
            .WithSummary("Reproducción ronda a ronda con tu vista de jugador (admin: ?as=full para la vista completa, auditada).")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<Results<Ok<MatchHistoryResponse>, ProblemHttpResult>> ListAsync(
        ClaimsPrincipal principal,
        [FromServices] MatchHistoryService history,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = MatchHistoryService.DefaultPageSize)
    {
        if (!UserEndpoints.TryGetUserId(principal, out var userId))
        {
            return Problems.Unauthorized.ToResult();
        }

        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["Debe ser 1 o mayor."];
        }

        if (pageSize is < 1 or > MatchHistoryService.MaxPageSize)
        {
            errors["pageSize"] = [$"Debe estar entre 1 y {MatchHistoryService.MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return Problems.ValidationResult(errors);
        }

        var result = await history.ListAsync(userId, page, pageSize, cancellationToken);
        return TypedResults.Ok(new MatchHistoryResponse([.. result.Items.Select(Item)], result.Page, result.PageSize, result.Total));
    }

    private static async Task<Results<Ok<MatchDetailResponse>, ProblemHttpResult>> GetAsync(
        Guid matchId,
        ClaimsPrincipal principal,
        [FromServices] MatchHistoryService history,
        [FromServices] RealtimeOptions options,
        [FromServices] IClock clock,
        CancellationToken cancellationToken)
    {
        if (!UserEndpoints.TryGetUserId(principal, out var userId))
        {
            return Problems.Unauthorized.ToResult();
        }

        if (await history.GetForParticipantAsync(matchId, userId, cancellationToken) is not { } found)
        {
            return Problems.NotFound.ToResult();
        }

        var (match, participant) = found;

        // Mismas posiciones que MatchFinished: el pacto ajeno solo se revela con resultado del juego.
        var summary = RealtimeMapper.Finished(match, options.PublicBaseUrl, clock.UtcNow);
        var me = match.State.FindHero(participant.PlayerId)!;
        var myScore = match.State.Outcome?.Scores.FirstOrDefault(s => s.Player == participant.PlayerId);
        var finishedAt = await history.FinishedAtAsync(match.MatchId, cancellationToken);
        return TypedResults.Ok(new MatchDetailResponse(
            match.MatchId,
            match.Status.ToCode(),
            OutcomeCode(match.Status, match.State.Outcome),
            match.CreatedAt,
            finishedAt,
            match.State.Round,
            summary.Winners,
            summary.Standings,
            new RevealedPactDto
            {
                PactId = RealtimeMapper.PactId(me.Pact),
                Description = RealtimeMapper.PactDescription(me.Pact),
                Fulfilled = myScore?.PactFulfilled ?? PactRules.IsFulfilled(me.Pact, me.Progress),
            },
            summary.ReplayUrl));
    }

    private static async Task<Results<Ok<MatchReplayResponse>, ProblemHttpResult>> ReplayAsync(
        Guid matchId,
        ClaimsPrincipal principal,
        [FromServices] MatchHistoryService history,
        [FromServices] IClock clock,
        CancellationToken cancellationToken,
        int fromRound = 0,
        int? toRound = null,
        [FromQuery(Name = "as")] string? viewMode = null)
    {
        if (!UserEndpoints.TryGetUserId(principal, out var userId))
        {
            return Problems.Unauthorized.ToResult();
        }

        var errors = new Dictionary<string, string[]>();
        if (fromRound is < 0 or > MatchHistoryService.MaxRound)
        {
            errors["fromRound"] = [$"Debe estar entre 0 y {MatchHistoryService.MaxRound}."];
        }

        if (toRound is { } to && (to < fromRound || to > MatchHistoryService.MaxRound))
        {
            errors["toRound"] = [$"Debe estar entre fromRound y {MatchHistoryService.MaxRound}."];
        }

        if (viewMode is not null and not "full")
        {
            errors["as"] = ["El único valor admitido es full."];
        }

        if (errors.Count > 0)
        {
            return Problems.ValidationResult(errors);
        }

        // Se decide antes de buscar la partida: un 403 no dice nada sobre si existe.
        var full = viewMode == "full";
        if (full && !principal.IsInRole(UserRoleNames.Admin))
        {
            return Problems.Forbidden.ToResult();
        }

        var replay = await history.GetReplayAsync(matchId, userId, full, fromRound, toRound, cancellationToken);
        return replay is null
            ? Problems.NotFound.ToResult()
            : TypedResults.Ok(MatchReplayMapper.Map(replay, clock.UtcNow));
    }

    private static MatchHistoryItem Item(MatchHistoryEntry entry)
    {
        var player = entry.Participant.PlayerId;
        var outcome = entry.Outcome;
        var score = outcome?.Scores.FirstOrDefault(s => s.Player == player);
        int? placement = score is null ? null : 1 + outcome!.Scores.Count(s => s.Total > score.Total);
        string? result = outcome?.Kind switch
        {
            null => null,
            OutcomeKind.Victory when outcome.Winners.Contains(player) => "VICTORY",
            OutcomeKind.Draw when outcome.Winners.Contains(player) => "DRAW",
            OutcomeKind.ThresholdClaimed when outcome.Winners.Count > 1 && outcome.Winners.Contains(player) => "DRAW",
            OutcomeKind.ThresholdClaimed when outcome.Winners.Contains(player) => "VICTORY",
            OutcomeKind.Victory or OutcomeKind.Draw or OutcomeKind.ThresholdClaimed => "DEFEAT",
            OutcomeKind.CollectiveDefeat => "COLLECTIVE_DEFEAT",
            _ => "ABANDONED",
        };

        return new MatchHistoryItem(
            entry.MatchId,
            entry.CreatedAt,
            entry.FinishedAt,
            entry.Status.ToCode(),
            OutcomeCode(entry.Status, outcome),
            result,
            RealtimeMapper.Hero(entry.Participant.Hero),
            placement,
            score?.Total,
            entry.Rounds,
            entry.PlayerCount);
    }

    private static string? OutcomeCode(MatchStatus status, GameOutcome? outcome) => status switch
    {
        MatchStatus.Drafting or MatchStatus.InProgress => null,
        MatchStatus.Faulted => "FAULTED",
        MatchStatus.Expired => "EXPIRED",
        _ => RealtimeMapper.Outcome(outcome?.Kind),
    };
}
