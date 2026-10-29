using Ascua.Infrastructure.Auth;

namespace Ascua.Integration.Tests;

public sealed class PasswordHasherTests
{
    private readonly Argon2idPasswordHasher _hasher = new();

    [Fact]
    public void Hash_uses_phc_format_with_documented_parameters()
    {
        var hash = _hasher.Hash("una-contrasena-larga");

        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", hash, StringComparison.Ordinal);
        Assert.DoesNotContain("una-contrasena-larga", hash, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_password_gets_a_different_salt_each_time()
    {
        Assert.NotEqual(_hasher.Hash("repetida-1234"), _hasher.Hash("repetida-1234"));
    }

    [Fact]
    public void Verify_accepts_only_the_original_password()
    {
        var hash = _hasher.Hash("la-buena-1234");

        Assert.True(_hasher.Verify("la-buena-1234", hash));
        Assert.False(_hasher.Verify("la-mala-12345", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("texto-plano")]
    [InlineData("$argon2id$v=19$m=999999999,t=2,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$no base64$x")]
    public void Verify_rejects_malformed_or_abusive_hashes(string stored)
    {
        Assert.False(_hasher.Verify("lo-que-sea-123", stored));
    }
}
