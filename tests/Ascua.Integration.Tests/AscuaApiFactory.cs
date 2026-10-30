using System.Collections.Concurrent;
using Ascua.Application.Matches;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Ascua.Integration.Tests;

/// <summary>
/// API real contra un Postgres y un Redis efímeros (Testcontainers). Se comparte entre todas las clases de la
/// colección "api"; cada prueba usa nombres de usuario únicos para no chocar con las demás.
/// El <see cref="IMatchLauncher"/> es el real envuelto por <see cref="Launcher"/>, que anota los pedidos.
/// </summary>
public sealed class AscuaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:7-alpine").Build();

    public CapturingLoggerProvider Logs { get; } = new();

    public MatchLaunchRecorder Launcher { get; } = new();

    /// <summary>Contenedores propios de esta instancia (las pruebas de resiliencia los pausan).</summary>
    public PostgreSqlContainer Postgres => _postgres;

    public RedisContainer Redis => _redis;

    public async Task InitializeAsync() => await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _redis.DisposeAsync();
    }

    /// <summary>Otra instancia de la API sobre la misma base, con configuración adicional.</summary>
    public WebApplicationFactory<Program> WithSettings(IDictionary<string, string?> settings) =>
        WithWebHostBuilder(builder => builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings)));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Como setting del host (no ConfigureAppConfiguration) para que Program.cs ya lo vea al decidir si registra Redis.
        builder.UseSetting("ConnectionStrings:Redis", _redis.GetConnectionString());
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _postgres.GetConnectionString(),
            ["ConnectionStrings:Redis"] = _redis.GetConnectionString(),
            ["Ascua:Realtime:EnableDevEndpoints"] = "true",
            ["Ascua:Matchmaking:MaxWait"] = "00:00:01",
            ["Ascua:Matchmaking:PollInterval"] = "00:00:00.200",
            ["Ascua:Database:MigrateOnStartup"] = "true",
            ["Ascua:RateLimiting:Auth:PermitLimit"] = "100000",
            ["Ascua:RateLimiting:User:PermitLimit"] = "100000",
            // Se captura todo (incluido el SQL de EF Core) para comprobar que no se filtran contraseñas.
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace",
        }));
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        // Las salas y la cola lanzan partidas reales (MatchEngine); el decorador solo anota cada pedido.
        builder.ConfigureTestServices(services =>
            services.AddSingleton(sp => Launcher.Wrap(sp.GetRequiredService<MatchEngine>())));
    }
}

/// <summary>Anota cada <see cref="LaunchMatchRequest"/> que llega al lanzador real, en todas las instancias de la API.</summary>
public sealed class MatchLaunchRecorder
{
    public ConcurrentQueue<LaunchMatchRequest> Requests { get; } = new();

    public IMatchLauncher Wrap(IMatchLauncher inner) => new Recording(this, inner);

    private sealed class Recording(MatchLaunchRecorder recorder, IMatchLauncher inner) : IMatchLauncher
    {
        public Task<Guid> LaunchAsync(LaunchMatchRequest request, CancellationToken cancellationToken)
        {
            recorder.Requests.Enqueue(request);
            return inner.LaunchAsync(request, cancellationToken);
        }
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
