using Ascua.Application.Rooms;
using Ascua.Application.Users;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ascua.Api.Endpoints;

/// <summary>
/// Tipos de Problem Details (RFC 9457) estables de la API. El <c>type</c> es una URN
/// <c>urn:ascua:problem:&lt;código&gt;</c> y el código también va en la extensión <c>code</c>.
/// Catálogo documentado en docs/api/autenticacion.md y docs/api/salas.md: si se agrega uno, se documenta en el mismo PR.
/// </summary>
public static class Problems
{
    public const string ContentType = "application/problem+json";

    public static readonly ProblemKind Validation = new("validation", StatusCodes.Status400BadRequest, "La solicitud tiene datos inválidos.");
    public static readonly ProblemKind InvalidCurrentPassword = new("invalid-current-password", StatusCodes.Status400BadRequest, "La contraseña actual no es correcta.");
    public static readonly ProblemKind Unauthorized = new("unauthorized", StatusCodes.Status401Unauthorized, "Se requiere un token de acceso válido.");
    public static readonly ProblemKind InvalidCredentials = new("invalid-credentials", StatusCodes.Status401Unauthorized, "Usuario o contraseña incorrectos.");
    public static readonly ProblemKind InvalidRefreshToken = new("invalid-refresh-token", StatusCodes.Status401Unauthorized, "El refresh token no es válido o expiró.");
    public static readonly ProblemKind RefreshTokenReused = new("refresh-token-reused", StatusCodes.Status401Unauthorized, "El refresh token ya fue usado; se cerraron todas las sesiones de esa familia.");
    public static readonly ProblemKind Forbidden = new("forbidden", StatusCodes.Status403Forbidden, "No tiene permiso para esta operación.");
    public static readonly ProblemKind AccountSuspended = new("account-suspended", StatusCodes.Status403Forbidden, "La cuenta está suspendida.");
    public static readonly ProblemKind UsernameTaken = new("username-taken", StatusCodes.Status409Conflict, "El nombre de usuario ya está en uso.");
    public static readonly ProblemKind EmailTaken = new("email-taken", StatusCodes.Status409Conflict, "El correo ya está en uso.");
    public static readonly ProblemKind NotFound = new("not-found", StatusCodes.Status404NotFound, "El recurso no existe.");
    public static readonly ProblemKind RoomNotFound = new("room-not-found", StatusCodes.Status404NotFound, "La sala no existe o no tiene acceso a ella.");
    public static readonly ProblemKind NotRoomMember = new("not-room-member", StatusCodes.Status403Forbidden, "No está en esta sala.");
    public static readonly ProblemKind NotRoomHost = new("not-room-host", StatusCodes.Status403Forbidden, "Solo el anfitrión puede iniciar la partida.");
    public static readonly ProblemKind RoomFull = new("room-full", StatusCodes.Status409Conflict, "La sala está llena.");
    public static readonly ProblemKind AlreadyInRoom = new("already-in-room", StatusCodes.Status409Conflict, "Ya está en una sala.");
    public static readonly ProblemKind AlreadyInQueue = new("already-in-queue", StatusCodes.Status409Conflict, "Ya está en la cola de emparejamiento.");
    public static readonly ProblemKind AlreadyInMatch = new("already-in-match", StatusCodes.Status409Conflict, "Ya tiene una partida activa.");
    public static readonly ProblemKind RoomNotOpen = new("room-not-open", StatusCodes.Status409Conflict, "La sala ya no acepta cambios: la partida está iniciando o iniciada.");
    public static readonly ProblemKind HeroTaken = new("hero-taken", StatusCodes.Status409Conflict, "Otro jugador de la sala ya eligió ese héroe.");
    public static readonly ProblemKind HeroRequired = new("hero-required", StatusCodes.Status409Conflict, "Elija un héroe antes de marcarse listo.");
    public static readonly ProblemKind NotEnoughReady = new("not-enough-ready", StatusCodes.Status409Conflict, "Hacen falta al menos 2 jugadores y que todos estén listos.");
    public static readonly ProblemKind RoomBusy = new("room-busy", StatusCodes.Status409Conflict, "La sala cambió demasiadas veces seguidas; reintente.");
    public static readonly ProblemKind MatchStarting = new("match-starting", StatusCodes.Status409Conflict, "Ya fue emparejado y su partida se está creando.");
    public static readonly ProblemKind MatchesUnavailable = new("matches-unavailable", StatusCodes.Status503ServiceUnavailable, "Las partidas aún no están disponibles.");
    public static readonly ProblemKind RateLimited = new("rate-limited", StatusCodes.Status429TooManyRequests, "Demasiadas solicitudes; reintente más tarde.");
    public static readonly ProblemKind TicketInvalid = new("ticket-invalid", StatusCodes.Status401Unauthorized, "El ticket de tiempo real falta, venció o ya se usó.");
    public static readonly ProblemKind UnsupportedProtocolVersion = new("unsupported-protocol-version", StatusCodes.Status400BadRequest, "La versión del protocolo de tiempo real no es compatible.");
    public static readonly ProblemKind ConnectionLimit = new("connection-limit", StatusCodes.Status429TooManyRequests, "Ya tiene el máximo de conexiones de tiempo real abiertas.");
    public static readonly ProblemKind MatchNotActive = new("match-not-active", StatusCodes.Status409Conflict, "La partida ya no está en curso.");
    public static readonly ProblemKind Internal = new("internal", StatusCodes.Status500InternalServerError, "Error interno del servidor.");

