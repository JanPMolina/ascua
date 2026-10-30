using Ascua.Api.Endpoints;
using Ascua.Application.Admin;
using Ascua.Application.Users;
using Ascua.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Ascua.Api.Security;

public static class Authentication
{
    public static IServiceCollection AddAscuaAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(AscuaClaims.Role, UserRoleNames.Admin));
        return services;
    }

    /// <summary>Política de <c>/api/v1/admin</c>: JWT válido de una cuenta no suspendida con <c>role=admin</c>.</summary>
    public const string AdminPolicy = "admin";

    private const string SuspendedFailure = "ascua:suspended";

    /// <summary>Validación de los JWT emitidos por <see cref="JwtAccessTokenIssuer"/>; solo se acepta ES256.</summary>
    private sealed class ConfigureJwtBearer(ISigningKeyProvider keys, IOptions<JwtOptions> jwt, IUserAccessGate access) : IConfigureNamedOptions<JwtBearerOptions>
    {
        public void Configure(string? name, JwtBearerOptions options)
        {
            if (name != JwtBearerDefaults.AuthenticationScheme)
            {
                return;
            }

            var settings = jwt.Value;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidIssuer = settings.Issuer,
                ValidAudience = settings.Audience,
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                IssuerSigningKeyResolver = (_, _, _, _) => keys.ValidationKeys,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = AscuaClaims.Name,
                RoleClaimType = AscuaClaims.Role,
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = async context =>
                {
                    // La firma no basta: un access token vigente de una cuenta suspendida deja de servir
                    // en la siguiente petición (caché corta en Redis, escrita al suspender).
                    if (!Guid.TryParse(context.Principal?.FindFirst(AscuaClaims.Subject)?.Value, out var userId)
                        || await access.IsSuspendedAsync(userId, context.HttpContext.RequestAborted))
                    {
                        context.HttpContext.Items[SuspendedFailure] = true;
                        context.Fail("La cuenta está suspendida o ya no existe.");
                    }
                },
                OnChallenge = async context =>
                {
                    // Problem Details en lugar del 401 vacío; el header WWW-Authenticate se mantiene.
                    // Una cuenta suspendida responde 403 account-suspended, igual que el login.
                    context.HandleResponse();
                    if (context.HttpContext.Items.ContainsKey(SuspendedFailure))
                    {
                        await Problems.WriteAsync(context.HttpContext, Problems.AccountSuspended);
                        return;
                    }

                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    await Problems.WriteAsync(context.HttpContext, Problems.Unauthorized);
                },
                OnForbidden = context => Problems.WriteAsync(context.HttpContext, Problems.Forbidden),
            };
        }

        public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);
    }
}
