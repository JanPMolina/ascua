using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Ascua.Api.Endpoints;
using Ascua.Api.Security;
using Ascua.Application.Admin;
using Ascua.Application.Matches;
using Ascua.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ascua.Api.Realtime;

/// <summary>
/// Esquema de autenticación del hub (ADR-0006): el ticket llega en el query <c>access_token</c> del
/// upgrade y se consume con <c>GETDEL</c>. Si falta, venció o ya se usó, el upgrade se rechaza con
/// 401 <c>ticket-invalid</c> y el socket no se abre. El JWT nunca viaja en la URL.
/// </summary>
public sealed partial class TicketAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IRealtimeTicketStore tickets,
    IUserAccessGate access) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "RealtimeTicket";
    public const string MatchClaim = "ascua:match";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var ticket = Request.Query["access_token"].ToString();
        if (!TicketPattern().IsMatch(ticket))
        {
            return AuthenticateResult.Fail("Ticket ausente o con formato inválido.");
        }

        var owner = await tickets.ConsumeAsync(ticket, Context.RequestAborted);
        if (owner is not { } bound)
        {
            return AuthenticateResult.Fail("Ticket vencido o ya usado.");
        }

        // Un ticket pedido justo antes de una suspensión no debe abrir el socket.
        if (await access.IsSuspendedAsync(bound.UserId, Context.RequestAborted))
        {
            return AuthenticateResult.Fail("La cuenta está suspendida.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, bound.UserId.ToString("D")),
                new Claim(AscuaClaims.Subject, bound.UserId.ToString("D")),
                new Claim(MatchClaim, bound.MatchId.ToString("D")),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Problems.WriteAsync(Context, Problems.TicketInvalid);

    [GeneratedRegex("^[A-Za-z0-9_-]{43,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TicketPattern();
}

/// <summary>Reglas del upgrade WebSocket que no son autenticación: versión de protocolo y conexiones por usuario.</summary>
public static class RealtimeHandshake
{
    public const string SupportedProtocolVersion = "3";

    /// <summary>Orígenes WS en lista blanca (los mismos de CORS); un cliente que no es navegador no envía Origin.</summary>
    public static WebSocketOptions WebSocketOptions(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = new WebSocketOptions();
        foreach (var origin in Cors.AllowedOrigins(configuration))
        {
            options.AllowedOrigins.Add(origin);
        }

        return options;
    }

    /// <summary>Antes de autenticar (para no gastar el ticket): <c>protocolVersion=3</c> o 400.</summary>
    public static IApplicationBuilder UseRealtimeProtocolVersion(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(MatchHub.Path)
                && context.Request.Query["protocolVersion"].ToString() != SupportedProtocolVersion)
            {
                await Problems.WriteAsync(context, Problems.UnsupportedProtocolVersion);
                return;
            }

            await next(context);
        });

    /// <summary>
    /// Después de autorizar: máximo de conexiones simultáneas por usuario (contador en Redis) o 429.
    /// Con WebSockets la petición dura lo que la conexión, así que el arriendo se libera al terminar.
    /// </summary>
    public static IApplicationBuilder UseRealtimeConnectionLimit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(MatchHub.Path)
                || !context.WebSockets.IsWebSocketRequest
                || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            {
                await next(context);
                return;
            }

            var services = context.RequestServices;
            var limiter = services.GetRequiredService<IConnectionLimiter>();
            var connections = services.GetRequiredService<RealtimeConnections>();
            var max = services.GetRequiredService<RealtimeOptions>().MaxConnectionsPerUser;
            var leaseId = Guid.NewGuid().ToString("N");
            if (!await limiter.TryAcquireAsync(userId, leaseId, max, context.RequestAborted))
            {
                await Problems.WriteAsync(context, Problems.ConnectionLimit);
                return;
            }

            connections.TrackLease(userId, leaseId);
            try
            {
                await next(context);
            }
            finally
            {
                connections.ForgetLease(leaseId);
                await limiter.ReleaseAsync(userId, leaseId, CancellationToken.None);
            }
        });
}
