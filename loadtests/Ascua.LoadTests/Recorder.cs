using System.Collections.Concurrent;

namespace Ascua.LoadTests;

public enum Kind
{
    /// <summary>Latencia de la meta (ms): disparo del cierre → último jugador recibe RoundResolved.</summary>
    RoundLatency,

    /// <summary>Igual, pero de una ronda que cerró por vencimiento del plazo.</summary>
    RoundLatencyDeadline,

    /// <summary>Ida y vuelta de SubmitOrder (ms).</summary>
    OrderRtt,
    OrderSent,
    OrderAccepted,

    /// <summary>Rechazo explícito STALE_ROUND (la orden llegó tarde): no se pierde, el servidor avisa.</summary>
    OrderStale,

    /// <summary>Error inesperado de SubmitOrder (excepción o código fuera de lo previsto).</summary>
    OrderError,

    /// <summary>ACK DUPLICATE_ACTION para un clientActionId nuevo: jugada duplicada.</summary>
    Duplicate,

    /// <summary>previousSeq de RoundResolved distinto del último seq visto: hueco o repetición.</summary>
    SeqGap,

    /// <summary>Orden aceptada que el servidor reemplazó por GUARDAR (DEFAULT_GUARD_APPLIED): jugada perdida.</summary>
    LostOrder,

    /// <summary>Un jugador no recibió el RoundResolved de una ronda cerrada.</summary>
    MissingBroadcast,
    RoundCompleted,

    /// <summary>Fallo al crear la partida, conectar o unirse.</summary>
    SetupError,
    MatchStarted,
    MatchFinished,

    /// <summary>Desfase estimado servidor − cliente (ms) de una conexión.</summary>
    ClockOffset,
}

public readonly record struct Sample(DateTime At, Kind Kind, double Value);

/// <summary>Todo lo medido, con su instante, para cortarlo después por etapa de la rampa.</summary>
public sealed class Recorder
{
    private readonly ConcurrentQueue<Sample> _samples = new();
    private readonly ConcurrentQueue<(DateTime At, int Active)> _active = new();
    private int _activeMatches;

    public IReadOnlyCollection<Sample> Samples => _samples;

    public IReadOnlyCollection<(DateTime At, int Active)> ActiveMatches => _active;

    public void Add(Kind kind, double value = 1) => _samples.Enqueue(new Sample(DateTime.UtcNow, kind, value));

    public void MatchStarted()
    {
        Interlocked.Increment(ref _activeMatches);
        Add(Kind.MatchStarted);
    }

    public void MatchEnded()
    {
        Interlocked.Decrement(ref _activeMatches);
        Add(Kind.MatchFinished);
    }

    /// <summary>Muestrea las partidas en curso una vez por segundo.</summary>
    public async Task SampleActiveAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                _active.Enqueue((DateTime.UtcNow, Volatile.Read(ref _activeMatches)));
            }
        }
        catch (OperationCanceledException)
        {
            // Fin de la prueba.
        }
    }
}
