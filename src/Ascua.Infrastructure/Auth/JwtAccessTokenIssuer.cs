using System.Security.Claims;
using Ascua.Application.Abstractions;
using Ascua.Application.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ascua.Infrastructure.Auth;

/// <summary>Emite JWT de acceso firmados con ES256. Claims: sub, name, role, jti, iat, nbf, exp.</summary>
public sealed class JwtAccessTokenIssuer(ISigningKeyProvider keys, IOptions<JwtOptions> options, IClock clock) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(User user)
    {
        var settings = options.Value;
        var now = clock.UtcNow;
        var expires = now + settings.AccessTokenLifetime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(keys.SigningKey, SecurityAlgorithms.EcdsaSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Name, user.Username),
                new Claim(AscuaClaims.Role, user.Role.ToClaimValue()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
        };

        return new AccessToken(_handler.CreateToken(descriptor), expires);
    }
}

/// <summary>Nombres de claims usados por la API (sin el mapeo heredado a URIs de WS-Federation).</summary>
public static class AscuaClaims
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string Name = JwtRegisteredClaimNames.Name;
    public const string Role = "role";
}
