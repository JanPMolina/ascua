using System.Security.Cryptography;
using Ascua.Application.Matches;
using Ascua.Application.Matchmaking;
using Ascua.Application.Rooms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ascua.Infrastructure.Lobby;

/// <summary>Sección <c>Ascua:Lobby</c>.</summary>
public sealed class LobbyOptions
{
    public const string SectionName = "Ascua:Lobby";

    public TimeSpan RoomTtl { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan ActiveMatchTtl { get; set; } = TimeSpan.FromHours(4);
}

/// <summary>Sección <c>Ascua:Matchmaking</c>.</summary>
public sealed class MatchmakingOptions
{
    public const string SectionName = "Ascua:Matchmaking";

    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Permite apagar el agrupador en una réplica (la cola sigue aceptando jugadores).</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>Códigos de invitación de 8 caracteres (alfabeto de 32 sin I ni O, ~40 bits) con CSPRNG.</summary>
public sealed class InviteCodeGenerator : IInviteCodeGenerator
{
    public const int Length = 8;
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    public string Next() => RandomNumberGenerator.GetString(Alphabet, Length);
}

/// <summary>Procesa la cola de emparejamiento en el servidor cada <see cref="MatchmakingPolicy.PollInterval"/>.</summary>
public sealed partial class MatchmakingWorker(
    IServiceScopeFactory scopes, MatchmakingPolicy policy, IOptions<MatchmakingOptions> options, ILogger<MatchmakingWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(policy.PollInterval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MatchmakingProcessor>().RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un fallo pasajero (Redis caído) no debe matar el servicio: se reintenta en la próxima vuelta.
                LogPassFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Emparejamiento: falló una pasada del agrupador")]
    private partial void LogPassFailed(Exception exception);
}

public static class LobbyServiceCollectionExtensions
{
    /// <summary>
    /// Salas, cola de emparejamiento y su agrupador en segundo plano, todo sobre Redis (requiere <c>AddAscuaRedis</c>).
    /// Si nadie registró un <see cref="IMatchLauncher"/>, deja uno que responde "no disponible" (503).
    /// </summary>
    public static IServiceCollection AddAscuaLobby(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LobbyOptions>(configuration.GetSection(LobbyOptions.SectionName));
        services.Configure<MatchmakingOptions>(configuration.GetSection(MatchmakingOptions.SectionName));
        services.AddSingleton(sp =>
        {
            var lobby = sp.GetRequiredService<IOptions<LobbyOptions>>().Value;
            return new LobbyPolicy(lobby.RoomTtl, lobby.ActiveMatchTtl);
        });
        services.AddSingleton(sp =>
        {
            var mm = sp.GetRequiredService<IOptions<MatchmakingOptions>>().Value;
            return new MatchmakingPolicy(mm.MaxWait, mm.QueueTimeout, mm.PollInterval);
        });

        services.AddSingleton<IRoomStore, RedisRoomStore>();
        services.AddSingleton<IMatchmakingQueue, RedisMatchmakingQueue>();
        services.AddSingleton<IActiveMatchRegistry, RedisActiveMatchRegistry>();
        services.AddSingleton<IInviteCodeGenerator, InviteCodeGenerator>();
        services.TryAddSingleton<IMatchLauncher, UnavailableMatchLauncher>();

        services.AddScoped<RoomService>();
        services.AddScoped<MatchmakingService>();
        services.AddScoped<MatchmakingProcessor>();
        services.AddHostedService<MatchmakingWorker>();

        return services;
    }
}
