using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ascua.Api.Endpoints;
using Ascua.Api.Realtime;
using Ascua.Application.Audit;
using Ascua.Application.Matches;
using Ascua.Application.Users;
using Ascua.Domain.Model;
using Ascua.Domain.Views;
using Ascua.Infrastructure.Matches;
using Ascua.Infrastructure.Persistence;
using Ascua.Integration.Tests.Realtime;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ascua.Integration.Tests;

/// <summary>Historial, detalle y reproducción contra Postgres y Redis reales.</summary>
[Collection(ApiCollection.Name)]
public sealed class MatchHistoryTests(AscuaApiFactory factory)
{
    private const string Password = "correcto-caballo-bateria";

    [Fact]
    public async Task History_is_paginated_newest_first_and_only_lists_own_matches()
    {
        var me = await RealtimeSetup.RegisterAsync(factory);
        var launched = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            launched.Add(await RealtimeSetup.LaunchAsync(factory, me, await RealtimeSetup.RegisterAsync(factory)));
        }

        // Una partida ajena no aparece.
        await RealtimeSetup.LaunchAsync(factory, await RealtimeSetup.RegisterAsync(factory), await RealtimeSetup.RegisterAsync(factory));

        using var http = Client(me);
        var first = await GetAsync<MatchHistoryResponse>(http, "/api/v1/matches/history?page=1&pageSize=2");
        var second = await GetAsync<MatchHistoryResponse>(http, "/api/v1/matches/history?page=2&pageSize=2");

        Assert.Equal(3, first.Total);
        Assert.Equal(2, first.Items.Count);
        Assert.Single(second.Items);
        launched.Reverse();
        Assert.Equal(launched, [.. first.Items.Concat(second.Items).Select(i => i.MatchId)]);
        Assert.All(first.Items, i =>
        {
            Assert.Equal("DRAFTING", i.Status);
            Assert.Null(i.Outcome);
            Assert.Null(i.Result);
            Assert.Equal("ERRATA", i.Hero);
            Assert.Equal(2, i.PlayerCount);
        });

