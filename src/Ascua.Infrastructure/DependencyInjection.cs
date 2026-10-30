using Ascua.Application.Admin;
using Ascua.Application.Audit;
using Ascua.Application.Matches;
using Ascua.Application.Users;
using Ascua.Infrastructure.Auth;
using Ascua.Infrastructure.Cloud;
using Ascua.Infrastructure.Persistence;
using Azure.Security.KeyVault.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ascua.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registra persistencia, hash de contraseñas, emisión de JWT y casos de uso de usuarios.</summary>
    public static IServiceCollection AddAscuaInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        // La cadena se lee al resolver (no al registrar) para que las pruebas puedan sobrescribirla.
        services.AddSingleton(sp => PostgresDataSource.Create(sp.GetRequiredService<IConfiguration>()));
        services.AddDbContext<AscuaDbContext>((sp, options) => options
            .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
            .UseSnakeCaseNamingConvention());

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddSingleton<IPasswordHasher, Argon2idPasswordHasher>();
        services.AddSingleton<IAccessTokenIssuer, JwtAccessTokenIssuer>();
        services.AddSingleton(sp => new SessionPolicy(sp.GetRequiredService<IOptions<JwtOptions>>().Value.RefreshTokenLifetime));
        services.AddSingleton(CreateSigningKeyProvider);

        services.AddScoped<AuthService>();
        services.AddScoped<ProfileService>();

        services.AddScoped<IAuditLog, EfAuditLog>();
        // Reemplaza la auditoría provisional del replay completo (el tiempo real la registra con TryAdd).
        services.AddSingleton<IReplayAccessAudit, AuditLogReplayAccessAudit>();
        services.AddScoped<IAdminQueries, EfAdminQueries>();
        services.AddSingleton<IUserAccessGate, RedisUserAccessGate>();
        services.AddScoped<AdminService>();

        return services;
    }

    /// <summary>Aplica las migraciones pendientes si <c>Ascua:Database:MigrateOnStartup</c> es true.</summary>
    public static async Task MigrateDatabaseIfConfiguredAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        if (!services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
        {
            return;
        }

        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AscuaDbContext>().Database.MigrateAsync(cancellationToken);
    }

    private static ISigningKeyProvider CreateSigningKeyProvider(IServiceProvider sp)
    {
        var environment = sp.GetRequiredService<IHostEnvironment>();
        var jwt = sp.GetRequiredService<IOptions<JwtOptions>>().Value;
        var keyVaultUri = sp.GetRequiredService<IConfiguration>()["Ascua:KeyVaultUri"];

        if (!string.IsNullOrWhiteSpace(keyVaultUri))
        {
            // Azure: la clave vive en Key Vault y se lee con la identidad administrada (sin secretos en la config).
            var client = new SecretClient(new Uri(keyVaultUri), AzureCredentials.Get(sp.GetRequiredService<IConfiguration>()));
            return KeyVaultSigningKeyProvider.CreateAsync(
                new KeyVaultSigningKeySource(client, jwt.KeyVaultSecretName),
                sp.GetRequiredService<ILogger<KeyVaultSigningKeyProvider>>(),
                jwt.KeyRefreshInterval).GetAwaiter().GetResult();
        }

        if (environment.IsDevelopment() || jwt.AllowEphemeralKey)
        {
            if (!environment.IsDevelopment())
            {
                sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DependencyInjection).FullName!)
                    .LogWarning("Usando clave JWT efímera fuera de Development (Ascua:Jwt:AllowEphemeralKey=true)");
            }

            return new EphemeralSigningKeyProvider();
        }

        throw new InvalidOperationException(
            "No hay clave de firma JWT: configure Ascua:KeyVaultUri o, solo para pruebas, Ascua:Jwt:AllowEphemeralKey=true.");
    }
}
