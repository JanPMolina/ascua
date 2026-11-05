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
/// Versión 3 por el hub y el REST (contrato 3.0.0): paso y acción, misión, interludio con temporizador y reinicio,
/// revancha concurrente, maldiciones, títulos y lo que cada uno no debe ver.
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

    // ───────────── Interludio ─────────────

    private static GameState AtTheStairs(GameState s) => s with
    {
        Enemies = [],
        Heroes = s.Heroes.SetItem(0, s.Heroes[0] with { Position = MatchFactory.StairsPosition }),
    };

    private static async Task ToInterludeAsync(StartedMatch match)
    {
        foreach (var client in match.Clients)
        {
            Assert.Equal("ACCEPTED", (await client.SubmitAsync(match.Id, 1, "INTERACT", "N")).Result);
        }

        foreach (var client in match.Clients)
        {
            await client.WaitForAsync<InterludeOfferMessage>("InterludeOffer");
        }
    }

    [Fact]
    public async Task The_stairs_open_the_trial_votes_are_secret_and_closing_early_shows_the_tally()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Ascua:Realtime:RoundDuration"] = "00:02:00",
            ["Ascua:Realtime:InterludeDuration"] = "00:02:00",
        });
        await using var match = await StartAsync(host, HeroClass.Mampostero, HeroClass.Errata, HeroClass.Avaro);
        var (a, b, c) = (match.Clients[0], match.Clients[1], match.Clients[2]);
        await InjectAsync(host, match.Id, AtTheStairs);
        await ToInterludeAsync(match);

        var offer = a.All<InterludeOfferMessage>("InterludeOffer").Single();
        Assert.True(offer.CanVote);
        Assert.Equal(2, offer.Doors.Count);
        Assert.NotEqual(offer.Doors[0].Type, offer.Doors[1].Type);
        Assert.Equal(2, offer.Candidates.Count);
        Assert.DoesNotContain(a.Player.Id.ToString("D"), offer.Candidates);
        Assert.Equal("INTERLUDE", (await a.GetSnapshotAsync(match.Id)).View!.Status);

        // Las órdenes se rechazan: no hay ronda abierta.
        Assert.NotNull((await a.SubmitAsync(match.Id, 2, "GUARD")).Error);

        // Validación y autorización como los demás mensajes.
        Assert.Equal("INVALID_PAYLOAD", (await a.VoteAsync(match.Id, "no-es-uuid", null, null)).Error?.Code);
        Assert.Equal("INVALID_PAYLOAD", (await a.VoteAsync(match.Id, offer.OfferId, null, 5)).Error?.Code);
        Assert.Equal("VOTE_CLOSED", (await a.VoteAsync(match.Id, Guid.NewGuid().ToString("D"), null, 0)).Error?.Code);
        Assert.Equal("INVALID_VOTE", (await a.VoteAsync(match.Id, offer.OfferId, a.Player.Id.ToString("D"), 0)).Error?.Code);

        var bId = b.Player.Id.ToString("D");
        Assert.Equal("ACCEPTED", (await a.VoteAsync(match.Id, offer.OfferId, bId, 1)).Result);
        Assert.Equal("DUPLICATE_ACTION", (await a.VoteAsync(match.Id, offer.OfferId, bId, 1)).Result);
        Assert.Equal("VOTE_CLOSED", (await a.VoteAsync(match.Id, offer.OfferId, null, 0)).Error?.Code);

        // Nadie más sabe qué votó: solo que votó.
        var seenByC = (await c.GetSnapshotAsync(match.Id)).View!.Interlude!;
        Assert.Null(seenByC.MyVote);
        Assert.Contains(a.Player.Id.ToString("D"), seenByC.Voted);
        Assert.Equal(bId, (await a.GetSnapshotAsync(match.Id)).View!.Interlude!.MyVote!.AccusedId);
        Assert.DoesNotContain("\"accusedId\":\"" + bId, c.Dump(), StringComparison.Ordinal);

        Assert.Equal("ACCEPTED", (await b.VoteAsync(match.Id, offer.OfferId, null, 1)).Result);
        Assert.Equal("ACCEPTED", (await c.VoteAsync(match.Id, offer.OfferId, bId, 0)).Result);

        var resolved = await c.WaitForAsync<InterludeResolvedMessage>("InterludeResolved");
        var accusation = Assert.Single(resolved.Verdict.Accusations);
        Assert.Equal(bId, accusation.AccusedId);
        Assert.Equal(2, accusation.Votes);
        Assert.True(accusation.Convicted);
        Assert.False(accusation.Guilty);
        Assert.Equal(offer.Doors[1].Type, resolved.Verdict.ChosenDoor);
        Assert.Contains(resolved.Events, e => (string)e["type"] == "FLOOR_ENTERED");

        var opened = await a.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 2);
        Assert.Equal(2, opened.Round);
        var view = (await a.GetSnapshotAsync(match.Id)).View!;
        Assert.Equal(2, view.Floor);
        Assert.Equal(offer.Doors[1].Type, view.Room.Type);
        Assert.Equal(offer.Doors[1].Type, view.LastVerdict!.ChosenDoor);
    }

    [Fact]
    public async Task At_the_deadline_absent_votes_are_nobody_and_a_door_is_picked_anyway()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Ascua:Realtime:RoundDuration"] = "00:02:00",
            ["Ascua:Realtime:InterludeDuration"] = "00:00:01",
        });
        await using var match = await StartAsync(host, HeroClass.Mampostero, HeroClass.Errata);
        await InjectAsync(host, match.Id, AtTheStairs);
        await ToInterludeAsync(match);

        var resolved = await match.Clients[1].WaitForAsync<InterludeResolvedMessage>("InterludeResolved", null, TimeSpan.FromSeconds(15));

        Assert.Empty(resolved.Verdict.Accusations);
        Assert.Equal(2, resolved.Verdict.NoAccusation);
        Assert.NotNull(resolved.Verdict.ChosenDoor);
        await match.Clients[0].WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 2);
    }

    [Fact]
    public async Task A_vote_and_the_deadline_survive_restarting_the_instance_in_the_middle()
    {
        // Otra fábrica sobre la misma base: al desecharla, el plazo sigue guardado en Postgres y Redis.
        var settings = new Dictionary<string, string?>
        {
            ["Ascua:Realtime:RoundDuration"] = "00:02:00",
            ["Ascua:Realtime:InterludeDuration"] = "00:00:03",
        };
        var first = factory.WithSettings(settings);
        var match = await StartAsync(first, HeroClass.Mampostero, HeroClass.Errata);
        await InjectAsync(first, match.Id, AtTheStairs);
        await ToInterludeAsync(match);
        var offer = match.Clients[0].All<InterludeOfferMessage>("InterludeOffer").Single();
        Assert.Equal("ACCEPTED", (await match.Clients[0].VoteAsync(match.Id, offer.OfferId, null, 0)).Result);
        var players = match.Clients.Select(c => c.Player).ToList();
        await match.DisposeAsync();
        await first.DisposeAsync();

        await using var second = factory.WithSettings(settings);
        var store = second.Services.GetRequiredService<IMatchStore>();
        var snapshot = await store.LoadAsync(match.Id, CancellationToken.None);
        Assert.Single(snapshot!.State.Interlude?.Ballots ?? [new InterludeBallot(new PlayerId("ya-cerrado"), null, 0)]);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            snapshot = await store.LoadAsync(match.Id, CancellationToken.None);
            if (snapshot!.State.Phase == MatchPhase.InProgress)
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.Equal(MatchPhase.InProgress, snapshot!.State.Phase);
        Assert.Equal(1, snapshot.State.RoomIndex);
        Assert.Equal(offer.Doors[0].Type, RealtimeMapper.RoomTypeCode(snapshot.State.CurrentRoom.Type));
        Assert.True(snapshot.Deadline > DateTimeOffset.UtcNow);

        // Y se retoma: el jugador vuelve con un ticket nuevo a la sala nueva.
        var (back, _) = await RealtimeSetup.JoinAsync(second, await RealtimeSetup.LoginAsync(second, players[0]), match.Id);
        await using var _ = back;
        Assert.Equal(2, (await back.GetSnapshotAsync(match.Id)).View!.Floor);
    }

    [Fact]
    public async Task Before_the_warden_there_are_no_doors()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Ascua:Realtime:RoundDuration"] = "00:02:00",
            ["Ascua:Realtime:InterludeDuration"] = "00:02:00",
        });
        await using var match = await StartAsync(host, HeroClass.Mampostero, HeroClass.Errata);
        await InjectAsync(host, match.Id, s => AtTheStairs(s) with { RoomIndex = s.Rooms.Count - 2 });
        await ToInterludeAsync(match);

        var offer = match.Clients[0].All<InterludeOfferMessage>("InterludeOffer").Single();
        Assert.Empty(offer.Doors);
        foreach (var client in match.Clients)
        {
            Assert.Equal("ACCEPTED", (await client.VoteAsync(match.Id, offer.OfferId, null, null)).Result);
        }

        var resolved = await match.Clients[0].WaitForAsync<InterludeResolvedMessage>("InterludeResolved");
        Assert.Null(resolved.Verdict.ChosenDoor);
        await match.Clients[0].WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 2);
        var view = (await match.Clients[0].GetSnapshotAsync(match.Id)).View!;
        Assert.Equal("NORMAL", view.Room.Type);
    }

    // ───────────── Sabotaje por el hub ─────────────

    [Fact]
    public async Task The_author_of_a_sabotage_never_reaches_the_others()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        await using var match = await StartAsync(host, HeroClass.Mampostero, HeroClass.Errata);
        var (a, b) = (match.Clients[0], match.Clients[1]);
        await InjectAsync(host, match.Id, s => s with
        {
            Rooms = s.Rooms.SetItem(0, s.Rooms[0] with { Features = [], Chests = [] }),
            Enemies = [Enemy.Create(900, EnemyKind.Totem, new Position(8, 0), 9)],
            Heroes = s.Heroes.SetItem(0, s.Heroes[0] with { Position = new Position(4, 5), Blessings = [] }).SetItem(1, s.Heroes[1] with { Position = new Position(4, 4), Blessings = [] }),
        });

        Assert.Equal("ACCEPTED", (await a.SubmitAsync(match.Id, 1, "SABOTAGE", "N")).Result);
        Assert.Equal("ACCEPTED", (await b.SubmitAsync(match.Id, 1, "INTERACT", "N")).Result);

        var seenByB = await b.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        var damaged = Assert.Single(seenByB.Delta.Events, e => (string)e["type"] == "DAMAGED");
        Assert.False(damaged.ContainsKey("actorId"));
        Assert.DoesNotContain(seenByB.Delta.Events, e => (string)e["type"] == "BETRAYAL_RECORDED");
        var seenByA = await a.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Contains(seenByA.Delta.Events, e => (string)e["type"] == "BETRAYAL_RECORDED" && (string)e["detail"] == "SABOTAGE");
        Assert.False(seenByA.Delta.Me!.CanSabotage);
        Assert.Equal("INVALID_ORDER", (await a.SubmitAsync(match.Id, 2, "SABOTAGE", "N")).Error?.Code);
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
