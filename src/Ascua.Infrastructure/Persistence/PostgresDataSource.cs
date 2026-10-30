using Ascua.Infrastructure.Cloud;
using Azure.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ascua.Infrastructure.Persistence;

/// <summary>Sección <c>Ascua:Database</c>.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Ascua:Database";

    /// <summary>Aplica las migraciones pendientes al arrancar (compose y pruebas; en Azure lo decide el despliegue).</summary>
    public bool MigrateOnStartup { get; set; }

    /// <summary>
    /// Azure: autenticarse con un token de Entra de la identidad administrada en lugar de contraseña.
    /// La cadena lleva <c>Username</c> = nombre del rol de Postgres de esa identidad y no lleva <c>Password</c>.
    /// </summary>
    public bool UseEntraAuth { get; set; }
}

public static class PostgresDataSource
{
    /// <summary>Scope de los tokens de Entra para Azure Database for PostgreSQL.</summary>
    public const string EntraScope = "https://ossrdbms-aad.database.windows.net/.default";

    /// <summary>
    /// Construye el <see cref="NpgsqlDataSource"/> a partir de <c>ConnectionStrings:Postgres</c>.
    /// Con <c>Ascua:Database:UseEntraAuth=true</c> (Azure) la contraseña es un token de Entra de la identidad
    /// administrada, renovado cada 55 min (duran ~60-90), y se exige TLS.
    /// </summary>
    public static NpgsqlDataSource Create(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Falta ConnectionStrings:Postgres (variable de entorno ConnectionStrings__Postgres).");
        }

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.ApplicationName ??= "ascua-api";

        if (configuration.GetValue<bool>($"{DatabaseOptions.SectionName}:{nameof(DatabaseOptions.UseEntraAuth)}"))
        {
            if (!string.IsNullOrEmpty(builder.ConnectionStringBuilder.Password))
            {
                throw new InvalidOperationException(
                    "Con Ascua:Database:UseEntraAuth=true la cadena de Postgres no debe llevar Password: se usa la identidad administrada.");
            }

            if (builder.ConnectionStringBuilder.SslMode is SslMode.Disable or SslMode.Allow or SslMode.Prefer)
            {
                builder.ConnectionStringBuilder.SslMode = SslMode.Require;
            }

            var credential = AzureCredentials.Get(configuration);
            builder.UsePeriodicPasswordProvider(
                async (_, ct) => (await credential.GetTokenAsync(new TokenRequestContext([EntraScope]), ct)).Token,
                TimeSpan.FromMinutes(55),
                TimeSpan.FromSeconds(5));
        }

        return builder.Build();
    }
}

/// <summary>Usado solo por <c>dotnet ef</c> para generar migraciones; no abre conexiones.</summary>
public sealed class AscuaDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AscuaDbContext>
{
    public AscuaDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AscuaDbContext>()
            .UseNpgsql("Host=localhost;Database=ascua")
            .UseSnakeCaseNamingConvention()
            .Options);
}
