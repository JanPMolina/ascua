using System.Security.Claims;
using Ascua.Api.Security;
using Ascua.Application.Users;
using Ascua.Infrastructure.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ascua.Api.Endpoints;

public static class UserEndpoints
{
    public const string MePath = "/api/v1/users/me";

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup(MePath)
            .WithTags("Users")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.UserPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        me.MapGet("/", GetAsync)
            .WithName("GetMyProfile")
            .WithSummary("Perfil del usuario autenticado.");

        me.MapPatch("/", UpdateAsync)
            .WithName("UpdateMyProfile")
            .WithSummary("Cambia el correo y/o la contraseña (exige la actual).")
            .WithValidation<UpdateProfileRequest>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<Results<Ok<UserProfileResponse>, ProblemHttpResult>> GetAsync(
        ClaimsPrincipal principal, ProfileService profiles, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Problems.Unauthorized.ToResult();
        }

        var result = await profiles.GetAsync(userId, cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.Ok(UserProfileResponse.From(result.Value!));
    }

    private static async Task<Results<Ok<UserProfileResponse>, ProblemHttpResult>> UpdateAsync(
        UpdateProfileRequest request, ClaimsPrincipal principal, ProfileService profiles, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Problems.Unauthorized.ToResult();
        }

        var result = await profiles.UpdateAsync(userId, request.Email, request.CurrentPassword, request.NewPassword, cancellationToken);
        return result.Error is { } error ? Problems.Result(error) : TypedResults.Ok(UserProfileResponse.From(result.Value!));
    }

    internal static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue(AscuaClaims.Subject), out userId);
}
