using Ascua.Api.Endpoints;
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
        services.AddAuthorization();
        return services;
    }

    /// <summary>Validación de los JWT emitidos por <see cref="JwtAccessTokenIssuer"/>; solo se acepta ES256.</summary>
    private sealed class ConfigureJwtBearer(ISigningKeyProvider keys, IOptions<JwtOptions> jwt) : IConfigureNamedOptions<JwtBearerOptions>
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
                OnChallenge = async context =>
                {
                    // Problem Details en lugar del 401 vacío; el header WWW-Authenticate se mantiene.
                    context.HandleResponse();
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    await Problems.WriteAsync(context.HttpContext, Problems.Unauthorized);
                },
                OnForbidden = context => Problems.WriteAsync(context.HttpContext, Problems.Forbidden),
            };
        }

        public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);
    }
}
