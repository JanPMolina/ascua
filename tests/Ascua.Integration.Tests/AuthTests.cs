using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ascua.Api.Endpoints;
using Ascua.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ascua.Integration.Tests;

[Collection(ApiCollection.Name)]
public sealed class AuthTests(AscuaApiFactory factory) : IDisposable
{
    private const string Password = "correcto-caballo-bateria";

    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Register_then_login_returns_es256_tokens_and_profile()
    {
        var username = NewUsername();

        using var register = await PostAsync("/api/v1/auth/register", new { username, email = $"{username}@example.com", password = Password });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        Assert.Equal(UserEndpoints.MePath, register.Headers.Location?.OriginalString);
        var profile = await register.Content.ReadFromJsonAsync<UserProfileResponse>();
        Assert.Equal(username, profile!.Username);
        Assert.Equal("player", profile.Role);

        var tokens = await LoginAsync(username, Password);
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.InRange(tokens.ExpiresIn, 590, 600);
        Assert.Equal("ES256", JwtHeader(tokens.AccessToken).GetProperty("alg").GetString());

        using var me = await GetMeAsync(tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(profile.Id, (await me.Content.ReadFromJsonAsync<UserProfileResponse>())!.Id);
    }

    [Fact]
    public async Task Login_is_case_insensitive_on_username()
    {
        var username = await RegisterAsync();

        var tokens = await LoginAsync(username.ToUpperInvariant(), Password);

        Assert.False(string.IsNullOrEmpty(tokens.AccessToken));
    }

    [Fact]
    public async Task Duplicate_username_is_conflict_problem()
    {
        var username = await RegisterAsync();

        using var response = await PostAsync("/api/v1/auth/register", new { username = username.ToUpperInvariant(), email = $"otro-{username}@example.com", password = Password });

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "username-taken");
    }

