using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ascua.Application.Users;
using Konscious.Security.Cryptography;

namespace Ascua.Infrastructure.Auth;

/// <summary>
/// Hash de contraseñas con Argon2id (RFC 9106) en formato PHC:
/// <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;sal&gt;$&lt;hash&gt;</c>.
/// Parámetros: los mínimos recomendados por OWASP (Password Storage Cheat Sheet) para Argon2id:
/// 19 MiB de memoria, 2 iteraciones, paralelismo 1; sal aleatoria de 16 bytes y salida de 32 bytes.
/// Los parámetros se guardan en cada hash, así que subirlos más adelante no invalida los existentes.
/// </summary>
public sealed class Argon2idPasswordHasher : IPasswordHasher
{
    public const int MemoryKiB = 19 * 1024;
    public const int Iterations = 2;
    public const int Parallelism = 1;
    public const int SaltSize = 16;
    public const int HashSize = 32;

    private const string Prefix = "$argon2id$v=19$";

    // Límites al verificar, para que un hash manipulado en la base no dispare un consumo enorme.
    private const int MaxMemoryKiB = 256 * 1024;
    private const int MaxIterations = 10;
    private const int MaxParallelism = 8;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt, MemoryKiB, Iterations, Parallelism, HashSize);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}m={MemoryKiB},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(hash).TrimEnd('=')}");
    }

    public bool Verify(string password, string passwordHash)
    {
        if (!TryParse(passwordHash, out var memory, out var iterations, out var parallelism, out var salt, out var expected))
        {
            return false;
        }

        var actual = Compute(password, salt, memory, iterations, parallelism, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Compute(string password, byte[] salt, int memoryKiB, int iterations, int parallelism, int length)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            using var argon2 = new Argon2id(passwordBytes)
            {
                Salt = salt,
                MemorySize = memoryKiB,
                Iterations = iterations,
                DegreeOfParallelism = parallelism,
            };
            return argon2.GetBytes(length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static bool TryParse(
        string phc, out int memory, out int iterations, out int parallelism, out byte[] salt, out byte[] hash)
    {
        memory = iterations = parallelism = 0;
        salt = hash = [];

        if (!phc.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = phc[Prefix.Length..].Split('$');
        if (parts.Length != 3)
        {
            return false;
        }

        foreach (var pair in parts[0].Split(','))
        {
            var kv = pair.Split('=');
            if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            switch (kv[0])
            {
                case "m": memory = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
                default: return false;
            }
        }

        if (memory is < 8 or > MaxMemoryKiB || iterations is < 1 or > MaxIterations || parallelism is < 1 or > MaxParallelism)
        {
            return false;
        }

        return TryDecode(parts[1], out salt) && TryDecode(parts[2], out hash) && salt.Length >= 8 && hash.Length >= 16;
    }

    private static bool TryDecode(string unpadded, out byte[] bytes)
    {
        var padded = unpadded.PadRight(unpadded.Length + ((4 - (unpadded.Length % 4)) % 4), '=');
        bytes = new byte[padded.Length];
        if (!Convert.TryFromBase64String(padded, bytes, out var written))
        {
            bytes = [];
            return false;
        }

        bytes = bytes[..written];
        return true;
    }
}
