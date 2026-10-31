using Ascua.Infrastructure.Cloud;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Ascua.Infrastructure.Redis;

public static class RedisConnection
{
    public const string ConnectionStringName = "Redis";

    public const string DedicatedSocketThreadsKey = "Ascua:Redis:DedicatedSocketThreads";

    /// <summary>Azure: autenticarse con Entra (identidad administrada) en lugar de la clave de acceso.</summary>
    public const string UseEntraAuthKey = "Ascua:Redis:UseEntraAuth";

    /// <summary>
    /// Registra un <see cref="IConnectionMultiplexer"/> compartido y su check de salud (etiqueta "ready").
    /// La cadena se lee al resolver para que las pruebas puedan sobrescribirla.
    /// </summary>
    public static IServiceCollection AddAscuaRedis(this IServiceCollection services)
    {
        services.AddSingleton<IConnectionMultiplexer>(sp =>
            ConnectionMultiplexer.Connect(BuildOptionsAsync(sp.GetRequiredService<IConfiguration>()).GetAwaiter().GetResult()));

        services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);
        return services;
    }

    /// <summary>
    /// Opciones de conexión desde <c>ConnectionStrings:Redis</c>, comunes a la app y al backplane de SignalR.
    /// Con <c>Ascua:Redis:UseEntraAuth=true</c> (Azure) la cadena no lleva <c>password</c>: la librería de
    /// Microsoft pide y renueva sola el token de Entra de la identidad administrada.
    /// </summary>
    public static async Task<ConfigurationOptions> BuildOptionsAsync(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Falta ConnectionStrings:{ConnectionStringName}.");
        }

        var options = ConfigurationOptions.Parse(connectionString);
        ApplySocketManager(options, configuration);
        if (configuration.GetValue<bool>(UseEntraAuthKey))
        {
            if (!string.IsNullOrEmpty(options.Password))
            {
                throw new InvalidOperationException(
                    "Con Ascua:Redis:UseEntraAuth=true la cadena de Redis no debe llevar password: se usa la identidad administrada.");
            }

            options.Ssl = true;
            await options.ConfigureForAzureWithTokenCredentialAsync(AzureCredentials.Get(configuration));
        }

        return options;
    }

    /// <summary>
    /// E/S de Redis en el thread pool de .NET (<see cref="SocketManager.ThreadPool"/>) en lugar de los hilos
    /// dedicados que trae StackExchange.Redis por defecto. Con una cuota de CPU fraccionaria (0,5 vCPU en
    /// Container Apps) esos hilos gastaban la cuota en <c>sched_yield</c>; ver docs/rendimiento.md.
    /// <c>Ascua:Redis:DedicatedSocketThreads=true</c> vuelve al comportamiento por defecto (para comparar).
    /// </summary>
    public static void ApplySocketManager(ConfigurationOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!configuration.GetValue<bool>(DedicatedSocketThreadsKey))
        {
            options.SocketManager = SocketManager.ThreadPool;
        }
    }

    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionStringName));
}

internal sealed class RedisHealthCheck(IServiceProvider services, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!RedisConnection.IsConfigured(configuration))
        {
            return HealthCheckResult.Unhealthy("Redis no está configurado.");
        }

        try
        {
            var latency = await services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().PingAsync();
            return HealthCheckResult.Healthy($"ping {latency.TotalMilliseconds:F0} ms");
        }
        catch (RedisException ex)
        {
            return HealthCheckResult.Unhealthy("Redis no responde.", ex);
        }
    }
}