        using var invalid = await http.GetAsync(new Uri("/api/v1/matches/history?pageSize=0", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("validation", (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        using var anonymous = factory.CreateClient();
        using var unauthorized = await anonymous.GetAsync(new Uri("/api/v1/matches/history", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task Detail_and_replay_of_someone_elses_match_answer_like_a_missing_one()
    {
        var a = await RealtimeSetup.RegisterAsync(factory);
        var b = await RealtimeSetup.RegisterAsync(factory);
        var outsider = await RealtimeSetup.RegisterAsync(factory);
        var matchId = await RealtimeSetup.LaunchAsync(factory, a, b);

        using var http = Client(outsider);
        foreach (var path in new[]
        {
            $"/api/v1/matches/{matchId}",
            $"/api/v1/matches/{matchId}/replay",
            $"/api/v1/matches/{Guid.NewGuid()}",
            $"/api/v1/matches/{Guid.NewGuid()}/replay",
        })
        {
            using var response = await http.GetAsync(new Uri(path, UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(Problems.ContentType, response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("not-found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // El participante sí ve su partida en curso, sin pactos ajenos.
        using var own = Client(a);
        var detail = await GetAsync<MatchDetailResponse>(own, $"/api/v1/matches/{matchId}");
        Assert.Equal("DRAFTING", detail.Status);
        Assert.All(detail.Players, p => Assert.Null(p.Pact));
        Assert.False(string.IsNullOrEmpty(detail.MyPact.PactId));
    }

    [Fact]
    public async Task Finished_match_replays_deterministically_with_the_requesters_view_only()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:MaxRounds"] = "3" });
        var (a, b, matchId) = await PlayFullMatchAsync(host);

        using var http = Client(a, host);
        var path = $"/api/v1/matches/{matchId}/replay";
        var raw1 = await http.GetStringAsync(new Uri(path, UriKind.Relative));
        var raw2 = await http.GetStringAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(raw1, raw2);

        var replay = JsonSerializer.Deserialize<MatchReplayResponse>(raw1, JsonSerializerOptions.Web)!;
        Assert.Equal("PLAYER", replay.ViewMode);
        Assert.Equal(a.Id.ToString("D"), replay.ViewerId);
        Assert.Equal("FINISHED", replay.Status);
        Assert.Equal(0, replay.Rounds[0].Round);
        Assert.Equal([.. Enumerable.Range(0, replay.Rounds.Count)], replay.Rounds.Select(r => r.Round));
        Assert.All(replay.Rounds, r => Assert.Null(r.Full));

        // La última ronda termina en el último seq confirmado.
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var match = await db.Matches.AsNoTracking().SingleAsync(m => m.Id == matchId);
        Assert.Equal(match.LastSeq, replay.Rounds[^1].Seq);

        // Cada ronda coincide con la proyección de la verdad guardada (el replay no agrega nada que A no viera).
        var states = await db.MatchStates.AsNoTracking().Where(s => s.MatchId == matchId).OrderBy(s => s.Version).ToListAsync();
        var playerA = PlayerIds.Of(a.Id);
        var playerB = PlayerIds.Of(b.Id);
        foreach (var round in replay.Rounds)
        {
            var truth = MatchJson.DeserializeState(states.Last(s => s.Round == round.Round).State);
            var expected = PlayerViewProjector.For(truth, playerA);
            var view = round.View!;
            Assert.Equal(expected.Enemies.Select(e => e.Id).Order(), view.Enemies.Select(e => e.EnemyId).Order());
            Assert.Equal(expected.Tiles.Count, view.Tiles.Count);
            Assert.True(view.Tiles.Count < Room.Size * Room.Size, "La niebla oculta casillas.");
            Assert.Equal(truth.FindHero(playerA)!.Blessings.Distinct().Select(RealtimeMapper.BlessingId), view.Me.Blessings);
            Assert.Equal(RealtimeMapper.PactId(truth.FindHero(playerA)!.Pact), view.Me.Pact?.PactId);
        }

        // Nada privado de B: ni su pacto ni sus bendiciones ni su draft, en ninguna ronda.
        using var document = JsonDocument.Parse(raw1);
        foreach (var round in document.RootElement.GetProperty("rounds").EnumerateArray())
        {
            foreach (var ally in round.GetProperty("view").GetProperty("allies").EnumerateArray())
            {
                Assert.False(ally.TryGetProperty("pact", out _));
                Assert.False(ally.TryGetProperty("blessings", out _));
            }

            foreach (var e in round.GetProperty("events").EnumerateArray())
            {
                var actor = e.TryGetProperty("actorId", out var id) ? id.GetString() : null;
                Assert.False(e.GetProperty("type").GetString() == "BLESSING_GAINED" && actor == playerB.Value);
                Assert.False(e.TryGetProperty("seq", out _));
            }
        }

        var bTruth = MatchJson.DeserializeState(states[^1].State).FindHero(playerB)!;
        var aTruth = MatchJson.DeserializeState(states[^1].State).FindHero(playerA)!;
        if (bTruth.Pact != aTruth.Pact)
        {
            Assert.DoesNotContain(RealtimeMapper.PactId(bTruth.Pact), raw1, StringComparison.Ordinal);
        }

        foreach (var secret in bTruth.Blessings.Except(aTruth.Blessings))
        {
            Assert.DoesNotContain(RealtimeMapper.BlessingId(secret), raw1, StringComparison.Ordinal);
        }

        // Rango de rondas.
        var only1 = await GetAsync<MatchReplayResponse>(http, $"{path}?fromRound=1&toRound=1");
        Assert.Equal(1, Assert.Single(only1.Rounds).Round);
        using var bad = await http.GetAsync(new Uri($"{path}?fromRound=2&toRound=1", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // Al terminar, el detalle revela los pactos de todos y el historial muestra el resultado.
        var detail = await GetAsync<MatchDetailResponse>(http, $"/api/v1/matches/{matchId}");
        Assert.Equal("FINISHED", detail.Status);
        Assert.NotNull(detail.Outcome);
        Assert.NotNull(detail.FinishedAt);
        Assert.Equal(2, detail.Players.Count);
        Assert.All(detail.Players, p => Assert.NotNull(p.Pact));
        Assert.EndsWith($"/api/v1/matches/{matchId}/replay", detail.ReplayUrl, StringComparison.Ordinal);

        var history = await GetAsync<MatchHistoryResponse>(http, "/api/v1/matches/history");
        var item = Assert.Single(history.Items, i => i.MatchId == matchId);
        Assert.Equal("FINISHED", item.Status);
        Assert.NotNull(item.Result);
        Assert.NotNull(item.Placement);
        Assert.NotNull(item.Embers);
        Assert.Equal(detail.Rounds, item.Rounds);
    }

    [Fact]
    public async Task Admin_can_request_the_full_view_and_a_player_cannot()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:MaxRounds"] = "2" });
        var (a, b, matchId) = await PlayFullMatchAsync(host);
        var admin = await RegisterAdminAsync(host);
        var path = $"/api/v1/matches/{matchId}/replay";

        using var player = Client(a, host);
        using var forbidden = await player.GetAsync(new Uri($"{path}?as=full", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("forbidden", (await forbidden.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // El 403 se decide antes de buscar: tampoco revela si una partida inexistente existe.
        using var missing = await player.GetAsync(new Uri($"/api/v1/matches/{Guid.NewGuid()}/replay?as=full", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);

        using var adminHttp = Client(admin, host);
        var full = await GetAsync<MatchReplayResponse>(adminHttp, $"{path}?as=full");
        Assert.Equal("FULL", full.ViewMode);
        Assert.Null(full.ViewerId);
        Assert.All(full.Rounds, r => Assert.Null(r.View));
        var last = full.Rounds[^1].Full!;
        Assert.Equal(2, last.Heroes.Count);
        Assert.Contains(last.Heroes, h => h.PlayerId == b.Id.ToString("D") && !string.IsNullOrEmpty(h.Pact));
        Assert.All(full.Rounds.SelectMany(r => r.Events), e => Assert.True(e.ContainsKey("seq")));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
            var entry = await db.AuditLog.SingleAsync(e =>
                e.Action == AuditActions.MatchFullReplayViewed && e.TargetId == matchId.ToString("D"));
            Assert.Equal(admin.Id, entry.ActorId);
            Assert.Equal(AuditTargets.Match, entry.TargetType);
        }

        // Sin as=full, el admin no tiene vista de jugador en una partida ajena.
        using var notMine = await adminHttp.GetAsync(new Uri(path, UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, notMine.StatusCode);

        using var badMode = await adminHttp.GetAsync(new Uri($"{path}?as=everything", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, badMode.StatusCode);
    }

    [Fact]
    public async Task Finished_match_rows_cannot_be_rewritten()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:MaxRounds"] = "1" });
        var (_, _, matchId) = await PlayFullMatchAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Matches.Where(m => m.Id == matchId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, "IN_PROGRESS")));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Matches.Where(m => m.Id == matchId).ExecuteUpdateAsync(s => s.SetProperty(m => m.State, "{}")));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.MatchPlayers.Where(p => p.MatchId == matchId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Seat, 9)));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.MatchStates.Where(s => s.MatchId == matchId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "{}")));
        await Assert.ThrowsAnyAsync<Exception>(() => db.MatchStates.Where(s => s.MatchId == matchId).ExecuteDeleteAsync());
        Assert.Equal("FINISHED", (await db.Matches.AsNoTracking().SingleAsync(m => m.Id == matchId)).Status);
    }

    [Fact]
    public async Task OpenApi_documents_the_match_endpoints_with_examples()
    {
        using var http = factory.CreateClient();
        using var document = JsonDocument.Parse(await http.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)));
        var paths = document.RootElement.GetProperty("paths");
        var replay = paths.GetProperty("/api/v1/matches/{matchId}/replay").GetProperty("get");
        Assert.Equal("GetMatchReplay", replay.GetProperty("operationId").GetString());
        Assert.True(replay.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").TryGetProperty("example", out _));
        Assert.True(replay.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(paths.TryGetProperty("/api/v1/matches/history", out _));
        Assert.True(paths.TryGetProperty("/api/v1/matches/{matchId}", out _));
    }

    private static async Task<(TestPlayer A, TestPlayer B, Guid MatchId)> PlayFullMatchAsync(WebApplicationFactory<Program> host)
    {
        var a = await RealtimeSetup.RegisterAsync(host);
        var b = await RealtimeSetup.RegisterAsync(host);
        var matchId = await RealtimeSetup.LaunchAsync(host, a, b);
        var (ca, _) = await RealtimeSetup.JoinAsync(host, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(host, b, matchId);
        await using var _ = ca;
        await using var __ = cb;

        await ca.PickFirstAsync(matchId);
        await cb.PickFirstAsync(matchId);
        await ca.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
        for (var round = 1; round == 1 || !await FinishedBeforeRoundAsync(ca, round); round++)
        {
            foreach (var (client, direction) in new[] { (ca, "N"), (cb, "E") })
            {
                var reply = await client.SubmitAsync(matchId, round, "MOVE", direction);
                Assert.True(reply.Result == "ACCEPTED" || reply.Error?.Code == "PLAYER_DOWN", reply.Error?.Code);
            }

            await ca.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == round);
        }

        await ca.WaitForAsync<MatchFinishedMessage>("MatchFinished");
        return (a, b, matchId);
    }

    /// <summary>Tras resolver una ronda llega la apertura de la siguiente o el fin de la partida.</summary>
    private static async Task<bool> FinishedBeforeRoundAsync(RealtimeTestClient client, int round)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (client.All<MatchFinishedMessage>("MatchFinished").Any())
            {
                return true;
            }

            if (client.All<RoundOpenedMessage>("RoundOpened").Any(m => m.Round == round))
            {
                return false;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"No llegó RoundOpened({round}) ni MatchFinished.");
    }

    private static async Task<TestPlayer> RegisterAdminAsync(WebApplicationFactory<Program> host)
    {
        var user = await RealtimeSetup.RegisterAsync(host);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Admin));
        }

        // El rol viaja en el JWT: hay que volver a iniciar sesión.
        using var http = host.CreateClient();
        using var login = await http.PostAsJsonAsync(new Uri("/api/v1/auth/login", UriKind.Relative), new { username = user.Username, password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        return user with { AccessToken = tokens.AccessToken };
    }

    private HttpClient Client(TestPlayer player, WebApplicationFactory<Program>? host = null)
    {
        var http = (host ?? factory).CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.AccessToken);
        return http;
    }

    private static async Task<T> GetAsync<T>(HttpClient http, string path)
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative));
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
