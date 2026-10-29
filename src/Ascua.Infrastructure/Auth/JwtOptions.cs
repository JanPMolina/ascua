namespace Ascua.Infrastructure.Auth;

/// <summary>Sección <c>Ascua:Jwt</c> de la configuración.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Ascua:Jwt";

    public string Issuer { get; set; } = "ascua";

    public string Audience { get; set; } = "ascua-api";

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Permite la clave efímera fuera de Development (p. ej. un ambiente de prueba de una sola réplica
    /// mientras no esté conectado Key Vault). En producción debe quedar en <c>false</c>.
    /// </summary>
    public bool AllowEphemeralKey { get; set; }
}
