namespace Ascua.Domain.Abstractions;

/// <summary>
/// Generador determinista (SplitMix64) a partir de una semilla. Sirve para pruebas,
/// bots y reproducciones; en producción el servidor usa un CSPRNG y la semilla nunca sale de él.
/// </summary>
public sealed class SeededRandom(ulong seed) : IRandom
{
    private ulong _state = seed;

    public int Next(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        _state += 0x9E3779B97F4A7C15UL;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (int)(z % (ulong)maxExclusive);
    }
}
