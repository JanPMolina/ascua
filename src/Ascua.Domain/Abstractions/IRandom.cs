namespace Ascua.Domain.Abstractions;

/// <summary>
/// Fuente de azar inyectada al dominio. En producción la implementa un generador
/// criptográficamente seguro (Infrastructure); en pruebas, una secuencia fija.
/// </summary>
public interface IRandom
{
    /// <summary>Entero en [0, maxExclusive).</summary>
    int Next(int maxExclusive);
}
