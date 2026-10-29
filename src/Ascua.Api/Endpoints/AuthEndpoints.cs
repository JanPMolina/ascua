using Ascua.Api.Security;
using Ascua.Application.Abstractions;
using Ascua.Application.Users;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ascua.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth")
            .WithTags("Auth")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimiting.AuthPolicy)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/register", RegisterAsync)
            .WithName("Register")
            .WithSummary("Crea una cuenta de jugador.")
            .WithValidation<RegisterRequest>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/login", LoginAsync)
            .WithName("Login")
            .WithSummary("Entrega un JWT de acceso (10 min) y un refresh token rotativo.")
            .WithValidation<LoginRequest>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/refresh", RefreshAsync)
            .WithName("Refresh")
            .WithSummary("Rota el refresh token. Reutilizar uno ya rotado revoca toda la familia de sesiones.")
            .WithValidation<RefreshRequest>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/logout", LogoutAsync)
            .WithName("Logout")
            .WithSummary("Revoca la familia del refresh token. Idempotente.")
            .WithValidation<RefreshRequest>();

        return app;
    }

    private static async Task<Results<Created<UserProfileResponse>, ProblemHttpResult>> RegisterAsync(
        RegisterRequest request, AuthService auth, CancellationToken cancellationToken)
    {
        var result = await auth.RegisterAsync(request.Username, request.Email, request.Password, cancellationToken);
        return result.Error is { } error
            ? Problems.Result(error)
            : TypedResults.Created(UserEndpoints.MePath, UserProfileResponse.From(result.Value!));
    }

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request, AuthService auth, IClock clock, CancellationToken cancellationToken)
    {
        var result = await auth.LoginAsync(request.Username, request.Password, cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.Ok(TokenResponse.From(result.Value!, clock.UtcNow));
    }

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> RefreshAsync(
        RefreshRequest request, AuthService auth, IClock clock, CancellationToken cancellationToken)
    {
        var result = await auth.RefreshAsync(request.RefreshToken, cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.Ok(TokenResponse.From(result.Value!, clock.UtcNow));
    }

    private static async Task<NoContent> LogoutAsync(RefreshRequest request, AuthService auth, CancellationToken cancellationToken)
    {
        await auth.LogoutAsync(request.RefreshToken, cancellationToken);
        return TypedResults.NoContent();
    }
}
