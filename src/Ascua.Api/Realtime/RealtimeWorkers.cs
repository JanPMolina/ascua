using Ascua.Application.Abstractions;
using Ascua.Application.Matches;
using Microsoft.AspNetCore.SignalR;

namespace Ascua.Api.Realtime;

/// <summary>
/// Temporizadores durables: corre en CADA réplica y lee de Redis los plazos y gracias vencidos
/// (ZRANGEBYSCORE ... -inf now). Que varias réplicas intenten lo mismo es seguro: el lock por partida
/// y la versión en Postgres dejan pasar a una sola. Cada partida se procesa aislada de las demás.
/// </summary>
public sealed class RoundTimerService(
    MatchEngine engine,
    IMatchHotState hot,
    IMatchStore store,
    IConnectionLimiter limiter,
    RealtimeConnections connections,
    MatchMetrics metrics,
    IClock clock,
    RealtimeOptions options,
    ILogger<RoundTimerService> logger) : BackgroundService
{
    private const int BatchSize = 64;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextCleanup = DateTimeOffset.MinValue;
        using var timer = new PeriodicTimer(options.TimerInterval);
        do
        {
            try
            {
                var now = clock.UtcNow;
                var deadlines = await hot.DueDeadlinesAsync(now, BatchSize, stoppingToken);
                await Task.WhenAll(deadlines.Select(id => Isolated(id, "plazo", () => engine.ProcessDeadlineAsync(id, stoppingToken))));

                var graces = await hot.ClaimDueGracesAsync(now, BatchSize, stoppingToken);
                await Task.WhenAll(graces.Select(g => Isolated(g.MatchId, "gracia", () => engine.ExpireGraceAsync(g.MatchId, g.UserId, stoppingToken))));

                if (now >= nextCleanup)
                {
                    nextCleanup = now + options.CleanupInterval;
                    await CleanupAsync(now, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Falló un ciclo del temporizador de rondas; se reintenta en el siguiente");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Limpieza: las partidas sin cambios durante InactivityTimeout pasan a EXPIRED.
        foreach (var matchId in await store.FindInactiveAsync(now - options.InactivityTimeout, BatchSize, cancellationToken))
        {
            await Isolated(matchId, "expiración", () => engine.ExpireAsync(matchId, cancellationToken));
        }

        // Plazos que Redis perdió (reinicio sin persistencia): se reconstruyen desde Postgres.
        foreach (var matchId in await store.FindOverdueAsync(now - (options.CleanupInterval * 2), BatchSize, cancellationToken))
        {
            await Isolated(matchId, "recuperación", () => engine.RecoverAsync(matchId, cancellationToken));
        }

        await limiter.RenewAsync(connections.Leases, cancellationToken);
        metrics.SetActiveMatches(await hot.ScheduledMatchCountAsync(cancellationToken));
    }

    private async Task Isolated(Guid matchId, string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falló el procesamiento de {What} de la partida {MatchId}", what, matchId);
        }
    }
}

/// <summary>
/// Al recibir SIGTERM (despliegue o reducción de escala), avisa <c>ServerDraining</c> a las conexiones
/// de esta réplica para que se reconecten a otra, y espera un momento antes de dejar cerrar el servidor.
/// También mantiene la suscripción al canal de control entre réplicas.
/// </summary>
public sealed class RealtimeDrainService(
    IHubContext<MatchHub> hub,
    RealtimeConnections connections,
    IClock clock,
    RealtimeOptions options,
    ILogger<RealtimeDrainService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => connections.SubscribeAsync();

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var local = connections.LocalConnectionIds;
        if (local.Count > 0)
        {
            logger.LogInformation("Drenando {Count} conexiones antes de apagar la réplica", local.Count);
            var message = new ServerDrainingMessage
            {
                Reason = "SHUTDOWN",
                ReconnectAfterMs = Random.Shared.Next(200, 1500),
                ClosesAt = RealtimeMapper.Timestamp(clock.UtcNow + options.DrainDelay),
            };
            try
            {
                await hub.Clients.Clients(local).SendAsync("ServerDraining", message, cancellationToken);
                await Task.Delay(options.DrainDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // El host dejó de esperar: se cierra igual.
            }
        }

        await connections.UnsubscribeAsync();
    }
}
