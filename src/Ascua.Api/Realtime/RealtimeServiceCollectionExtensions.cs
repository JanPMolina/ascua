using Ascua.Application.Admin;
using Ascua.Application.Matches;
using Ascua.Infrastructure.Matches;
using Ascua.Infrastructure.Redis;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Ascua.Api.Realtime;

public static class RealtimeServiceCollectionExtensions
{
    /// <summary>Hub, backplane de Redis, motor de partidas, tickets, límites y temporizadores.</summary>
    public static IServiceCollection AddAscuaRealtime(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // Se lee al resolver (no al registrar) para que las pruebas puedan sobrescribirla.
        services.AddSingleton(sp =>
            sp.GetRequiredService<IConfiguration>().GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>() ?? new RealtimeOptions());

        services
            .AddSignalR(options =>
            {
                options.MaximumReceiveMessageSize = 4 * 1024;
                options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                options.EnableDetailedErrors = false;
                options.AddFilter<RealtimeHubFilter>();

                // JSON solo para depuración en local/dev (contrato, sección 1); en prod solo MessagePack.
                if (!environment.IsDevelopment() && !environment.IsEnvironment("dev"))
                {
                    options.SupportedProtocols = ["messagepack"];
                }
            })
            .AddMessagePackProtocol()
            .AddStackExchangeRedis();

        // Backplane: misma cadena que el resto de Redis, leída al resolver.
        services.AddOptions<RedisOptions>().Configure<IConfiguration>((options, configuration) =>
        {
            options.Configuration = ConfigurationOptions.Parse(configuration.GetConnectionString(RedisConnection.ConnectionStringName) ?? "localhost");
            options.Configuration.ChannelPrefix = RedisChannel.Literal("ascua:signalr");
        });

        services.AddSingleton<RealtimeHubFilter>();
        services.AddSingleton<MatchMetrics>();
        services.AddSingleton<RealtimeConnections>();
        services.AddSingleton<IRealtimeKicker>(sp => sp.GetRequiredService<RealtimeConnections>());
        services.AddSingleton<IMatchStore, EfMatchStore>();
        services.AddSingleton<IMatchHotState, RedisMatchHotState>();
        services.AddSingleton<IRealtimeTicketStore, RedisTicketStore>();
        services.AddSingleton<IConnectionLimiter, RedisConnectionLimiter>();
        services.AddSingleton<IMatchNotifier, SignalRMatchNotifier>();
        services.AddSingleton<MatchEngine>();

        // Reemplaza cualquier implementación provisional (TryAdd) que haya registrado salas.
        services.RemoveAll<IMatchLauncher>();
        services.AddSingleton<IMatchLauncher>(sp => sp.GetRequiredService<MatchEngine>());

        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TicketAuthentication>(TicketAuthentication.SchemeName, null);
        services.AddHostedService<RealtimeDrainService>();
        services.AddHostedService<RoundTimerService>();
        return services;
    }

    public static WebApplication MapAscuaRealtime(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapRealtimeEndpoints(app.Services.GetRequiredService<RealtimeOptions>());

        // Solo WebSockets (sin SSE ni long polling): no hacen falta sticky sessions.
        app.MapHub<MatchHub>(MatchHub.Path, options => options.Transports = HttpTransportType.WebSockets);
        return app;
    }
}
