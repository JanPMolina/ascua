using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Ascua.Application.Users;

/// <summary>Genera refresh tokens opacos (256 bits, base64url) y calcula su hash para guardarlo.</summary>
public static class RefreshTokenSecret
{
    private const int SizeInBytes = 32;

    /// <summary>Longitud máxima aceptada al recibir un token; evita hashear entradas enormes.</summary>
    public const int MaxEncodedLength = 128;

    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SizeInBytes));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
