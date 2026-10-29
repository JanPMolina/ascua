namespace Ascua.Application.Abstractions;

/// <summary>Reloj del servidor; los plazos de ronda nunca vienen del cliente.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
