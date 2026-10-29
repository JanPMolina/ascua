using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Ascua.Infrastructure.Auth;

/// <summary>
/// Origen de la clave ECDSA P-256 con la que se firman (ES256) y validan los JWT de acceso.
/// <para>
/// Punto de extensión para Azure: implementar un <c>KeyVaultSigningKeyProvider</c> que lea la clave de
/// Key Vault con identidad administrada (<c>DefaultAzureCredential</c>) y registrarlo en
/// <see cref="DependencyInjection.AddAscuaInfrastructure"/> cuando <c>Ascua:KeyVaultUri</c> tenga valor.
/// Para rotar claves sin cortar sesiones, <see cref="ValidationKeys"/> debe incluir la clave anterior
/// durante al menos la vida de un token de acceso (10 min).
/// </para>
/// </summary>
public interface ISigningKeyProvider
{
    /// <summary>Clave privada con la que se firman los tokens nuevos; su <c>KeyId</c> viaja en el header <c>kid</c>.</summary>
    ECDsaSecurityKey SigningKey { get; }

    /// <summary>Claves públicas aceptadas al validar (la actual y, si hay rotación, las anteriores).</summary>
    IReadOnlyCollection<SecurityKey> ValidationKeys { get; }
}

/// <summary>
/// Clave efímera generada en memoria al arrancar. Solo para desarrollo y pruebas: al reiniciar
/// el proceso se invalidan todos los tokens, y con varias réplicas cada una tendría una clave distinta.
/// </summary>
public sealed class EphemeralSigningKeyProvider : ISigningKeyProvider, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public EphemeralSigningKeyProvider()
    {
        SigningKey = new ECDsaSecurityKey(_key) { KeyId = $"ephemeral-{Guid.NewGuid():N}" };
        ValidationKeys = [SigningKey];
    }

    public ECDsaSecurityKey SigningKey { get; }

    public IReadOnlyCollection<SecurityKey> ValidationKeys { get; }

    public void Dispose() => _key.Dispose();
}