    [Fact]
    public async Task Invalid_registration_returns_validation_problem_with_field_errors()
    {
        using var response = await PostAsync("/api/v1/auth/register", new { username = "a b", email = "no-es-correo", password = "corta" });

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("username", out _));
        Assert.True(errors.TryGetProperty("email", out _));
        Assert.True(errors.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Malformed_json_returns_validation_problem()
    {
        using var content = new StringContent("{ no es json", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(new Uri("/api/v1/auth/login", UriKind.Relative), content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_return_the_same_401()
    {
        var username = await RegisterAsync();

        using var wrongPassword = await PostAsync("/api/v1/auth/login", new { username, password = "otra-contrasena-mala" });
        using var unknownUser = await PostAsync("/api/v1/auth/login", new { username = NewUsername(), password = Password });

        await AssertProblemAsync(wrongPassword, HttpStatusCode.Unauthorized, "invalid-credentials");
        await AssertProblemAsync(unknownUser, HttpStatusCode.Unauthorized, "invalid-credentials");
    }

    [Fact]
    public async Task Refresh_rotates_token_and_new_token_works()
    {
        var username = await RegisterAsync();
        var first = await LoginAsync(username, Password);

        var second = await RefreshAsync(first.RefreshToken);
        var third = await RefreshAsync(second.RefreshToken);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(second.RefreshToken, third.RefreshToken);
        using var me = await GetMeAsync(third.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_the_whole_family()
    {
        var username = await RegisterAsync();
        var original = await LoginAsync(username, Password);
        var otherSession = await LoginAsync(username, Password);
        var rotated = await RefreshAsync(original.RefreshToken);

        using var reuse = await PostAsync("/api/v1/auth/refresh", new { refreshToken = original.RefreshToken });
        await AssertProblemAsync(reuse, HttpStatusCode.Unauthorized, "refresh-token-reused");

        // El token legítimo más reciente de esa familia también quedó revocado…
        using var afterReuse = await PostAsync("/api/v1/auth/refresh", new { refreshToken = rotated.RefreshToken });
        await AssertProblemAsync(afterReuse, HttpStatusCode.Unauthorized, "refresh-token-reused");

        // …pero otra sesión (otra familia) del mismo usuario sigue viva.
        var stillAlive = await RefreshAsync(otherSession.RefreshToken);
        Assert.False(string.IsNullOrEmpty(stillAlive.AccessToken));
    }

    [Fact]
    public async Task Concurrent_refresh_with_the_same_token_lets_only_one_win()
    {
        var username = await RegisterAsync();
        var tokens = await LoginAsync(username, Password);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => PostAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken })));

        try
        {
            Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.OK) <= 1);
            Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Unauthorized);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Unknown_refresh_token_is_401()
    {
        using var response = await PostAsync("/api/v1/auth/refresh", new { refreshToken = "no-existe" });

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "invalid-refresh-token");
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        var username = await RegisterAsync();
        var tokens = await LoginAsync(username, Password);

        using var logout = await PostAsync("/api/v1/auth/logout", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        using var refresh = await PostAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Suspended_user_cannot_login_refresh_or_read_profile()
    {
        var username = await RegisterAsync();
        var tokens = await LoginAsync(username, Password);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
            var user = await db.Users.SingleAsync(u => u.NormalizedUsername == username.ToLowerInvariant());
            user.Suspend(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        using var login = await PostAsync("/api/v1/auth/login", new { username, password = Password });
        await AssertProblemAsync(login, HttpStatusCode.Forbidden, "account-suspended");

        using var refresh = await PostAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        await AssertProblemAsync(refresh, HttpStatusCode.Forbidden, "account-suspended");

        using var me = await GetMeAsync(tokens.AccessToken);
        await AssertProblemAsync(me, HttpStatusCode.Forbidden, "account-suspended");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("no.es.jwt")]
    public async Task Profile_without_valid_token_is_401_problem(string? token)
    {
        using var response = await GetMeAsync(token);

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Fact]
    public async Task Changing_password_requires_current_one_and_closes_sessions()
    {
        var username = await RegisterAsync();
        var tokens = await LoginAsync(username, Password);
        const string newPassword = "nueva-contrasena-larga";

        using var wrong = await PatchMeAsync(tokens.AccessToken, new { currentPassword = "no-es-la-actual", newPassword });
        await AssertProblemAsync(wrong, HttpStatusCode.BadRequest, "invalid-current-password");

        using var ok = await PatchMeAsync(tokens.AccessToken, new { currentPassword = Password, newPassword });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var oldRefresh = await PostAsync("/api/v1/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
        using var oldPassword = await PostAsync("/api/v1/auth/login", new { username, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        await LoginAsync(username, newPassword);
    }

    [Fact]
    public async Task Changing_email_to_one_in_use_is_conflict()
    {
        var taken = await RegisterAsync();
        var username = await RegisterAsync();
        var tokens = await LoginAsync(username, Password);

        using var response = await PatchMeAsync(tokens.AccessToken, new { email = $"{taken.ToUpperInvariant()}@example.com" });

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "email-taken");
    }

    [Fact]
    public async Task Passwords_and_hashes_never_appear_in_logs_or_responses()
    {
        var username = NewUsername();
        var secret = $"Secreto-{Guid.NewGuid():N}";
        var wrongSecret = $"Erroneo-{Guid.NewGuid():N}";
        var bodies = new List<string>();

        async Task Send(string path, object body)
        {
            using var response = await PostAsync(path, body);
            bodies.Add(await response.Content.ReadAsStringAsync());
        }

        await Send("/api/v1/auth/register", new { username, email = $"{username}@example.com", password = secret });
        await Send("/api/v1/auth/login", new { username, password = wrongSecret });
        await Send("/api/v1/auth/login", new { username, password = secret });
        var tokens = await LoginAsync(username, secret);
        using (var me = await GetMeAsync(tokens.AccessToken))
        {
            bodies.Add(await me.Content.ReadAsStringAsync());
        }

        var logs = factory.Logs.Entries.ToArray();
        Assert.NotEmpty(logs);
        foreach (var text in logs.Concat(bodies))
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(wrongSecret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("$argon2id$", text, StringComparison.Ordinal);
        }

        Assert.All(bodies, b => Assert.DoesNotContain("password", b, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Auth_endpoints_are_rate_limited_with_retry_after()
    {
        using var limited = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Ascua:RateLimiting:Auth:PermitLimit"] = "3",
            ["Ascua:RateLimiting:Auth:Window"] = "00:01:00",
        });
        using var client = limited.CreateClient();

        HttpResponseMessage? last = null;
        for (var i = 0; i < 4; i++)
        {
            last?.Dispose();
            last = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = "nadie", password = "da-igual-123" });
        }

        using (last)
        {
            await AssertProblemAsync(last!, HttpStatusCode.TooManyRequests, "rate-limited");
            Assert.True(last!.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        }
    }

    private static string NewUsername() => $"u_{Guid.NewGuid():N}"[..20];

    private async Task<string> RegisterAsync()
    {
        var username = NewUsername();
        using var response = await PostAsync("/api/v1/auth/register", new { username, email = $"{username}@example.com", password = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return username;
    }

    private async Task<TokenResponse> LoginAsync(string username, string password)
    {
        using var response = await PostAsync("/api/v1/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private async Task<TokenResponse> RefreshAsync(string refreshToken)
    {
        using var response = await PostAsync("/api/v1/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body) =>
        _client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body);

    private async Task<HttpResponseMessage> GetMeAsync(string? accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(UserEndpoints.MePath, UriKind.Relative));
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PatchMeAsync(string accessToken, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, new Uri(UserEndpoints.MePath, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var problem = document.RootElement.Clone();
        Assert.Equal($"urn:ascua:problem:{code}", problem.GetProperty("type").GetString());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        return problem;
    }

    private static JsonElement JwtHeader(string jwt)
    {
        var header = jwt.Split('.')[0];
        var json = Convert.FromBase64String(header.Replace('-', '+').Replace('_', '/').PadRight(header.Length + ((4 - (header.Length % 4)) % 4), '='));
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
