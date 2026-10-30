using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace Ascua.Infrastructure.Cloud;

/// <summary>
/// Credencial de Azure compartida por Key Vault, Postgres y Redis.
/// <list type="bullet">
/// <item>En Azure: identidad administrada asignada por el usuario, con su client id en
/// <c>Ascua:Azure:ManagedIdentityClientId</c> (o la asignada por el sistema si se deja vacío y no hay
/// variables de entorno de otra credencial).</item>
/// <item>En una máquina de desarrollo: <see cref="DefaultAzureCredential"/> usa la sesión de <c>az login</c>,
/// útil para probar contra recursos reales sin secretos.</item>
/// </list>
/// </summary>
public static class AzureCredentials
{
    public const string ManagedIdentityClientIdKey = "Ascua:Azure:ManagedIdentityClientId";

    private static readonly Lock Gate = new();
    private static TokenCredential? _shared;

    public static TokenCredential Get(IConfiguration configuration)
    {
        lock (Gate)
        {
            return _shared ??= Create(configuration);
        }
    }

    private static TokenCredential Create(IConfiguration configuration)
    {
        var clientId = configuration[ManagedIdentityClientIdKey];
        return string.IsNullOrWhiteSpace(clientId)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }
}
