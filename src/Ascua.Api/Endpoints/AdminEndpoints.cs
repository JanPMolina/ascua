using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Ascua.Api.Security;
using Ascua.Application.Admin;
using Ascua.Application.Audit;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ascua.Api.Endpoints;

/// <summary>
/// Administración (<c>/api/v1/admin</c>): solo rol <c>admin</c> (401 sin token, 403 a cualquier otro rol) y
/// cada acción queda en <c>audit_log</c>. Ver docs/api/administracion.md.
/// </summary>
public static class AdminEndpoints
{
    public const string BasePath = "/api/v1/admin";
    public const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup(BasePath)
            .WithTags("Admin")
            .RequireAuthorization(Authentication.AdminPolicy)
            .RequireRateLimiting(RateLimiting.UserPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        admin.MapGet("/matches", ListMatchesAsync)
            .WithName("AdminListMatches")
            .WithSummary("Partidas activas (DRAFTING o IN_PROGRESS) con ronda, jugadores, conectados y última actividad.")
            .ProducesValidationProblem();

        admin.MapGet("/connections", GetConnectionsAsync)
            .WithName("AdminGetConnections")
            .WithSummary("Conexiones de tiempo real vivas, agregadas por partida y usuario (sin ids de conexión ni tickets).");

        admin.MapPost("/matches/{matchId:guid}/terminate", TerminateAsync)
            .WithName("AdminTerminateMatch")
            .WithSummary("Detiene la partida (estado TERMINATED), avisa a los jugadores (KICKED_BY_ADMIN) y libera su partida activa.")
            .WithValidation<AdminActionRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapPost("/users/{userId:guid}/suspend", SuspendAsync)
            .WithName("AdminSuspendUser")
            .WithSummary("Suspende la cuenta: revoca sus sesiones, invalida su access token, corta sus conexiones y lo saca de salas, cola y partida.")
            .WithValidation<AdminActionRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        admin.MapPost("/users/{userId:guid}/unsuspend", UnsuspendAsync)
            .WithName("AdminUnsuspendUser")
            .WithSummary("Rehabilita la cuenta (tendrá que volver a iniciar sesión).")
            .WithValidation<AdminActionRequest>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        admin.MapGet("/audit", ListAuditAsync)
            .WithName("AdminListAudit")
            .WithSummary("Registro de auditoría, lo más reciente primero.")
            .ProducesValidationProblem();

        return app;
    }

    private static async Task<Results<Ok<AdminMatchPageResponse>, ProblemHttpResult>> ListMatchesAsync(
        [FromServices] AdminService admin, CancellationToken cancellationToken, string status = "active", int page = 1, int pageSize = 20)
    {
        var errors = PagingErrors(page, pageSize);
        if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
        {
            errors["status"] = ["Solo se admite status=active."];
        }

        if (errors.Count > 0)
        {
            return Problems.ValidationResult(errors);
        }

        var result = await admin.ListActiveMatchesAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(new AdminMatchPageResponse([.. result.Items.Select(AdminMatchResponse.From)], page, pageSize, result.Total));
    }

    private static async Task<Ok<ConnectionsOverview>> GetConnectionsAsync(
        [FromServices] AdminService admin, CancellationToken cancellationToken) =>
        TypedResults.Ok(await admin.GetConnectionsAsync(cancellationToken));

