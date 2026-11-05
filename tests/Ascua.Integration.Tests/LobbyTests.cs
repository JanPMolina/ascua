using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ascua.Api.Endpoints;
using Ascua.Application.Abstractions;
using Ascua.Application.Matches;
using Ascua.Application.Matchmaking;
using Ascua.Application.Rooms;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ascua.Integration.Tests;

[Collection(ApiCollection.Name)]
public sealed class LobbyTests(AscuaApiFactory factory) : IDisposable
{
    private const string Password = "correcto-caballo-bateria";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Public_room_is_listed_and_visible_to_others_without_private_details()
    {
        var host = await NewUserAsync();
        var other = await NewUserAsync();

        using var created = await SendAsync(HttpMethod.Post, "/api/v1/rooms", host, new { name = "Sala abierta", visibility = "PUBLIC", maxPlayers = 3 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var room = await ReadAsync(created);
        Assert.Equal($"/api/v1/rooms/{room.GetProperty("id").GetString()}", created.Headers.Location?.OriginalString);
        Assert.Equal("OPEN", room.GetProperty("status").GetString());
        Assert.True(room.GetProperty("members")[0].GetProperty("isHost").GetBoolean());

        using var list = await SendAsync(HttpMethod.Get, "/api/v1/rooms?page=1&pageSize=50", other);
        var items = (await ReadAsync(list)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString());
        Assert.Contains(room.GetProperty("id").GetString(), items);

        using var seen = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{room.GetProperty("id").GetString()}", other);
        Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await ReadAsync(seen)).GetProperty("matchId").ValueKind);

        await LeaveAsync(host, room);
    }

