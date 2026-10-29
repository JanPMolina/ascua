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
}

public static class PostgresDataSource
{
    /// <summary>
    /// Construye el <see cref="NpgsqlDataSource"/> a partir de <c>ConnectionStrings:Postgres</c>.
    /// <para>
    /// Punto de extensión para Azure (autenticación Entra, sin contraseña): cuando la cadena no trae
    /// <c>Password</c>, registrar aquí un proveedor periódico de contraseñas que entregue un token de
    /// <c>DefaultAzureCredential</c> (paquete Azure.Identity) para el scope
    /// <c>https://ossrdbms-aad.database.windows.net/.default</c>:
    /// <code>
    /// builder.UsePeriodicPasswordProvider(async (_, ct) =>
    ///     (await credential.GetTokenAsync(new TokenRequestContext([scope]), ct)).Token,
    ///     TimeSpan.FromMinutes(55), TimeSpan.FromSeconds(5));
    /// </code>
    /// El usuario de la cadena es el nombre de la identidad administrada y se exige <c>SSL Mode=Require</c>.
    /// </para>
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