    /// <summary>
    /// Completa los problemas que genera el propio framework (JSON mal formado, 404, excepción no
    /// controlada…) con un tipo estable según el código HTTP, para que todo error tenga <c>type</c> y <c>code</c>.
    /// </summary>
    public static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        if (problem.Type?.StartsWith("urn:ascua:problem:", StringComparison.Ordinal) == true)
        {
            return;
        }

        var kind = problem.Status switch
        {
            StatusCodes.Status400BadRequest => Validation,
            StatusCodes.Status401Unauthorized => Unauthorized,
            StatusCodes.Status403Forbidden => Forbidden,
            StatusCodes.Status404NotFound => NotFound,
            StatusCodes.Status429TooManyRequests => RateLimited,
            >= 500 => Internal,
            _ => null,
        };

        if (kind is not null)
        {
            problem.Type = kind.Type;
            problem.Title = kind.Title;
            problem.Extensions["code"] = kind.Code;
        }
    }

    public static ProblemKind For(UserError error) => error switch
    {
        UserError.InvalidCredentials => InvalidCredentials,
        UserError.InvalidRefreshToken => InvalidRefreshToken,
        UserError.RefreshTokenReused => RefreshTokenReused,
        UserError.AccountSuspended => AccountSuspended,
        UserError.UsernameTaken => UsernameTaken,
        UserError.EmailTaken => EmailTaken,
        UserError.InvalidCurrentPassword => InvalidCurrentPassword,
        // El token era válido pero el usuario ya no existe: se trata como credencial inválida.
        UserError.UserNotFound => Unauthorized,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };

    public static ProblemHttpResult Result(UserError error) => For(error).ToResult();

    public static ProblemKind For(LobbyError error) => error switch
    {
        LobbyError.RoomNotFound => RoomNotFound,
        LobbyError.RoomFull => RoomFull,
        LobbyError.AlreadyInRoom => AlreadyInRoom,
        LobbyError.AlreadyInQueue => AlreadyInQueue,
        LobbyError.AlreadyInMatch => AlreadyInMatch,
        LobbyError.NotRoomMember => NotRoomMember,
        LobbyError.NotRoomHost => NotRoomHost,
        LobbyError.RoomNotOpen => RoomNotOpen,
        LobbyError.HeroTaken => HeroTaken,
        LobbyError.HeroRequired => HeroRequired,
        LobbyError.NotEnoughReady => NotEnoughReady,
        LobbyError.RoomBusy => RoomBusy,
        LobbyError.MatchStarting => MatchStarting,
        LobbyError.MatchesUnavailable => MatchesUnavailable,
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };

    public static ProblemHttpResult Result(LobbyError error) => For(error).ToResult();

    public static ProblemHttpResult ValidationResult(IDictionary<string, string[]> errors) =>
        TypedResults.Problem(new HttpValidationProblemDetails(errors)
        {
            Type = Validation.Type,
            Title = Validation.Title,
            Status = Validation.Status,
            Extensions = { ["code"] = Validation.Code },
        });

    /// <summary>Escribe el problema directamente en la respuesta (fuera de un endpoint: auth, rate limiter).</summary>
    public static async Task WriteAsync(HttpContext context, ProblemKind kind)
    {
        context.Response.StatusCode = kind.Status;
        var service = context.RequestServices.GetRequiredService<IProblemDetailsService>();
        await service.WriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = kind.ToProblemDetails() });
    }
}

public sealed record ProblemKind(string Code, int Status, string Title)
{
    public string Type => $"urn:ascua:problem:{Code}";

    public ProblemDetails ToProblemDetails() => new()
    {
        Type = Type,
        Title = Title,
        Status = Status,
        Extensions = { ["code"] = Code },
    };

    public ProblemHttpResult ToResult() => TypedResults.Problem(ToProblemDetails());
}
