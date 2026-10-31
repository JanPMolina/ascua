using System.Security.Claims;
using System.Security.Cryptography;
using Ascua.Infrastructure.Auth;
using Ascua.Infrastructure.Persistence;
using Ascua.Infrastructure.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ascua.Integration.Tests;

/// <summary>
/// Lo que la app hace distinto en Azure, probado sin Azure: la clave JWT desde Key Vault (con un origen
/// falso) y las validaciones de la configuración de Entra para Postgres y Redis.
/// </summary>
public sealed class AzureReadinessTests
{
    [Fact]
    public async Task Key_vault_provider_signs_with_the_newest_version_and_still_validates_older_enabled_ones()
    {
        var source = new FakeSource(Version("v2"), [Version("v1")]);
        using var provider = await KeyVaultSigningKeyProvider.CreateAsync(source, NullLogger<KeyVaultSigningKeyProvider>.Instance, TimeSpan.FromHours(1));

        Assert.Equal("kv-v2", provider.SigningKey.KeyId);
        Assert.Equal(["kv-v2", "kv-v1"], provider.ValidationKeys.Select(k => k.KeyId));

        // Un token firmado con la versión anterior (antes de rotar) sigue siendo válido.
        var oldToken = Sign(KeyVaultSigningKeyProvider.Import(source.Others[0]));
        var result = await Validate(oldToken, provider);
        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public async Task Key_vault_provider_picks_up_a_rotation_and_keeps_the_last_good_keys_when_key_vault_fails()
    {
        var source = new FakeSource(Version("v1"), []);
        using var provider = await KeyVaultSigningKeyProvider.CreateAsync(source, NullLogger<KeyVaultSigningKeyProvider>.Instance, TimeSpan.FromHours(1));

        source.Current = Version("v2");
        source.Others = [Version("v1")];
        await provider.RefreshAsync();
        Assert.Equal("kv-v2", provider.SigningKey.KeyId);

        source.Fail = true;
        await provider.RefreshAsync();
        Assert.Equal("kv-v2", provider.SigningKey.KeyId);
        Assert.Equal(2, provider.ValidationKeys.Count);
    }

    [Fact]
    public void Key_vault_provider_rejects_keys_that_are_not_p256()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var version = new SigningKeyVersion("bad", p384.ExportPkcs8PrivateKeyPem(), null);

        var error = Assert.Throws<InvalidOperationException>(() => KeyVaultSigningKeyProvider.Import(version));
        Assert.Contains("P-256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_old_version_stops_validating_its_tokens()
    {
        var old = Version("v1");
        var source = new FakeSource(Version("v2"), [old]);
        using var provider = await KeyVaultSigningKeyProvider.CreateAsync(source, NullLogger<KeyVaultSigningKeyProvider>.Instance, TimeSpan.FromHours(1));
        var oldToken = Sign(KeyVaultSigningKeyProvider.Import(old));

        // Rotación urgente: se deshabilita la versión comprometida en Key Vault.
        source.Others = [];
        await provider.RefreshAsync();

        Assert.False((await Validate(oldToken, provider)).IsValid);
    }

    [Fact]
    public void Postgres_with_entra_auth_refuses_a_connection_string_with_a_password()
    {
        var configuration = Config(
            ("ConnectionStrings:Postgres", "Host=db.postgres.database.azure.com;Database=ascua;Username=ascua-api;Password=no-debe-estar"),
            ("Ascua:Database:UseEntraAuth", "true"));

        var error = Assert.Throws<InvalidOperationException>(() => PostgresDataSource.Create(configuration));
        Assert.Contains("Password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redis_with_entra_auth_refuses_a_connection_string_with_a_password()
    {
        var configuration = Config(
            ("ConnectionStrings:Redis", "ascua.redis.cache.windows.net:6380,ssl=True,password=no-debe-estar"),
            ("Ascua:Redis:UseEntraAuth", "true"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => RedisConnection.BuildOptionsAsync(configuration));
        Assert.Contains("password", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_entra_auth_redis_options_are_the_plain_connection_string()
    {
        var options = await RedisConnection.BuildOptionsAsync(Config(("ConnectionStrings:Redis", "redis:6379")));

        Assert.StartsWith("redis:6379", options.ToString(), StringComparison.Ordinal);
        Assert.Null(options.Password);
        Assert.False(options.Ssl);
    }

    private static SigningKeyVersion Version(string id)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new SigningKeyVersion(id, key.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
    }

    private static string Sign(SecurityKey key) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = "ascua",
        Audience = "ascua-api",
        Subject = new ClaimsIdentity([new Claim("sub", "u1")]),
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256),
    });

    private static Task<TokenValidationResult> Validate(string token, ISigningKeyProvider keys) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "ascua",
            ValidAudience = "ascua-api",
            IssuerSigningKeyResolver = (_, _, _, _) => keys.ValidationKeys,
        });

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    private sealed class FakeSource(SigningKeyVersion current, IReadOnlyList<SigningKeyVersion> others) : ISigningKeySource
    {
        public SigningKeyVersion Current { get; set; } = current;

        public IReadOnlyList<SigningKeyVersion> Others { get; set; } = others;

        public bool Fail { get; set; }

        public Task<(SigningKeyVersion Current, IReadOnlyList<SigningKeyVersion> Others)> LoadAsync(CancellationToken cancellationToken) =>
            Fail ? throw new HttpRequestException("Key Vault no responde") : Task.FromResult((Current, Others));
    }
}
