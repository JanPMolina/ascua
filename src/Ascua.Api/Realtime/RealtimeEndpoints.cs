using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using Ascua.Api.Endpoints;
using Ascua.Api.Security;
using Ascua.Application.Abstractions;
using Ascua.Application.Matches;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ascua.Api.Realtime;

public sealed class RealtimeTicketRequest
{
    [Required]
    [Description("Partida a la que se conectará el WebSocket; el ticket solo sirve para ella.")]
    public Guid MatchId { get; init; }
}

public sealed record RealtimeTicketResponse(
    [property: Description("Ticket opaco de un solo uso (43 caracteres base64url) para el query access_token del upgrade.")] string Ticket,
    DateTimeOffset ExpiresAt);

public sealed class DevMatchRequest
{
    [Required, MinLength(2), MaxLength(4)]
    [Description("Jugadores (2-4) con su héroe: ERRATA, ANTEAYER, HILVAN, POLIZON, ESCUCHA, USURERA, AVARO, MAMPOSTERO o CAMPANERO. Debe incluir al que llama.")]
    public IReadOnlyList<DevSeat> Players { get; init; } = [];
}

public sealed class DevSeat
{
    [Required]
    public Guid UserId { get; init; }

    [Required, StringLength(8)]
    public string Hero { get; init; } = string.Empty;
}

public sealed record DevMatchResponse(Guid MatchId);

public static class RealtimeEndpoints
{
    public const string TicketsPath = "/api/v1/realtime/tickets";

    public static IEndpointRouteBuilder MapRealtimeEndpoints(this IEndpointRouteBuilder app, RealtimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        app.MapPost(TicketsPath, IssueTicketAsync)
            .WithTags("Realtime")
            .WithName("IssueRealtimeTicket")
            .WithSummary("Emite un ticket de un solo uso (30 s) para abrir el WebSocket de una partida.")
            .WithValidation<RealtimeTicketRequest>()
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.UserPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        if (options.EnableDevEndpoints)
        {
            app.MapPost("/api/v1/dev/matches", LaunchDevMatchAsync)
                .WithTags("Dev")
                .WithName("LaunchDevMatch")
                .WithSummary("Solo local/dev: arranca una partida sin pasar por salas (para el bot y pruebas manuales).")
                .WithValidation<DevMatchRequest>()
                .RequireAuthorization()
                .RequireRateLimiting(RateLimiting.UserPolicy);
        }

        return app;
    }

    private static async Task<Results<Created<RealtimeTicketResponse>, ProblemHttpResult>> IssueTicketAsync(
        RealtimeTicketRequest request,
        ClaimsPrincipal principal,
        MatchEngine engine,
        IRealtimeTicketStore tickets,
        RealtimeOptions options,
        IClock clock,
        CancellationToken cancellationToken)
    {
        if (!UserEndpoints.TryGetUserId(principal, out var userId))
        {
            return Problems.Unauthorized.ToResult();
        }

        // Partida inexistente o ajena responden igual (404): no se revela qué partidas existen.
        var found = await engine.GetForPlayerAsync(request.MatchId, userId, cancellationToken);
        if (found.Error is not null)
        {
            return Problems.NotFound.ToResult();
        }

        if (!found.Value.Snapshot.Status.IsActive())
        {
            return Problems.MatchNotActive.ToResult();
        }

        var ticket = Base64Url(RandomNumberGenerator.GetBytes(32));
        await tickets.IssueAsync(ticket, userId, request.MatchId, options.TicketLifetime, cancellationToken);
        return TypedResults.Created((string?)null, new RealtimeTicketResponse(ticket, clock.UtcNow + options.TicketLifetime));
    }

    private static async Task<Results<Created<DevMatchResponse>, ProblemHttpResult>> LaunchDevMatchAsync(
        DevMatchRequest request, ClaimsPrincipal principal, IMatchLauncher launcher, CancellationToken cancellationToken)
    {
        if (!UserEndpoints.TryGetUserId(principal, out var userId))
        {
            return Problems.Unauthorized.ToResult();
        }

        var seats = new List<MatchSeat>();
        foreach (var player in request.Players)
        {
            if (RealtimeMapper.ParseHero(player.Hero) is not { } hero)
            {
                return Problems.ValidationResult(new Dictionary<string, string[]> { ["players.hero"] = ["Héroe desconocido."] });
            }

            seats.Add(new MatchSeat(player.UserId, hero));
        }

        if (seats.All(s => s.UserId != userId) || seats.Select(s => s.UserId).Distinct().Count() != seats.Count)
        {
            return Problems.ValidationResult(new Dictionary<string, string[]> { ["players"] = ["Debe incluirte y no repetir jugadores."] });
        }

        var matchId = await launcher.LaunchAsync(new LaunchMatchRequest(Guid.NewGuid(), seats), cancellationToken);
        return TypedResults.Created((string?)null, new DevMatchResponse(matchId));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