    [Fact]
    public async Task Private_room_is_hidden_from_strangers_and_joined_by_invite_code()
    {
        var host = await NewUserAsync();
        var guest = await NewUserAsync();
        var room = await CreateRoomAsync(host, "PRIVATE");
        var id = room.GetProperty("id").GetString();
        var code = room.GetProperty("inviteCode").GetString()!;
        Assert.Matches("^[23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{8}$", code);

        using (var hidden = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{id}", guest))
        {
            await AssertProblemAsync(hidden, HttpStatusCode.NotFound, "room-not-found");
        }

        using (var byId = await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/join", guest))
        {
            await AssertProblemAsync(byId, HttpStatusCode.NotFound, "room-not-found");
        }

        using var joined = await SendAsync(HttpMethod.Post, "/api/v1/rooms/join", guest, new { code = code.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        Assert.Equal(code, (await ReadAsync(joined)).GetProperty("inviteCode").GetString());

        using var current = await SendAsync(HttpMethod.Get, "/api/v1/rooms/current", guest);
        Assert.Equal(id, (await ReadAsync(current)).GetProperty("id").GetString());

        await LeaveAsync(guest, room);
        await LeaveAsync(host, room);
    }

    [Fact]
    public async Task Concurrent_joins_to_a_room_of_four_let_exactly_four_in()
    {
        var host = await NewUserAsync();
        var room = await CreateRoomAsync(host, "PUBLIC");
        var id = room.GetProperty("id").GetString();
        var joiners = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => NewUserAsync()));

        var responses = await Task.WhenAll(joiners.Select(user => SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/join", user)));
        try
        {
            Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
            foreach (var rejected in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
            {
                await AssertProblemAsync(rejected, HttpStatusCode.Conflict, "room-full");
            }
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }

        using var final = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{id}", host);
        Assert.Equal(4, (await ReadAsync(final)).GetProperty("members").GetArrayLength());
    }

    [Fact]
    public async Task Host_starts_when_everyone_is_ready_and_players_get_an_active_match()
    {
        var host = await NewUserAsync();
        var guest = await NewUserAsync();
        var room = await CreateRoomAsync(host, "PUBLIC");
        var id = room.GetProperty("id").GetString();
        (await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/join", guest)).Dispose();

        await PutOkAsync(host, $"/api/v1/rooms/{id}/hero", new { hero = "ERRATA" });
        using (var taken = await SendAsync(HttpMethod.Put, $"/api/v1/rooms/{id}/hero", guest, new { hero = "ERRATA" }))
        {
            await AssertProblemAsync(taken, HttpStatusCode.Conflict, "hero-taken");
        }

        using (var noHero = await SendAsync(HttpMethod.Put, $"/api/v1/rooms/{id}/ready", guest, new { ready = true }))
        {
            await AssertProblemAsync(noHero, HttpStatusCode.Conflict, "hero-required");
        }

        await PutOkAsync(guest, $"/api/v1/rooms/{id}/hero", new { hero = "POLIZON" });
        await PutOkAsync(host, $"/api/v1/rooms/{id}/ready", new { ready = true });
        using (var notReady = await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/start", host))
        {
            await AssertProblemAsync(notReady, HttpStatusCode.Conflict, "not-enough-ready");
        }

        await PutOkAsync(guest, $"/api/v1/rooms/{id}/ready", new { ready = true });
        using (var notHost = await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/start", guest))
        {
            await AssertProblemAsync(notHost, HttpStatusCode.Forbidden, "not-room-host");
        }

        using var started = await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/start", host);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var matchId = (await ReadAsync(started)).GetProperty("matchId").GetGuid();

        var request = factory.Launcher.Requests.Last(r => r.RoomId == Guid.Parse(id!));
        Assert.Equal([host.Id, guest.Id], request.Seats.Select(s => s.UserId));
        Assert.Equal(matchId, await factory.Services.GetRequiredService<IActiveMatchRegistry>().GetAsync(guest.Id, default));

        using (var again = await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/start", host))
        {
            Assert.Equal(matchId, (await ReadAsync(again)).GetProperty("matchId").GetGuid());
        }

        using (var view = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{id}", guest))
        {
            var body = await ReadAsync(view);
            Assert.Equal("STARTED", body.GetProperty("status").GetString());
            Assert.Equal(matchId, body.GetProperty("matchId").GetGuid());
        }

        using (var queue = await SendAsync(HttpMethod.Post, "/api/v1/matchmaking/queue", host))
        {
            await AssertProblemAsync(queue, HttpStatusCode.Conflict, "already-in-match");
        }

        using var status = await SendAsync(HttpMethod.Get, "/api/v1/matchmaking/queue", guest);
        Assert.Equal("MATCHED", (await ReadAsync(status)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Leaving_the_real_match_frees_players_to_queue_again()
    {
        var host = await NewUserAsync();
        var guest = await NewUserAsync();
        var room = await CreateRoomAsync(host, "PUBLIC");
        var id = room.GetProperty("id").GetString();
        (await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/join", guest)).Dispose();
        await PutOkAsync(host, $"/api/v1/rooms/{id}/hero", new { hero = "ERRATA" });
        await PutOkAsync(guest, $"/api/v1/rooms/{id}/hero", new { hero = "HILVAN" });
        await PutOkAsync(host, $"/api/v1/rooms/{id}/ready", new { ready = true });
        await PutOkAsync(guest, $"/api/v1/rooms/{id}/ready", new { ready = true });
        using var started = await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/start", host);
        var matchId = (await ReadAsync(started)).GetProperty("matchId").GetGuid();

        using (var active = await SendAsync(HttpMethod.Get, "/api/v1/users/me/active-match", guest))
        {
            Assert.Equal(matchId, (await ReadAsync(active)).GetProperty("matchId").GetGuid());
        }

        var engine = factory.Services.GetRequiredService<MatchEngine>();
        Assert.Null((await engine.RetireAsync(matchId, host.Id, default)).Error);
        Assert.Null((await engine.RetireAsync(matchId, guest.Id, default)).Error);

        using (var none = await SendAsync(HttpMethod.Get, "/api/v1/users/me/active-match", guest))
        {
            Assert.Equal(System.Text.Json.JsonValueKind.Null, (await ReadAsync(none)).GetProperty("matchId").ValueKind);
        }

        var registry = factory.Services.GetRequiredService<IActiveMatchRegistry>();
        Assert.Null(await registry.GetAsync(host.Id, default));
        Assert.Null(await registry.GetAsync(guest.Id, default));
        using var queue = await SendAsync(HttpMethod.Post, "/api/v1/matchmaking/queue", host);
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        (await SendAsync(HttpMethod.Delete, "/api/v1/matchmaking/queue", host)).Dispose();
    }

    [Fact]
    public async Task Start_without_realtime_service_is_503_and_the_room_reopens()
    {
        using var unavailable = factory
            .WithSettings(new Dictionary<string, string?> { ["Ascua:Matchmaking:Enabled"] = "false" })
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IMatchLauncher, UnavailableMatchLauncher>()));
        using var client = unavailable.CreateClient();
        var host = await NewUserAsync(client);
        var guest = await NewUserAsync(client);
        var room = await CreateRoomAsync(host, "PUBLIC");
        var id = room.GetProperty("id").GetString();
        (await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/join", guest)).Dispose();
        await PutOkAsync(host, $"/api/v1/rooms/{id}/hero", new { hero = "CAMPANERO" });
        await PutOkAsync(guest, $"/api/v1/rooms/{id}/hero", new { hero = "HILVAN" });
        await PutOkAsync(host, $"/api/v1/rooms/{id}/ready", new { ready = true });
        await PutOkAsync(guest, $"/api/v1/rooms/{id}/ready", new { ready = true });

        using var start = await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{id}/start", host);

        await AssertProblemAsync(start, HttpStatusCode.ServiceUnavailable, "matches-unavailable");
        using var view = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{id}", host);
        Assert.Equal("OPEN", (await ReadAsync(view)).GetProperty("status").GetString());

        await LeaveAsync(guest, room);
        await LeaveAsync(host, room);
    }

    [Fact]
    public async Task A_user_is_in_one_room_or_queue_at_a_time()
    {
        var user = await NewUserAsync();
        var room = await CreateRoomAsync(user, "PUBLIC");

        using (var second = await SendAsync(HttpMethod.Post, "/api/v1/rooms", user, new { name = "Otra sala" }))
        {
            await AssertProblemAsync(second, HttpStatusCode.Conflict, "already-in-room");
        }

        using (var queue = await SendAsync(HttpMethod.Post, "/api/v1/matchmaking/queue", user))
        {
            await AssertProblemAsync(queue, HttpStatusCode.Conflict, "already-in-room");
        }

        await LeaveAsync(user, room);
        using (var gone = await SendAsync(HttpMethod.Get, $"/api/v1/rooms/{room.GetProperty("id").GetString()}", user))
        {
            await AssertProblemAsync(gone, HttpStatusCode.NotFound, "room-not-found");
        }

        var queued = await NewUserAsync();
        using (var enqueue = await SendAsync(HttpMethod.Post, "/api/v1/matchmaking/queue", queued, new { preferredHero = "HILVAN" }))
        {
            var status = await ReadAsync(enqueue);
            Assert.Equal("QUEUED", status.GetProperty("status").GetString());
            Assert.Equal("HILVAN", status.GetProperty("preferredHero").GetString());
        }

        using (var create = await SendAsync(HttpMethod.Post, "/api/v1/rooms", queued, new { name = "Desde la cola" }))
        {
            await AssertProblemAsync(create, HttpStatusCode.Conflict, "already-in-queue");
        }

        using var leave = await SendAsync(HttpMethod.Delete, "/api/v1/matchmaking/queue", queued);
        Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        using var idle = await SendAsync(HttpMethod.Get, "/api/v1/matchmaking/queue", queued);
        Assert.Equal("IDLE", (await ReadAsync(idle)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Matchmaking_worker_groups_four_queued_players_into_one_match()
    {
        var users = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => NewUserAsync()));

        foreach (var response in await Task.WhenAll(users.Select(u => SendAsync(HttpMethod.Post, "/api/v1/matchmaking/queue", u, new { preferredHero = "ERRATA" }))))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            response.Dispose();
        }

        var matchIds = await Task.WhenAll(users.Select(WaitForMatchAsync));

        Assert.Single(matchIds.Distinct());
        var request = factory.Launcher.Requests.Last(r => r.Seats.Any(s => s.UserId == users[0].Id));
        Assert.Equal(users.Select(u => u.Id).Order(), request.Seats.Select(s => s.UserId).Order());
        Assert.Equal(4, request.Seats.Select(s => s.Hero).Distinct().Count());
    }

    [Fact]
    public async Task Concurrent_matchers_never_assign_a_player_to_two_matches()
    {
        var users = await Task.WhenAll(Enumerable.Range(0, 11).Select(_ => NewUserAsync()));
        var ids = users.Select(u => u.Id).ToHashSet();
        var queue = factory.Services.GetRequiredService<IMatchmakingQueue>();
        foreach (var response in await Task.WhenAll(users.Select(u => SendAsync(HttpMethod.Post, "/api/v1/matchmaking/queue", u))))
        {
            response.Dispose();
        }

        // Cuatro agrupadores a la vez (además del BackgroundService de la API) sobre la misma cola.
        var processor = new MatchmakingProcessor(
            queue,
            factory.Services.GetRequiredService<IMatchLauncher>(),
            new MatchmakingPolicy(TimeSpan.Zero, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1)),
            factory.Services.GetRequiredService<LobbyPolicy>(),
            factory.Services.GetRequiredService<IClock>(),
            NullLogger<MatchmakingProcessor>.Instance);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await processor.RunOnceAsync(default);
            }
        })));

        var groups = factory.Launcher.Requests
            .Where(r => r.Seats.Any(s => ids.Contains(s.UserId)))
            .GroupBy(r => r.RoomId)
            .Select(g => g.First().Seats.Select(s => s.UserId).ToList())
            .ToList();
        var assigned = groups.SelectMany(g => g).ToList();
        Assert.Equal(assigned.Count, assigned.Distinct().Count());
        Assert.All(groups, g => Assert.InRange(g.Count, 2, 4));
        Assert.True(assigned.Count >= ids.Count - 1, $"Solo {assigned.Count} de {ids.Count} jugadores fueron emparejados.");

        var registry = factory.Services.GetRequiredService<IActiveMatchRegistry>();
        foreach (var user in users.Where(u => assigned.Contains(u.Id)))
        {
            Assert.NotNull(await registry.GetAsync(user.Id, default));
        }

        // Un posible jugador sobrante no debe quedar en la cola para las demás pruebas.
        foreach (var user in users)
        {
            (await SendAsync(HttpMethod.Delete, "/api/v1/matchmaking/queue", user)).Dispose();
        }
    }

    [Fact]
    public async Task Lobby_endpoints_require_authentication_and_validate_input()
    {
        using (var anonymous = await _client.GetAsync(new Uri("/api/v1/rooms", UriKind.Relative)))
        {
            await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "unauthorized");
        }

        var user = await NewUserAsync();
        using (var badName = await SendAsync(HttpMethod.Post, "/api/v1/rooms", user, new { name = "<script>", maxPlayers = 9 }))
        {
            var errors = (await AssertProblemAsync(badName, HttpStatusCode.BadRequest, "validation")).GetProperty("errors");
            Assert.True(errors.TryGetProperty("name", out _));
            Assert.True(errors.TryGetProperty("maxPlayers", out _));
        }

        using (var badCode = await SendAsync(HttpMethod.Post, "/api/v1/rooms/join", user, new { code = "no" }))
        {
            await AssertProblemAsync(badCode, HttpStatusCode.BadRequest, "validation");
        }

        using (var badPage = await SendAsync(HttpMethod.Get, "/api/v1/rooms?pageSize=500", user))
        {
            await AssertProblemAsync(badPage, HttpStatusCode.BadRequest, "validation");
        }

        // "ANVIL" era el nombre anterior a ADR-0008: el contrato ya no lo acepta.
        foreach (var hero in new[] { "DRAGON", "ANVIL" })
        {
            using var unknownHero = await SendAsync(HttpMethod.Post, "/api/v1/matchmaking/queue", user, new { preferredHero = hero });
            await AssertProblemAsync(unknownHero, HttpStatusCode.BadRequest, "validation");
        }
    }

    [Fact]
    public async Task OpenApi_documents_lobby_endpoints_with_hero_enum()
    {
        var document = await _client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Contains("/api/v1/rooms/{roomId}/start", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/matchmaking/queue", document, StringComparison.Ordinal);
        Assert.Contains("\"ERRATA\"", document, StringComparison.Ordinal);
    }

    private async Task<TestUser> NewUserAsync(HttpClient? client = null)
    {
        client ??= _client;
        var username = $"l_{Guid.NewGuid():N}"[..20];
        using (var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email = $"{username}@example.com", password = Password }))
        {
            register.EnsureSuccessStatusCode();
        }

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password = Password });
        var token = (await login.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        using var me = await SendAsync(HttpMethod.Get, UserEndpoints.MePath, new TestUser(Guid.Empty, token, client));
        return new TestUser((await ReadAsync(me)).GetProperty("id").GetGuid(), token, client);
    }

    private async Task<JsonElement> CreateRoomAsync(TestUser host, string visibility)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/rooms", host, new { name = "Sala de prueba", visibility });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private async Task LeaveAsync(TestUser user, JsonElement room)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/v1/rooms/{room.GetProperty("id").GetString()}/leave", user);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task PutOkAsync(TestUser user, string path, object body)
    {
        using var response = await SendAsync(HttpMethod.Put, path, user, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<Guid> WaitForMatchAsync(TestUser user)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            using var response = await SendAsync(HttpMethod.Get, "/api/v1/matchmaking/queue", user);
            var status = await ReadAsync(response);
            if (status.GetProperty("status").GetString() == "MATCHED")
            {
                return status.GetProperty("matchId").GetGuid();
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"El usuario {user.Id} no fue emparejado.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, TestUser user, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.Token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return await user.Client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await ReadAsync(response);
        Assert.Equal($"urn:ascua:problem:{code}", problem.GetProperty("type").GetString());
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }

    /// <summary>Usuario con su token; <see cref="Client"/> es la instancia de la API que lo emitió (cada una tiene su clave).</summary>
    private sealed record TestUser(Guid Id, string Token, HttpClient Client);
}
