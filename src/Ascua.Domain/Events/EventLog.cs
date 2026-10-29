namespace Ascua.Domain.Events;

/// <summary>Acumula los eventos de un comando y les asigna números de secuencia consecutivos.</summary>
internal sealed class EventLog(long lastSeq, int round)
{
    private readonly List<DomainEvent> _events = [];

    public long LastSeq { get; private set; } = lastSeq;

    public IReadOnlyList<DomainEvent> Events => _events;

    public void Emit(DomainEvent domainEvent)
    {
        LastSeq++;
        _events.Add(domainEvent with { Seq = LastSeq, Round = round });
    }
}
