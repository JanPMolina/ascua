using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;

namespace Ascua.Integration.Tests;

/// <summary>
/// API real contra un Postgres efímero (Testcontainers). Se comparte entre todas las clases de la
/// colección "api"; cada prueba usa nombres de usuario únicos para no chocar con las demás.
/// </summary>
public sealed class AscuaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public CapturingLoggerProvider Logs { get; } = new();

    public async Task InitializeAsync() => await _postgres.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>Otra instancia de la API sobre la misma base, con configuración adicional.</summary>
    public WebApplicationFactory<Program> WithSettings(IDictionary<string, string?> settings) =>
        WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings)));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
            ["Ascua:Database:MigrateOnStartup"] = "true",
            ["Ascua:RateLimiting:Auth:PermitLimit"] = "100000",
            ["Ascua:RateLimiting:User:PermitLimit"] = "100000",
            // Se captura todo (incluido el SQL de EF Core) para comprobar que no se filtran contraseñas.
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
        }));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<AscuaApiFactory>
{
    public const string Name = "api";
}

/// <summary>Guarda en memoria todos los mensajes de log (con sus excepciones) emitidos por la API.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries;

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }
}
