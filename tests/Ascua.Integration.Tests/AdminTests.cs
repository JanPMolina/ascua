using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ascua.Api.Endpoints;
using Ascua.Api.Realtime;
using Ascua.Application.Audit;
using Ascua.Application.Matches;
using Ascua.Application.Users;
using Ascua.Infrastructure.Auth;
using Ascua.Infrastructure.Persistence;
using Ascua.Integration.Tests.Realtime;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Ascua.Integration.Tests;

/// <summary>Administración: solo admins, cada acción auditada, terminar partidas y suspender cuentas de verdad.</summary>
[Collection(ApiCollection.Name)]
public sealed class AdminTests(AscuaApiFactory factory) : IDisposable
{
    private const string Password = "correcto-caballo-bateria";

    private readonly HttpClient _client = factory.CreateClient();

    public static TheoryData<string, string> AdminEndpointsData => new()
    {
        { "GET", "/api/v1/admin/matches?status=active" },
        { "GET", "/api/v1/admin/connections" },
        { "POST", $"/api/v1/admin/matches/{Guid.NewGuid()}/terminate" },
        { "POST", $"/api/v1/admin/users/{Guid.NewGuid()}/suspend" },
        { "POST", $"/api/v1/admin/users/{Guid.NewGuid()}/unsuspend" },
        { "GET", "/api/v1/admin/audit" },
    };

    public void Dispose() => _client.Dispose();

    [Theory]
    [MemberData(nameof(AdminEndpointsData))]
    public async Task Admin_endpoints_reject_anonymous_with_401_and_players_with_403(string method, string path)
    {
        using var anonymous = await SendAsync(method, path, null, new { reason = "prueba de acceso" });
        await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "unauthorized");

