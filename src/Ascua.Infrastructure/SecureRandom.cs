using System.Security.Cryptography;
using Ascua.Domain.Abstractions;

namespace Ascua.Infrastructure;

/// <summary>Azar del servidor con generador criptográficamente seguro.</summary>
public sealed class SecureRandom : IRandom
{
    public int Next(int maxExclusive) => RandomNumberGenerator.GetInt32(maxExclusive);
}
