using System.Security.Cryptography;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Ascua.Infrastructure.Auth;

/// <summary>Una versión de la clave de firma: su id (versión del secreto) y la clave privada en PEM.</summary>
public sealed record SigningKeyVersion(string Version, string Pem, DateTimeOffset? CreatedOn);

/// <summary>Origen de las versiones de la clave de firma. En Azure, un secreto de Key Vault.</summary>
public interface ISigningKeySource
{
    /// <summary>La versión vigente (con la que se firma) y las demás versiones habilitadas (solo para validar).</summary>
    Task<(SigningKeyVersion Current, IReadOnlyList<SigningKeyVersion> Others)> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Lee la clave de un secreto de Key Vault que guarda una clave privada ECDSA P-256 en PEM (PKCS#8).
/// La versión más reciente firma; las demás versiones <b>habilitadas</b> siguen validando, así una
/// rotación no corta las sesiones (ver docs/runbooks/rotar-clave-jwt.md). Para retirar una clave vieja
/// basta con deshabilitar su versión.
/// </summary>
public sealed class KeyVaultSigningKeySource(SecretClient client, string secretName) : ISigningKeySource
{
    /// <summary>Tope de versiones viejas que se cargan para validar (las más recientes).</summary>
    private const int MaxOtherVersions = 3;

    public async Task<(SigningKeyVersion Current, IReadOnlyList<SigningKeyVersion> Others)> LoadAsync(CancellationToken cancellationToken)
    {
        KeyVaultSecret current = await client.GetSecretAsync(secretName, cancellationToken: cancellationToken);

        var olderVersions = new List<SecretProperties>();
        await foreach (var properties in client.GetPropertiesOfSecretVersionsAsync(secretName, cancellationToken))
        {
            if (properties.Enabled == true && properties.Version != current.Properties.Version)
            {
                olderVersions.Add(properties);
            }
        }

        var others = new List<SigningKeyVersion>();
        foreach (var properties in olderVersions.OrderByDescending(p => p.CreatedOn).Take(MaxOtherVersions))
        {
            KeyVaultSecret old = await client.GetSecretAsync(secretName, properties.Version, cancellationToken);
            others.Add(new SigningKeyVersion(properties.Version, old.Value, properties.CreatedOn));
        }

        return (new SigningKeyVersion(current.Properties.Version, current.Value, current.Properties.CreatedOn), others);
    }
}

/// <summary>
/// <see cref="ISigningKeyProvider"/> respaldado por Key Vault. Carga las claves al crearse (si falla, la
/// app no arranca: mejor que emitir tokens que otras réplicas no validan) y las recarga cada
/// <c>Ascua:Jwt:KeyRefreshInterval</c>. Si una recarga falla, conserva las últimas claves buenas.
/// </summary>
public sealed class KeyVaultSigningKeyProvider : ISigningKeyProvider, IDisposable
{
    private readonly ISigningKeySource _source;
    private readonly ILogger<KeyVaultSigningKeyProvider> _logger;
    private readonly Timer _timer;
    private volatile KeySet _keys;

    private KeyVaultSigningKeyProvider(ISigningKeySource source, ILogger<KeyVaultSigningKeyProvider> logger, KeySet keys, TimeSpan refreshInterval)
    {
        _source = source;
        _logger = logger;
        _keys = keys;
        _timer = new Timer(_ => _ = RefreshAsync(), null, refreshInterval, refreshInterval);
    }

    public ECDsaSecurityKey SigningKey => _keys.Signing;

    public IReadOnlyCollection<SecurityKey> ValidationKeys => _keys.Validation;

    public static async Task<KeyVaultSigningKeyProvider> CreateAsync(
        ISigningKeySource source, ILogger<KeyVaultSigningKeyProvider> logger, TimeSpan refreshInterval, CancellationToken cancellationToken = default)
    {
        var keys = await LoadAsync(source, cancellationToken);
        logger.LogInformation("Clave de firma JWT cargada de Key Vault ({KeyId}; {Count} clave(s) de validación)", keys.Signing.KeyId, keys.Validation.Count);
        return new KeyVaultSigningKeyProvider(source, logger, keys, refreshInterval);
    }

    /// <summary>Recarga las claves; si algo falla, deja las anteriores. Público para las pruebas.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var keys = await LoadAsync(_source, cancellationToken);
            if (keys.Signing.KeyId != _keys.Signing.KeyId)
            {
                _logger.LogInformation("Rotación de la clave de firma JWT: ahora firma {KeyId}", keys.Signing.KeyId);
            }

            _keys = keys;
        }
#pragma warning disable CA1031 // Cualquier falla de red o de Key Vault: se conservan las claves que ya funcionan.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "No se pudo recargar la clave de firma JWT de Key Vault; se siguen usando las actuales");
        }
    }

    public void Dispose() => _timer.Dispose();

    private static async Task<KeySet> LoadAsync(ISigningKeySource source, CancellationToken cancellationToken)
    {
        var (current, others) = await source.LoadAsync(cancellationToken);
        var signing = Import(current);
        var validation = new List<SecurityKey> { signing };
        validation.AddRange(others.Select(Import));
        return new KeySet(signing, validation);
    }

    /// <summary>Convierte el PEM en una clave ES256; rechaza cualquier curva que no sea P-256.</summary>
    public static ECDsaSecurityKey Import(SigningKeyVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(version.Pem);
            var curve = ecdsa.ExportParameters(false).Curve;
            if (curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            {
                throw new InvalidOperationException(
                    $"La versión {version.Version} de la clave JWT no es ECDSA P-256 (ES256); revise el secreto de Key Vault.");
            }
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }

        return new ECDsaSecurityKey(ecdsa) { KeyId = $"kv-{version.Version}" };
    }

    private sealed record KeySet(ECDsaSecurityKey Signing, IReadOnlyCollection<SecurityKey> Validation);
}
