using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ascua.Api.Realtime;
using Ascua.Application.Matches;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ascua.Integration.Tests.Realtime;

/// <summary>
/// Versiones 3 y 4 por el hub y el REST (contrato 4.0.0): paso y acción, misión, escalera directa, 5 jugadores en una sala
/// de 11, revancha concurrente, maldiciones, títulos y lo que cada uno no debe ver.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class VersionThreeTests(AscuaApiFactory factory)
{
    // ───────────── Paso y acción ─────────────

    [Fact]
    public async Task An_order_with_a_step_survives_redis_and_moves_before_acting()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        await using var match = await StartAsync(host, HeroClass.Mampostero, HeroClass.Errata);
        var (a, b) = (match.Clients[0], match.Clients[1]);
        await InjectAsync(host, match.Id, s => s with
        {
            Rooms = s.Rooms.SetItem(0, s.Rooms[0] with { Features = [], Chests = [] }),
            Enemies = [Enemy.Create(900, EnemyKind.Totem, new Position(5, 3), 9)],
            Heroes = s.Heroes.SetItem(0, s.Heroes[0] with { Position = new Position(4, 4), Blessings = [] }).SetItem(1, s.Heroes[1] with { Position = new Position(1, 8) }),
        });

        var invalid = await a.SubmitStepAsync(match.Id, 1, "GUARD", null, "E");
        Assert.Equal("INVALID_PAYLOAD", invalid.Error?.Code);
        Assert.Equal("order.step", invalid.Error!.Details!["field"]);
        var badStep = await a.SubmitStepAsync(match.Id, 1, "ATTACK", "N", "X");
        Assert.Equal("INVALID_PAYLOAD", badStep.Error?.Code);

        Assert.Equal("ACCEPTED", (await a.SubmitStepAsync(match.Id, 1, "ATTACK", "N", "E")).Result);
        var pending = (await a.GetSnapshotAsync(match.Id)).View!.Me.PendingOrder!;
        Assert.Equal("E", pending["step"]);
        Assert.Equal("ACCEPTED", (await b.SubmitAsync(match.Id, 1, "GUARD")).Result);

        var resolved = await a.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Equal(5, resolved.Delta.Me!.Position.X);
        Assert.Equal(4, resolved.Delta.Me.Position.Y);
        var moved = resolved.Delta.Events.First(e => (string)e["type"] == "MOVED");
        var attacked = resolved.Delta.Events.First(e => (string)e["type"] == "ATTACKED");
        Assert.Equal("MOVEMENT", moved["phase"]);
        Assert.Equal("ACTIONS", attacked["phase"]);
        Assert.Contains(resolved.Delta.Events, e => (string)e["type"] == "DAMAGED");
    }

    // ───────────── Misión ─────────────

    [Fact]
    public async Task Each_player_gets_two_private_missions_and_the_match_starts_when_all_chose()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        var a = await RealtimeSetup.RegisterAsync(host);
        var b = await RealtimeSetup.RegisterAsync(host);
        var matchId = await RealtimeSetup.LaunchAsync(host, a, b);
        var (ca, _) = await RealtimeSetup.JoinAsync(host, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(host, b, matchId);
        await using var _1 = ca;
        await using var _2 = cb;

        var offerA = await ca.WaitForAsync<PactOfferMessage>("PactOffer");
        var offerB = await cb.WaitForAsync<PactOfferMessage>("PactOffer");
        Assert.Equal(2, offerA.Options.Count);
        Assert.NotEqual(offerA.OfferId, offerB.OfferId);
        Assert.Null((await ca.GetSnapshotAsync(matchId)).View!.Me.Pact);

        // No se puede elegir una misión ajena ni una oferta ajena.
        Assert.Equal("PACT_CLOSED", (await ca.ChoosePactAsync(matchId, offerB.OfferId, offerA.Options[0].PactId)).Error?.Code);
        Assert.Equal("INVALID_PAYLOAD", (await ca.ChoosePactAsync(matchId, offerA.OfferId, "NO_EXISTE")).Error?.Code);

        var chosen = offerA.Options[1];
        Assert.Equal("ACCEPTED", (await ca.ChoosePactAsync(matchId, offerA.OfferId, chosen.PactId)).Result);
        Assert.Equal("DUPLICATE_ACTION", (await ca.ChoosePactAsync(matchId, offerA.OfferId, chosen.PactId)).Result);
        var other = offerA.Options[0].PactId;
        Assert.Equal("PACT_CLOSED", (await ca.ChoosePactAsync(matchId, offerA.OfferId, other)).Error?.Code);
        Assert.Equal(chosen.PactId, (await ca.GetSnapshotAsync(matchId)).View!.Me.Pact!.PactId);

        // Lo de A no llega a B.
        Assert.DoesNotContain(offerA.OfferId, cb.Dump(), StringComparison.Ordinal);
        if (chosen.TargetId is { } target)
        {
            Assert.Equal(target, (await ca.GetSnapshotAsync(matchId)).View!.Me.Pact!.TargetId);
        }

        foreach (var client in new[] { ca, cb })
        {
            var offer = await client.WaitForAsync<DraftOfferMessage>("DraftOffer");
            Assert.Null((await client.DraftPickAsync(matchId, offer.OfferId, offer.Options[0])).Error);
        }

        Assert.Equal("ACCEPTED", (await cb.ChoosePactAsync(matchId, offerB.OfferId, offerB.Options[0].PactId)).Result);
        await ca.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
    }

    // ───────────── Versión 4: escalera directa, sin sabotaje, sala de 11 ─────────────

    [Fact]
    public async Task The_stairs_lead_straight_to_the_next_room_and_announce_its_type()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        await using var match = await StartAsync(host, HeroClass.Mampostero, HeroClass.Errata, HeroClass.Avaro);
        await InjectAsync(host, match.Id, s => s with
        {
            Enemies = [],
            Rooms = s.Rooms.SetItem(1, s.Rooms[1] with { Type = RoomType.Flooded }),
            Heroes = s.Heroes.SetItem(0, s.Heroes[0] with { Position = MatchFactory.StairsPosition }),
        });
        foreach (var client in match.Clients)
        {
            Assert.Equal("ACCEPTED", (await client.SubmitAsync(match.Id, 1, "GUARD")).Result);
        }

        foreach (var client in match.Clients)
        {
            var resolved = await client.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
            Assert.Equal("IN_PROGRESS", resolved.Delta.Status);
            Assert.Equal(2, resolved.Delta.FloorChanged);
            Assert.Contains(resolved.Delta.Events, e => (string)e["type"] == "FLOOR_ENTERED" && (string)e["detail"] == "FLOODED");
            await client.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 2);
        }

        var view = (await match.Clients[1].GetSnapshotAsync(match.Id)).View!;
        Assert.Equal("FLOODED", view.Room.Type);
        Assert.Equal(9, view.Room.Size);
        Assert.Equal(GameState.ChillStartRound, view.ChillRound);
    }

    [Fact]
    public async Task Sabotage_and_votes_no_longer_exist()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        await using var match = await StartAsync(host, HeroClass.Mampostero, HeroClass.Errata);
        var a = match.Clients[0];

        Assert.Equal("INVALID_PAYLOAD", (await a.SubmitAsync(match.Id, 1, "SABOTAGE", "N")).Error?.Code);
        await Assert.ThrowsAnyAsync<Exception>(() => Microsoft.AspNetCore.SignalR.Client.HubConnectionExtensions.InvokeAsync<object>(a.Connection, "CastInterludeVote", new { matchId = match.Id }));
        Assert.DoesNotContain("canSabotage", a.Dump(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Five_players_play_in_an_11_room()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        await using var match = await StartAsync(host, HeroClass.Mampostero, HeroClass.Errata, HeroClass.Avaro, HeroClass.Hilvan, HeroClass.Escucha);

        var view = (await match.Clients[4].GetSnapshotAsync(match.Id)).View!;
        Assert.Equal(11, view.Room.Size);
        Assert.Equal(4, view.Allies.Count);
        Assert.Equal(121, view.Tiles.Count);
        Assert.Equal(9, view.Me.Position.Y);

        foreach (var client in match.Clients)
        {
            Assert.Equal("ACCEPTED", (await client.SubmitAsync(match.Id, 1, "GUARD")).Result);
        }

        var resolved = await match.Clients[0].WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Equal(1, resolved.Round);
    }

    // ───────────── Maldiciones, títulos y catálogo ─────────────

    [Fact]
    public async Task Curses_are_kept_with_the_match_and_haste_shortens_the_rounds()
    {
        var a = await RealtimeSetup.RegisterAsync(factory);
        var b = await RealtimeSetup.RegisterAsync(factory);
        var matchId = await factory.Services.GetRequiredService<IMatchLauncher>().LaunchAsync(
            new LaunchMatchRequest(Guid.NewGuid(), [new MatchSeat(a.Id, HeroClass.Errata), new MatchSeat(b.Id, HeroClass.Avaro)]) { Curses = [Curse.Haste, Curse.Dim] },
            CancellationToken.None);
        var store = factory.Services.GetRequiredService<IMatchStore>();
        var snapshot = await store.LoadAsync(matchId, CancellationToken.None);

        Assert.Equal(CurseRules.HasteRoundDuration, snapshot!.RoundDuration);
        Assert.Equal(2, snapshot.State.Curses.Count);

        var (client, join) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        await using var _ = client;
        Assert.Contains("HASTE", join.Snapshot!.View.Curses);
        Assert.Contains("DIM", join.Snapshot.View.Curses);
    }

    [Fact]
    public async Task A_room_keeps_the_curses_the_host_chose_and_rejects_unknown_ones()
    {
        var user = await RealtimeSetup.RegisterAsync(factory);
        using var http = Client(factory, user);

        using var bad = await http.PostAsJsonAsync("/api/v1/rooms", new { name = "Sala maldita", curses = new[] { "NO_EXISTE" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var ok = await http.PostAsJsonAsync("/api/v1/rooms", new { name = "Sala maldita", curses = new[] { "FIERCE", "HASTE" } });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var room = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["FIERCE", "HASTE"], room.GetProperty("curses").EnumerateArray().Select(c => c.GetString()));

        using var leave = await http.PostAsync(new Uri($"/api/v1/rooms/{room.GetProperty("id").GetGuid()}/leave", UriKind.Relative), null);
        leave.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_catalog_shows_effect_and_cost_of_all_twelve()
    {
        var user = await RealtimeSetup.RegisterAsync(factory);
        using var http = Client(factory, user);

        var catalog = await http.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/catalog", UriKind.Relative));

        var blessings = catalog.GetProperty("blessings").EnumerateArray().ToList();
        Assert.Equal(12, blessings.Count);
        Assert.All(blessings, b => Assert.False(string.IsNullOrWhiteSpace(b.GetProperty("cost").GetString())));
        Assert.Equal(3, catalog.GetProperty("synergies").GetArrayLength());
        Assert.Equal(5, catalog.GetProperty("roomTypes").GetArrayLength());
        Assert.Equal(4, catalog.GetProperty("curses").GetArrayLength());
    }

    [Fact]
    public async Task The_final_message_carries_awards_and_a_collective_defeat_is_a_defeat_in_the_history()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        await using var match = await StartAsync(host, HeroClass.Mampostero, HeroClass.Errata);
        var (a, b) = (match.Clients[0], match.Clients[1]);
        await InjectAsync(host, match.Id, s => s with
        {
            Enemies = [Enemy.Create(900, EnemyKind.Scavenger, new Position(0, 0), 1) with { Telegraph = [new Position(4, 4), new Position(4, 5)] }],
            Heroes = s.Heroes
                .SetItem(0, s.Heroes[0] with { Position = new Position(4, 4), Hp = 1, Progress = new PactProgress { ChestsOpened = 2 } })
                .SetItem(1, s.Heroes[1] with { Position = new Position(4, 5), Hp = 1 }),
        });
        Assert.Equal("ACCEPTED", (await a.SubmitAsync(match.Id, 1, "INTERACT", "N")).Result);
        Assert.Equal("ACCEPTED", (await b.SubmitAsync(match.Id, 1, "INTERACT", "N")).Result);

        var finished = await a.WaitForAsync<MatchFinishedMessage>("MatchFinished");
        Assert.Equal("EMBER_EXTINGUISHED", finished.Outcome);
        Assert.Empty(finished.Winners);
        var award = Assert.Single(finished.Awards, x => x.AwardId == "MOST_CHESTS");
        Assert.Equal(a.Player.Id.ToString("D"), award.PlayerId);
        Assert.Contains("2", award.Reason, StringComparison.Ordinal);

        using var http = Client(host, a.Player);
        var history = await http.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/matches/history?pageSize=1", UriKind.Relative));
        var item = history.GetProperty("items")[0];
        Assert.Equal("DEFEAT", item.GetProperty("result").GetString());
    }

    // ───────────── Revancha ─────────────

    [Fact]
    public async Task A_rematch_requested_at_once_by_everyone_makes_a_single_room_with_one_code()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        var players = new List<TestPlayer>();
        for (var i = 0; i < 4; i++)
        {
            players.Add(await RealtimeSetup.RegisterAsync(host));
        }

        var engine = host.Services.GetRequiredService<MatchEngine>();
        var matchId = await host.Services.GetRequiredService<IMatchLauncher>().LaunchAsync(
            new LaunchMatchRequest(Guid.NewGuid(), [.. players.Select((p, i) => new MatchSeat(p.Id, (HeroClass)i))]) { Curses = [Curse.Fierce] },
            CancellationToken.None);

        using var early = Client(host, players[0]);
        using var tooSoon = await early.PostAsync(new Uri($"/api/v1/matches/{matchId}/rematch", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);

        foreach (var player in players)
        {
            await engine.RetireAsync(matchId, player.Id, CancellationToken.None);
        }

        var stranger = await RealtimeSetup.RegisterAsync(host);
        using var strangerHttp = Client(host, stranger);
        using var forbidden = await strangerHttp.PostAsync(new Uri($"/api/v1/matches/{matchId}/rematch", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);

        using (var before = await early.GetAsync(new Uri($"/api/v1/matches/{matchId}/rematch", UriKind.Relative)))
        {
            Assert.False((await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("available").GetBoolean());
        }

        using (var notYours = await strangerHttp.GetAsync(new Uri($"/api/v1/matches/{matchId}/rematch", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.NotFound, notYours.StatusCode);
        }

        // Cada uno la pide dos veces y todos a la vez: concurrencia real contra Redis.
        var clients = players.Select(p => Client(host, p)).ToList();
        var requests = clients.SelectMany(c => new[] { c, c }).Select(c => c.PostAsync(new Uri($"/api/v1/matches/{matchId}/rematch", UriKind.Relative), null)).ToList();
        var responses = await Task.WhenAll(requests);

        var rooms = new List<JsonElement>();
        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            rooms.Add(await response.Content.ReadFromJsonAsync<JsonElement>());
            response.Dispose();
        }

        Assert.Single(rooms.Select(r => r.GetProperty("id").GetGuid()).Distinct());
        var codes = rooms.Select(r => r.GetProperty("inviteCode").GetString()).Distinct().ToList();
        Assert.Single(codes);
        Assert.False(string.IsNullOrEmpty(codes[0]));

        var final = await clients[0].GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/rooms/{rooms[0].GetProperty("id").GetGuid()}", UriKind.Relative));
        Assert.Equal(4, final.GetProperty("members").GetArrayLength());
        Assert.Equal(4, final.GetProperty("capacity").GetInt32());
        Assert.Equal(["FIERCE"], final.GetProperty("curses").EnumerateArray().Select(c => c.GetString()));
        var status = await clients[0].GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/matches/{matchId}/rematch", UriKind.Relative));
        Assert.True(status.GetProperty("available").GetBoolean());
        Assert.Equal(4, status.GetProperty("players").GetInt32());
        Assert.True(status.GetProperty("joined").GetBoolean());

        foreach (var client in clients)
        {
            client.Dispose();
        }
    }

    // ───────────── Ayudas ─────────────

    private static HttpClient Client(WebApplicationFactory<Program> host, TestPlayer player)
    {
        var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.AccessToken);
        return http;
    }

    private sealed record StartedMatch(Guid Id, List<RealtimeTestClient> Clients) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var client in Clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    private static async Task<StartedMatch> StartAsync(WebApplicationFactory<Program> host, params HeroClass[] heroes)
    {
        var players = new List<TestPlayer>();
        foreach (var _ in heroes)
        {
            players.Add(await RealtimeSetup.RegisterAsync(host));
        }

        var matchId = await host.Services.GetRequiredService<IMatchLauncher>().LaunchAsync(
            new LaunchMatchRequest(Guid.NewGuid(), [.. players.Select((p, i) => new MatchSeat(p.Id, heroes[i]))]),
            CancellationToken.None);
        var clients = new List<RealtimeTestClient>();
        foreach (var player in players)
        {
            var (client, _) = await RealtimeSetup.JoinAsync(host, player, matchId);
            clients.Add(client);
        }

        foreach (var client in clients)
        {
            await client.PickFirstAsync(matchId);
        }

        foreach (var client in clients)
        {
            await client.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
        }

        return new StartedMatch(matchId, clients);
    }

    private static async Task InjectAsync(WebApplicationFactory<Program> host, Guid matchId, Func<GameState, GameState> change)
    {
        var hot = host.Services.GetRequiredService<IMatchHotState>();
        var store = host.Services.GetRequiredService<IMatchStore>();
        await using var lease = await hot.TryLockAsync(matchId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(lease);
        var current = await hot.GetAsync(matchId, CancellationToken.None) ?? (await store.LoadAsync(matchId, CancellationToken.None))!;
        var next = current with { State = change(current.State), Version = current.Version + 1 };
        Assert.True(await store.CommitAsync(current, next, [], CancellationToken.None));
        await hot.SaveAsync(next, CancellationToken.None);
    }
}
