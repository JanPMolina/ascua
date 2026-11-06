using System.Globalization;
using System.Net;
using Ascua.Api.Realtime;
using Ascua.Application.Matches;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ascua.Integration.Tests.Realtime;

/// <summary>Tiempo real de punta a punta: SignalR + MessagePack contra Postgres y Redis reales.</summary>
[Collection(ApiCollection.Name)]
public sealed class RealtimeMatchTests(AscuaApiFactory factory)
{
    [Fact]
    public async Task Two_players_play_a_full_match_with_contiguous_seq_and_immutable_result()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:TestRoundLimit"] = "3" });
        var (a, b, matchId) = await NewMatchAsync(host);
        var (ca, ackA) = await RealtimeSetup.JoinAsync(host, a, matchId);
        var (cb, ackB) = await RealtimeSetup.JoinAsync(host, b, matchId);
        await using var _ = ca;
        await using var __ = cb;

        Assert.Equal(1, ackA.Seat);
        Assert.Equal(2, ackB.Seat);
        Assert.Equal("DRAFTING", ackA.Snapshot!.View.Status);

        await StartRoundsAsync(matchId, ca, cb);
        var seq = (await ca.GetSnapshotAsync(matchId)).Seq;
        for (var round = 1; round <= 3 && !ca.All<MatchFinishedMessage>("MatchFinished").Any(); round++)
        {
            await SubmitOrAcceptDownAsync(ca, matchId, round);
            await SubmitOrAcceptDownAsync(cb, matchId, round);

            var resolved = await ca.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == round);
            Assert.Equal(seq, resolved.PreviousSeq);
            Assert.True(resolved.Seq > resolved.PreviousSeq);
            seq = resolved.Seq;
            await cb.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == round);
        }

        var finished = await ca.WaitForAsync<MatchFinishedMessage>("MatchFinished");
        Assert.Equal(seq, finished.Seq);
        Assert.Equal(2, finished.Standings.Count);
        Assert.All(finished.Standings, s => Assert.NotNull(s.Pact));
        Assert.Contains(finished.Outcome, new[] { "WARDEN_DEFEATED", "DRAW", "EMBER_EXTINGUISHED", "ABANDONED" });

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var seqs = await db.MatchEvents.Where(e => e.MatchId == matchId).OrderBy(e => e.Seq).Select(e => e.Seq).ToListAsync();
        Assert.Equal(Enumerable.Range(1, seqs.Count).Select(i => (long)i), seqs);
        var match = await db.Matches.AsNoTracking().SingleAsync(m => m.Id == matchId);
        Assert.Equal("FINISHED", match.Status);
        Assert.Equal(seq, match.LastSeq);
        Assert.NotNull(match.Result);

        // El resultado es inmutable (trigger) y el log es append-only.
        await Assert.ThrowsAnyAsync<Exception>(() =>
            db.Matches.Where(m => m.Id == matchId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Result, "{}")));
        await Assert.ThrowsAnyAsync<Exception>(() => db.MatchEvents.Where(e => e.MatchId == matchId).ExecuteDeleteAsync());
    }

    [Fact]
    public async Task Resent_order_is_acknowledged_once_and_applied_once()
    {
        var (a, b, matchId) = await NewMatchAsync(factory);
        var (ca, _) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(factory, b, matchId);
        await using var _ = ca;
        await using var __ = cb;
        await StartRoundsAsync(matchId, ca, cb);

        var actionId = Guid.CreateVersion7();
        var first = await ca.SubmitAsync(matchId, 1, "MOVE", "N", actionId);
        var again = await ca.SubmitAsync(matchId, 1, "MOVE", "N", actionId);
        var other = await ca.SubmitAsync(matchId, 1, "GUARD");

        Assert.Equal("ACCEPTED", first.Result);
        Assert.Equal("DUPLICATE_ACTION", again.Result);
        Assert.Equal(first.ReceivedAt, again.ReceivedAt);
        Assert.Equal("ORDER_ALREADY_SUBMITTED", other.Error?.Code);

        Assert.Equal("ACCEPTED", (await cb.SubmitAsync(matchId, 1, "GUARD")).Result);
        await ca.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);

        // Un reenvío tardío (la ronda ya cerró) sigue devolviendo el ACK original, no STALE_ROUND.
        var late = await ca.SubmitAsync(matchId, 1, "MOVE", "N", actionId);
        Assert.Equal("DUPLICATE_ACTION", late.Result);

        Assert.Single(cb.All<PlayerDecidedMessage>("PlayerDecided"), d => d.PlayerId == a.Id.ToString("D") && d.Round == 1);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var moves = await db.MatchEvents
            .Where(e => e.MatchId == matchId && e.Round == 1 && (e.Type == nameof(HeroMoved) || e.Type == nameof(MoveBounced)))
            .Select(e => e.Payload)
            .ToListAsync();
        Assert.Single(moves, payload => payload.Contains(a.Id.ToString("D"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Order_for_another_round_or_with_bad_schema_is_rejected_without_closing_the_match()
    {
        var (a, b, matchId) = await NewMatchAsync(factory);
        var (ca, _) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(factory, b, matchId);
        await using var _ = ca;
        await using var __ = cb;
        await StartRoundsAsync(matchId, ca, cb);

        var stale = await ca.SubmitAsync(matchId, 2, "GUARD");
        Assert.Equal("STALE_ROUND", stale.Error?.Code);
        Assert.Equal(1, Convert.ToInt32(stale.Error!.Details!["currentRound"], CultureInfo.InvariantCulture));

        Assert.Equal("INVALID_PAYLOAD", (await ca.SubmitAsync(matchId, 61, "GUARD")).Error?.Code);
        Assert.Equal("INVALID_PAYLOAD", (await ca.SubmitAsync(matchId, 1, "GUARD", "N")).Error?.Code);
        Assert.Equal("INVALID_PAYLOAD", (await ca.SubmitAsync(matchId, 1, "MOVE", "NE")).Error?.Code);
        Assert.Equal("INVALID_PAYLOAD", (await ca.SubmitAsync(matchId, 1, "TELEPORT", "N")).Error?.Code);

        // La partida sigue para todos: la orden válida se acepta y la ronda se resuelve.
        Assert.Equal("ACCEPTED", (await ca.SubmitAsync(matchId, 1, "GUARD")).Result);
        Assert.Equal("ACCEPTED", (await cb.SubmitAsync(matchId, 1, "GUARD")).Result);
        await cb.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
    }

    [Fact]
    public async Task Two_replicas_closing_the_same_round_produce_a_single_resolution()
    {
        var settings = new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:30:00" };
        var replica1 = factory.WithSettings(settings);
        var replica2 = factory.WithSettings(settings);
        var (a, b, matchId) = await NewMatchAsync(replica1);
        var engine1 = replica1.Services.GetRequiredService<MatchEngine>();
        var engine2 = replica2.Services.GetRequiredService<MatchEngine>();

        foreach (var player in new[] { a, b })
        {
            var snapshot = (await engine1.GetAsync(matchId, CancellationToken.None))!;
            var hero = snapshot.State.FindHero(PlayerIds.Of(player.Id))!;
            var pick = await engine1.DraftPickAsync(matchId, player.Id, MatchEngine.OfferIdFor(matchId, hero), hero.DraftOptions[0], CancellationToken.None);
            Assert.Null(pick.Error);
            var pact = await engine1.ChoosePactAsync(matchId, player.Id, MatchEngine.PactOfferIdFor(matchId, hero.Player), hero.PactOptions[0].Kind, CancellationToken.None);
            Assert.Null(pact.Error);
        }

        // Deja terminar los intentos de cierre en segundo plano del draft antes de cargar las órdenes.
        await Task.Delay(500);
        var hot = replica1.Services.GetRequiredService<IMatchHotState>();
        foreach (var player in new[] { a, b })
        {
            var ack = new OrderAck(matchId, 1, Guid.CreateVersion7(), player.Id, DateTimeOffset.UtcNow);
            await hot.SubmitOrderAsync(ack, PlayerIds.Of(player.Id), Order.DefaultGuard, CancellationToken.None);
        }

        var closed = await Task.WhenAll(
            engine1.TryCloseRoundAsync(matchId, 1, deadlineExpired: false, CancellationToken.None),
            engine2.TryCloseRoundAsync(matchId, 1, deadlineExpired: false, CancellationToken.None));
        Assert.Single(closed, c => c);
        Assert.False(await engine2.TryCloseRoundAsync(matchId, 1, deadlineExpired: false, CancellationToken.None));

        await using var scope = replica2.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var seqs = await db.MatchEvents.Where(e => e.MatchId == matchId).OrderBy(e => e.Seq).Select(e => e.Seq).ToListAsync();
        Assert.Equal(Enumerable.Range(1, seqs.Count).Select(i => (long)i), seqs);
        Assert.Equal(1, await db.MatchEvents.CountAsync(e => e.MatchId == matchId && e.Type == nameof(Domain.Events.RoundResolved)));
        Assert.Equal(2, (await db.Matches.AsNoTracking().SingleAsync(m => m.Id == matchId)).Round);

        // Garantía final sin lock: un commit con un seq ya confirmado no tiene efectos.
        var store = replica2.Services.GetRequiredService<IMatchStore>();
        var current = (await store.LoadAsync(matchId, CancellationToken.None))!;
        var duplicate = new MatchStarted { Seq = current.State.LastSeq, Round = current.State.Round };
        Assert.False(await store.CommitAsync(current, current with { Version = current.Version + 1 }, [duplicate], CancellationToken.None));
        Assert.Equal(current.Version, (await store.LoadAsync(matchId, CancellationToken.None))!.Version);
    }

    [Fact]
    public async Task Timer_closes_the_round_with_guard_when_the_deadline_expires()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Ascua:Realtime:DraftDuration"] = "00:00:01",
            ["Ascua:Realtime:RoundDuration"] = "00:00:01",
        });
        var (a, _, matchId) = await NewMatchAsync(host);
        var (ca, _) = await RealtimeSetup.JoinAsync(host, a, matchId);
        await using var _ = ca;

        // Nadie elige bendición ni envía orden: vence el draft (elige el servidor) y luego la ronda.
        await ca.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
        var resolved = await ca.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);

        Assert.Contains(resolved.Delta.Events, e => (string)e["type"] == "DEFAULT_GUARD_APPLIED");
        Assert.Single(resolved.Delta.Me.Blessings);
    }

    [Fact]
    public async Task Reconnecting_player_recovers_state_and_pending_order()
    {
        var (a, b, matchId) = await NewMatchAsync(factory);
        var (ca, _) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(factory, b, matchId);
        await using var __ = cb;
        await StartRoundsAsync(matchId, ca, cb);
        Assert.Equal("ACCEPTED", (await ca.SubmitAsync(matchId, 1, "MOVE", "N")).Result);

        await ca.DisposeAsync();
        var away = await cb.WaitForAsync<PlayerConnectionChangedMessage>(
            "PlayerConnectionChanged", m => m.PlayerId == a.Id.ToString("D") && m.State == "DISCONNECTED");
        Assert.NotNull(away.GraceDeadline);

        var (again, ack) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        await using var ___ = again;
        var view = ack.Snapshot!.View;
        Assert.Equal("IN_PROGRESS", view.Status);
        Assert.Equal(1, view.Round);
        Assert.True(view.Me.Decided);
        Assert.Equal("MOVE", view.Me.PendingOrder!["type"]);
        Assert.Equal((await cb.GetSnapshotAsync(matchId)).Seq, ack.Snapshot.Seq);
        await cb.WaitForAsync<PlayerConnectionChangedMessage>(
            "PlayerConnectionChanged", m => m.PlayerId == a.Id.ToString("D") && m.State == "CONNECTED");
    }

    [Fact]
    public async Task A_player_view_never_carries_another_player_secrets()
    {
        var (a, b, matchId) = await NewMatchAsync(factory);
        var (ca, _) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        var (cb, ackB) = await RealtimeSetup.JoinAsync(factory, b, matchId);
        await using var _ = ca;
        await using var __ = cb;
        var pactOfferB = await cb.WaitForAsync<PactOfferMessage>("PactOffer");
        var pactB = pactOfferB.OfferId;
        var offerB = (await cb.WaitForAsync<DraftOfferMessage>("DraftOffer")).OfferId;
        await StartRoundsAsync(matchId, ca, cb);

        Assert.Equal("ACCEPTED", (await cb.SubmitAsync(matchId, 1, "INTERACT", "W")).Result);
        await ca.WaitForAsync<PlayerDecidedMessage>("PlayerDecided", d => d.PlayerId == b.Id.ToString("D"));
        var snapshot = await ca.GetSnapshotAsync(matchId);
        Assert.True(snapshot.View!.Allies.Single().Decided);

        Assert.Equal("ACCEPTED", (await ca.SubmitAsync(matchId, 1, "GUARD")).Result);
        await ca.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);

        var seenByA = ca.Dump();
        Assert.DoesNotContain(pactB, seenByA, StringComparison.Ordinal);
        Assert.DoesNotContain(offerB, seenByA, StringComparison.Ordinal);
        Assert.DoesNotContain("INTERACT", seenByA, StringComparison.Ordinal);
        Assert.DoesNotContain(ca.All<DraftOfferMessage>("DraftOffer"), o => o.OfferId == offerB);
    }

    [Fact]
    public async Task Ticket_is_single_use_and_protocol_version_is_checked_before_spending_it()
    {
        var (a, _, matchId) = await NewMatchAsync(factory);
        var ticket = await RealtimeSetup.TicketAsync(factory, a, matchId);
        Assert.Equal(43, ticket.Length);

        // Versión no soportada: 400 y el ticket sigue sin usar.
        var badVersion = await Assert.ThrowsAnyAsync<Exception>(() =>
            factory.Server.CreateWebSocketClient().ConnectAsync(RealtimeSetup.HubUri(factory, ticket, "1"), CancellationToken.None));
        Assert.Contains("400", badVersion.Message, StringComparison.Ordinal);

        await using (var first = RealtimeTestClient.Create(factory, ticket, a))
        {
            await first.Connection.StartAsync();
        }

        var reused = await Assert.ThrowsAnyAsync<Exception>(() =>
            factory.Server.CreateWebSocketClient().ConnectAsync(RealtimeSetup.HubUri(factory, ticket), CancellationToken.None));
        Assert.Contains("401", reused.Message, StringComparison.Ordinal);

        var forged = await Assert.ThrowsAnyAsync<Exception>(() =>
            factory.Server.CreateWebSocketClient().ConnectAsync(RealtimeSetup.HubUri(factory, new string('A', 43)), CancellationToken.None));
        Assert.Contains("401", forged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_participant_cannot_get_a_ticket_join_or_act()
    {
        var (a, _, matchId) = await NewMatchAsync(factory);
        var outsider = await RealtimeSetup.RegisterAsync(factory);
        using (var denied = await RealtimeSetup.RequestTicketAsync(factory, outsider, matchId))
        {
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }

        // Con un ticket de su propia partida, no puede unirse a otra.
        var (_, _, otherMatch) = await NewMatchAsync(factory);
        await using var client = await RealtimeSetup.ConnectAsync(factory, a, matchId);
        Assert.Equal("NOT_PARTICIPANT", (await client.JoinAsync(otherMatch)).Error?.Code);

        // Sin JoinMatch en esta conexión no se puede actuar.
        Assert.Equal("NOT_JOINED", (await client.SubmitAsync(matchId, 1, "GUARD")).Error?.Code);
        Assert.Equal("NOT_JOINED", (await client.GetSnapshotAsync(matchId)).Error?.Code);
    }

    [Fact]
    public async Task A_user_cannot_open_more_than_three_connections()
    {
        var (a, _, matchId) = await NewMatchAsync(factory);
        var open = new List<RealtimeTestClient>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                open.Add(await RealtimeSetup.ConnectAsync(factory, a, matchId));
            }

            var ticket = await RealtimeSetup.TicketAsync(factory, a, matchId);
            var refused = await Assert.ThrowsAnyAsync<Exception>(() =>
                factory.Server.CreateWebSocketClient().ConnectAsync(RealtimeSetup.HubUri(factory, ticket), CancellationToken.None));
            Assert.Contains("429", refused.Message, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var client in open)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Leaving_an_in_progress_match_counts_as_abandonment()
    {
        var (a, b, matchId) = await NewMatchAsync(factory);
        var (ca, _) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(factory, b, matchId);
        await using var _ = ca;
        await using var __ = cb;
        await StartRoundsAsync(matchId, ca, cb);

        var left = await ca.LeaveAsync(matchId);
        Assert.Null(left.Error);
        Assert.True(left.Abandoned);
        await cb.WaitForAsync<PlayerConnectionChangedMessage>("PlayerConnectionChanged", m => m.PlayerId == a.Id.ToString("D") && m.State == "ABANDONED");

        // El que queda sigue jugando solo: su orden basta para cerrar la ronda.
        Assert.Equal("ACCEPTED", (await cb.SubmitAsync(matchId, 1, "GUARD")).Result);
        var resolved = await cb.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Equal("RETIRED", resolved.Delta.AlliesChanged.Single().State);
    }

    private static async Task<(TestPlayer A, TestPlayer B, Guid MatchId)> NewMatchAsync(WebApplicationFactory<Program> host)
    {
        var a = await RealtimeSetup.RegisterAsync(host);
        var b = await RealtimeSetup.RegisterAsync(host);
        return (a, b, await RealtimeSetup.LaunchAsync(host, a, b));
    }

    /// <summary>Ambos eligen bendición y esperan la apertura de la ronda 1.</summary>
    private static async Task StartRoundsAsync(Guid matchId, RealtimeTestClient a, RealtimeTestClient b)
    {
        await a.PickFirstAsync(matchId);
        await b.PickFirstAsync(matchId);
        await a.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
        await b.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
    }

    private static async Task SubmitOrAcceptDownAsync(RealtimeTestClient client, Guid matchId, int round)
    {
        var reply = await client.SubmitAsync(matchId, round, "GUARD");
        Assert.True(reply.Result == "ACCEPTED" || reply.Error?.Code == "PLAYER_DOWN", reply.Error?.Code);
    }
}