    private static async Task<Results<Ok<TerminationResult>, ProblemHttpResult>> TerminateAsync(
        Guid matchId, AdminActionRequest request, HttpContext http, [FromServices] AdminService admin, CancellationToken cancellationToken)
    {
        var result = await admin.TerminateMatchAsync(Actor(http), matchId, request.Reason.Trim(), cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.Ok(result.Value!);
    }

    private static async Task<Results<Ok<SuspensionResult>, ProblemHttpResult>> SuspendAsync(
        Guid userId, AdminActionRequest request, HttpContext http, [FromServices] AdminService admin, CancellationToken cancellationToken)
    {
        var result = await admin.SuspendAsync(Actor(http), userId, request.Reason.Trim(), cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.Ok(result.Value!);
    }

    private static async Task<Results<Ok<SuspensionResult>, ProblemHttpResult>> UnsuspendAsync(
        Guid userId, AdminActionRequest request, HttpContext http, [FromServices] AdminService admin, CancellationToken cancellationToken)
    {
        var result = await admin.UnsuspendAsync(Actor(http), userId, request.Reason.Trim(), cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.Ok(result.Value!);
    }

    private static async Task<Results<Ok<AuditPageResponse>, ProblemHttpResult>> ListAuditAsync(
        [FromServices] AdminService admin, CancellationToken cancellationToken, int page = 1, int pageSize = 50)
    {
        var errors = PagingErrors(page, pageSize);
        if (errors.Count > 0)
        {
            return Problems.ValidationResult(errors);
        }

        var result = await admin.ListAuditAsync(page, pageSize, cancellationToken);
        return TypedResults.Ok(new AuditPageResponse([.. result.Items.Select(AuditEntryResponse.From)], page, pageSize, result.Total));
    }

    private static Dictionary<string, string[]> PagingErrors(int page, int pageSize)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["Debe ser 1 o mayor."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"Debe estar entre 1 y {MaxPageSize}."];
        }

        return errors;
    }

    /// <summary>Actor de la auditoría: el admin del JWT, su IP (ya resuelta por ForwardedHeaders) y el id de traza.</summary>
    private static AuditActor Actor(HttpContext http) => new(
        UserEndpoints.TryGetUserId(http.User, out var userId) ? userId : null,
        http.Connection.RemoteIpAddress?.ToString(),
        Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier);
}

public sealed class AdminActionRequest
{
    [Required, StringLength(AuditEntry.ReasonMaxLength, MinimumLength = 3)]
    [Description("Motivo de la acción; queda en la auditoría. No incluya datos sensibles.")]
    public string Reason { get; init; } = string.Empty;
}

public sealed record AdminMatchPlayerResponse(Guid UserId, string DisplayName, string Hero, int Seat, bool Connected);

public sealed record AdminMatchResponse(
    Guid MatchId,
    [property: Description("DRAFTING o IN_PROGRESS.")] string Status,
    int Round,
    IReadOnlyList<AdminMatchPlayerResponse> Players,
    int ConnectedPlayers,
    DateTimeOffset CreatedAt,
    [property: Description("Última confirmación de la partida en Postgres.")] DateTimeOffset LastActivityAt)
{
    public static AdminMatchResponse From(AdminMatch match)
    {
        var players = match.Match.Players
            .Select(p => new AdminMatchPlayerResponse(p.UserId, p.DisplayName, p.Hero, p.Seat + 1, match.Connected.Contains(p.UserId)))
            .ToList();
        return new AdminMatchResponse(
            match.Match.MatchId, match.Match.Status, match.Match.Round, players, players.Count(p => p.Connected),
            match.Match.CreatedAt, match.Match.UpdatedAt);
    }
}

public sealed record AdminMatchPageResponse(IReadOnlyList<AdminMatchResponse> Items, int Page, int PageSize, long Total);

public sealed record AuditEntryResponse(
    Guid Id,
    [property: Description("null si la acción la hizo el sistema (p. ej. el bootstrap del primer admin).")] Guid? ActorId,
    string Action,
    string TargetType,
    string TargetId,
    string? Reason,
    JsonElement Metadata,
    string? Ip,
    string? CorrelationId,
    DateTimeOffset CreatedAt)
{
    public static AuditEntryResponse From(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        using var metadata = JsonDocument.Parse(entry.Metadata);
        return new AuditEntryResponse(
            entry.Id, entry.ActorId, entry.Action, entry.TargetType, entry.TargetId, entry.Reason,
            metadata.RootElement.Clone(), entry.Ip, entry.CorrelationId, entry.CreatedAt);
    }
}

public sealed record AuditPageResponse(IReadOnlyList<AuditEntryResponse> Items, int Page, int PageSize, long Total);
