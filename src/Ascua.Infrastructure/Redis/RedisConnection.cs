using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Ascua.Infrastructure.Redis;

public static class RedisConnection
{
    public const string ConnectionStringName = "Redis";

    public const string DedicatedSocketThreadsKey = "Ascua:Redis:DedicatedSocketThreads";

    /// <summary>
    /// Registra un <see cref="IConnectionMultiplexer"/> compartido y su check de salud (etiqueta "ready").
    /// La cadena se lee al resolver para que las pruebas puedan sobrescribirla.
    /// TODO(Juan Pablo): en Azure, autenticación Entra con identidad administrada en lugar de contraseña.
    /// </summary>
    public static IServiceCollection AddAscuaRedis(this IServiceCollection services)
    {
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"Falta ConnectionStrings:{ConnectionStringName}.");
            }

            var options = ConfigurationOptions.Parse(connectionString);
            ApplySocketManager(options, sp.GetRequiredService<IConfiguration>());
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);
        return services;
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