        var player = await RegisterAsync();
        using var forbidden = await SendAsync(method, path, player.Tokens.AccessToken, new { reason = "prueba de acceso" });
        await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task Admin_actions_require_a_reason()
    {
        var admin = await AdminAsync();
        var player = await RegisterAsync();

        using var response = await SendAsync("POST", $"/api/v1/admin/users/{player.Id}/suspend", admin.Tokens.AccessToken, new { reason = "" });

        var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation");
        Assert.True(problem.GetProperty("errors").TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task Admin_lists_active_matches_and_live_connections_without_secrets()
    {
        var admin = await AdminAsync();
        var a = await RealtimeSetup.RegisterAsync(factory);
        var b = await RealtimeSetup.RegisterAsync(factory);
        var matchId = await RealtimeSetup.LaunchAsync(factory, a, b);
        var (ca, _) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        await using var _ca = ca;

        var page = await GetJsonAsync($"/api/v1/admin/matches?status=active&page=1&pageSize=100", admin.Tokens.AccessToken);
        var match = page.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("matchId").GetGuid() == matchId);
        Assert.Equal("DRAFTING", match.GetProperty("status").GetString());
        Assert.Equal(2, match.GetProperty("players").GetArrayLength());
        Assert.Equal(1, match.GetProperty("connectedPlayers").GetInt32());
        Assert.True(match.TryGetProperty("lastActivityAt", out _));

        using var connections = await SendAsync("GET", "/api/v1/admin/connections", admin.Tokens.AccessToken, null);
        var body = await connections.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, connections.StatusCode);
        var overview = JsonDocument.Parse(body).RootElement;
        var live = overview.GetProperty("matches").EnumerateArray().Single(m => m.GetProperty("matchId").GetGuid() == matchId);
        var connected = Assert.Single(live.GetProperty("players").EnumerateArray());
        Assert.Equal(a.Id, connected.GetProperty("userId").GetGuid());
        Assert.True(connected.GetProperty("connections").GetInt32() >= 1);
        Assert.DoesNotContain(a.AccessToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain("ticket", body, StringComparison.OrdinalIgnoreCase);

        using var badStatus = await SendAsync("GET", "/api/v1/admin/matches?status=finished", admin.Tokens.AccessToken, null);
        await AssertProblemAsync(badStatus, HttpStatusCode.BadRequest, "validation");
    }

    [Fact]
    public async Task Terminating_a_match_stops_it_notifies_players_releases_them_and_is_audited()
    {
        var admin = await AdminAsync();
        var a = await RealtimeSetup.RegisterAsync(factory);
        var b = await RealtimeSetup.RegisterAsync(factory);
        var matchId = await RealtimeSetup.LaunchAsync(factory, a, b);
        var (ca, _) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(factory, b, matchId);
        await using var _ca = ca;
        await using var _cb = cb;

        // Marca de "partida activa" como la deja el lobby al lanzar.
        var redis = factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        await redis.StringSetAsync($"ascua:user:{a.Id:N}:active_match", matchId.ToString("N"));

        using var response = await SendAsync("POST", $"/api/v1/admin/matches/{matchId}/terminate", admin.Tokens.AccessToken, new { reason = "Partida bloqueada por un bug" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TERMINATED", result.GetProperty("status").GetString());

        foreach (var client in new[] { ca, cb })
        {
            var finished = await client.WaitForAsync<MatchFinishedMessage>("MatchFinished");
            Assert.Equal("TERMINATED", finished.Outcome);
            var kicked = await client.WaitForAsync<ErrorPushDto>("Error", e => e.Error?.Code == RealtimeConnections.KickedByAdmin);
            Assert.Equal(matchId.ToString("D"), kicked.MatchId);
            await WaitUntilAsync(() => client.Connection.State == HubConnectionState.Disconnected, "la conexión se cierre");
        }

        Assert.False(await redis.KeyExistsAsync($"ascua:user:{a.Id:N}:active_match"));
        var engine = factory.Services.GetRequiredService<MatchEngine>();
        Assert.Equal(MatchStatus.Terminated, (await engine.GetAsync(matchId, CancellationToken.None))!.Status);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        Assert.Equal("TERMINATED", (await db.Matches.AsNoTracking().SingleAsync(m => m.Id == matchId)).Status);
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(e => e.TargetId == matchId.ToString("D"));
        Assert.Equal(AuditActions.MatchTerminated, audit.Action);
        Assert.Equal(admin.Id, audit.ActorId);
        Assert.Equal("Partida bloqueada por un bug", audit.Reason);
        Assert.Contains("DRAFTING", audit.Metadata, StringComparison.Ordinal);

        // Una partida ya terminada no se puede volver a terminar (ni deja otra auditoría).
        using var again = await SendAsync("POST", $"/api/v1/admin/matches/{matchId}/terminate", admin.Tokens.AccessToken, new { reason = "otra vez" });
        await AssertProblemAsync(again, HttpStatusCode.Conflict, "match-not-active");
        using var missing = await SendAsync("POST", $"/api/v1/admin/matches/{Guid.NewGuid()}/terminate", admin.Tokens.AccessToken, new { reason = "no existe" });
        await AssertProblemAsync(missing, HttpStatusCode.NotFound, "match-not-found");
        Assert.Equal(1, await db.AuditLog.CountAsync(e => e.TargetId == matchId.ToString("D")));
    }

    [Fact]
    public async Task Suspending_a_user_cuts_login_refresh_live_access_token_and_websocket()
    {
        var admin = await AdminAsync();
        var target = await RegisterAsync();
        var other = await RealtimeSetup.RegisterAsync(factory);
        var targetPlayer = new TestPlayer(target.Id, target.Username, target.Tokens.AccessToken);
        var matchId = await RealtimeSetup.LaunchAsync(factory, targetPlayer, other);
        var (connection, _) = await RealtimeSetup.JoinAsync(factory, targetPlayer, matchId);
        await using var _c = connection;

        // Antes de suspender, el access token sirve.
        using (var before = await SendAsync("GET", UserEndpoints.MePath, target.Tokens.AccessToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        using var suspend = await SendAsync("POST", $"/api/v1/admin/users/{target.Id}/suspend", admin.Tokens.AccessToken, new { reason = "Trampas reportadas" });
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        Assert.True((await suspend.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("suspended").GetBoolean());

        // El WS recibe KICKED_BY_ADMIN y se cierra.
        await connection.WaitForAsync<ErrorPushDto>("Error", e => e.Error?.Code == RealtimeConnections.KickedByAdmin);
        await WaitUntilAsync(() => connection.Connection.State == HubConnectionState.Disconnected, "la conexión se cierre");

        // Access token vigente, refresh y login: todos rechazados.
        using var me = await SendAsync("GET", UserEndpoints.MePath, target.Tokens.AccessToken, null);
        await AssertProblemAsync(me, HttpStatusCode.Forbidden, "account-suspended");
        using var ticket = await RealtimeSetup.RequestTicketAsync(factory, targetPlayer, matchId);
        await AssertProblemAsync(ticket, HttpStatusCode.Forbidden, "account-suspended");
        using var refresh = await SendAsync("POST", "/api/v1/auth/refresh", null, new { refreshToken = target.Tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        using var login = await SendAsync("POST", "/api/v1/auth/login", null, new { username = target.Username, password = Password });
        await AssertProblemAsync(login, HttpStatusCode.Forbidden, "account-suspended");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
            Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == target.Id && t.RevokedAt == null));
        }

        // Un admin no puede suspenderse a sí mismo; un usuario inexistente es 404.
        using var self = await SendAsync("POST", $"/api/v1/admin/users/{admin.Id}/suspend", admin.Tokens.AccessToken, new { reason = "auto" });
        await AssertProblemAsync(self, HttpStatusCode.Conflict, "cannot-target-self");
        using var ghost = await SendAsync("POST", $"/api/v1/admin/users/{Guid.NewGuid()}/suspend", admin.Tokens.AccessToken, new { reason = "fantasma" });
        await AssertProblemAsync(ghost, HttpStatusCode.NotFound, "user-not-found");

        // Rehabilitado: vuelve a entrar con un login nuevo (el token viejo sigue muerto por su refresh revocado).
        using var unsuspend = await SendAsync("POST", $"/api/v1/admin/users/{target.Id}/unsuspend", admin.Tokens.AccessToken, new { reason = "Apelación aceptada" });
        Assert.Equal(HttpStatusCode.OK, unsuspend.StatusCode);
        var relogin = await LoginAsync(target.Username);
        using (var after = await SendAsync("GET", UserEndpoints.MePath, relogin.AccessToken, null))
        {
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }

        var audit = await GetJsonAsync("/api/v1/admin/audit?page=1&pageSize=100", admin.Tokens.AccessToken);
        var actions = audit.GetProperty("items").EnumerateArray()
            .Where(e => e.GetProperty("targetId").GetString() == target.Id.ToString("D"))
            .Select(e => e.GetProperty("action").GetString())
            .ToList();
        Assert.Equal([AuditActions.UserUnsuspended, AuditActions.UserSuspended], actions);
    }

    [Fact]
    public async Task Audit_log_never_contains_passwords_or_tokens_and_is_append_only()
    {
        var admin = await AdminAsync();
        var target = await RegisterAsync();
        using (var suspend = await SendAsync("POST", $"/api/v1/admin/users/{target.Id}/suspend", admin.Tokens.AccessToken, new { reason = "Revisión de seguridad" }))
        {
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        }

        using var response = await SendAsync("GET", "/api/v1/admin/audit?page=1&pageSize=100", admin.Tokens.AccessToken, null);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(target.Id.ToString("D"), body, StringComparison.Ordinal);
        foreach (var secret in new[] { Password, target.Tokens.AccessToken, target.Tokens.RefreshToken, admin.Tokens.AccessToken, "$argon2id$" })
        {
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var id = target.Id.ToString("D");
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.AuditLog.Where(e => e.TargetId == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Reason, "editado")));
        await Assert.ThrowsAnyAsync<Exception>(() => db.AuditLog.Where(e => e.TargetId == id).ExecuteDeleteAsync());
        Assert.Equal("Revisión de seguridad", (await db.AuditLog.AsNoTracking().SingleAsync(e => e.TargetId == id)).Reason);
    }

    [Fact]
    public async Task Bootstrap_setting_promotes_the_named_user_once_and_audits_it_as_system()
    {
        var user = await RegisterAsync();
        using var host = factory.WithSettings(new Dictionary<string, string?> { [AdminBootstrap.UsernameKey] = user.Username.ToUpperInvariant() });
        using (host.CreateClient())
        {
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        Assert.Equal(UserRole.Admin, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id)).Role);
        var audit = await db.AuditLog.AsNoTracking().SingleAsync(e => e.TargetId == user.Id.ToString("D"));
        Assert.Equal(AuditActions.UserPromoted, audit.Action);
        Assert.Null(audit.ActorId);

        // Con el rol nuevo en un token nuevo, ya entra a la administración.
        var tokens = await LoginAsync(user.Username);
        using var response = await SendAsync("GET", "/api/v1/admin/audit", tokens.AccessToken, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<TestUser> AdminAsync()
    {
        var user = await RegisterAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
            var entity = await db.Users.SingleAsync(u => u.Id == user.Id);
            entity.PromoteToAdmin(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }

        // El rol viaja en el JWT: hace falta un login nuevo.
        return user with { Tokens = await LoginAsync(user.Username) };
    }

    private async Task<TestUser> RegisterAsync()
    {
        var username = $"ad{Guid.NewGuid():N}"[..20];
        using var register = await _client.PostAsJsonAsync("/api/v1/auth/register", new { username, email = $"{username}@example.com", password = Password });
        register.EnsureSuccessStatusCode();
        var profile = (await register.Content.ReadFromJsonAsync<UserProfileResponse>())!;
        return new TestUser(profile.Id, username, await LoginAsync(username));
    }

    private async Task<TokenResponse> LoginAsync(string username)
    {
        using var login = await _client.PostAsJsonAsync("/api/v1/auth/login", new { username, password = Password });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string path, string? accessToken, object? body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (body is not null && method != "GET")
        {
            request.Content = JsonContent.Create(body);
        }

        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> GetJsonAsync(string path, string accessToken)
    {
        using var response = await SendAsync("GET", path, accessToken, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(Problems.ContentType, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal($"urn:ascua:problem:{code}", problem.GetProperty("type").GetString());
        return problem;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var giveUpAt = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUpAt)
            {
                throw new TimeoutException($"No se cumplió a tiempo: {what}.");
            }

            await Task.Delay(50);
        }
    }

    private sealed record TestUser(Guid Id, string Username, TokenResponse Tokens);
}
