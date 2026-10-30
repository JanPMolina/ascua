using System.Diagnostics.Metrics;

namespace Ascua.Application.Matches;

/// <summary>Métricas propias del tiempo real (planteamiento/02-arquitectura.md, sección 8).</summary>
public sealed class MatchMetrics
{
    public const string MeterName = "Ascua.Realtime";

    private readonly Counter<long> _orders;
    private readonly UpDownCounter<long> _connections;
    private readonly Histogram<double> _resolveDuration;
    private readonly Histogram<double> _broadcastLatency;
    private readonly Counter<long> _faults;
    private long _activeMatches;

    public MatchMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        meter.CreateObservableGauge(
            "ascua.matches.active",
            () => Interlocked.Read(ref _activeMatches),
            unit: "{match}",
            description: "Partidas vivas con plazo programado (todas las réplicas).");
        _connections = meter.CreateUpDownCounter<long>(
            "ascua.connections.active", unit: "{connection}", description: "Conexiones WebSocket abiertas en esta réplica.");
        _orders = meter.CreateCounter<long>(
            "ascua.orders.rate", unit: "{order}", description: "Órdenes aceptadas (la tasa se obtiene derivando el contador).");
        _resolveDuration = meter.CreateHistogram<double>(
            "ascua.round.resolve.duration", unit: "ms", description: "Cierre de ronda: sellar, resolver y confirmar en Postgres.");
        _broadcastLatency = meter.CreateHistogram<double>(
            "ascua.broadcast.latency", unit: "ms", description: "Desde que se decide cerrar la ronda hasta enviar la vista al último jugador.");
        _faults = meter.CreateCounter<long>("ascua.matches.faulted", unit: "{match}", description: "Partidas aisladas como FAULTED.");
    }

    public void SetActiveMatches(long count) => Interlocked.Exchange(ref _activeMatches, count);

    public void ConnectionOpened() => _connections.Add(1);

    public void ConnectionClosed() => _connections.Add(-1);

    public void OrderAccepted() => _orders.Add(1);

    public void RoundResolved(TimeSpan duration) => _resolveDuration.Record(duration.TotalMilliseconds);

    public void Broadcast(TimeSpan latency) => _broadcastLatency.Record(latency.TotalMilliseconds);

    public void MatchFaulted() => _faults.Add(1);
}
