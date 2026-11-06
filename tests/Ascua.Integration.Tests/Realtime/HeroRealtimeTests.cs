using Ascua.Api.Realtime;
using Ascua.Application.Matches;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ascua.Integration.Tests.Realtime;

/// <summary>
/// Los nueve héroes por el hub (contrato 2.0.0): señales rápidas, Último en hablar, Repique, traiciones reveladas y
/// lo que cada uno ve de los demás. Para no depender del azar, los estados se preparan confirmándolos en Postgres y
/// Redis, como haría el motor.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class HeroRealtimeTests(AscuaApiFactory factory)
{
    // ───────────── Señales rápidas ─────────────

    [Fact]
    public async Task A_signal_reaches_only_its_side_is_validated_rate_limited_and_never_stored()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        var match = await StartAsync(host, HeroClass.Errata, HeroClass.Hilvan, HeroClass.Avaro);
        await using var _ = match;
        var (a, b, c) = (match.Clients[0], match.Clients[1], match.Clients[2]);
        await InjectAsync(host, match.Id, s => s with { Heroes = s.Heroes.SetItem(2, s.Heroes[2] with { Status = HeroStatus.Renegade }) });

        var sent = await a.SendSignalAsync(match.Id, "DANGER", new PositionDto { X = 3, Y = 3 });
        Assert.Null(sent.Error);
        Assert.NotNull(sent.SentAt);

        var seenByB = await b.WaitForAsync<SignalReceivedMessage>("SignalReceived");
        Assert.Equal("DANGER", seenByB.Kind);
        Assert.Equal(a.Player.Id.ToString("D"), seenByB.FromPlayerId);
        Assert.Equal(3, seenByB.Position!.X);
        await a.WaitForAsync<SignalReceivedMessage>("SignalReceived");
        await Task.Delay(200);
        Assert.Empty(c.All<SignalReceivedMessage>("SignalReceived"));

        var limited = await a.SendSignalAsync(match.Id, "HERE");
        Assert.Equal("RATE_LIMITED", limited.Error?.Code);
        Assert.True(limited.Error!.RetryAfterMs > 0);

        var badKind = await b.SendSignalAsync(match.Id, "MENTIRA");
        Assert.Equal("INVALID_PAYLOAD", badKind.Error?.Code);
        Assert.Equal("kind", badKind.Error!.Details!["field"]);
        var badTile = await b.SendSignalAsync(match.Id, "HERE", new PositionDto { X = 9, Y = 0 });
        Assert.Equal("INVALID_PAYLOAD", badTile.Error?.Code);
        Assert.Equal("position", badTile.Error!.Details!["field"]);

        // Un Renegado solo le habla a su bando; sin casilla también vale.
        var fromRenegade = await c.SendSignalAsync(match.Id, "MINE");
        Assert.Null(fromRenegade.Error);
        var heard = await c.WaitForAsync<SignalReceivedMessage>("SignalReceived");
        Assert.Null(heard.Position);
        await Task.Delay(200);
        Assert.DoesNotContain(b.All<SignalReceivedMessage>("SignalReceived"), s => s.Kind == "MINE");

        await using (var stranger = await RealtimeSetup.ConnectAsync(host, a.Player, match.Id))
        {
            Assert.Equal("NOT_JOINED", (await stranger.SendSignalAsync(match.Id, "HERE")).Error?.Code);
        }

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        Assert.False(await db.MatchEvents.AnyAsync(e => e.MatchId == match.Id && e.Type.Contains("Signal")));
    }

    // ───────────── Último en hablar ─────────────

    [Fact]
    public async Task When_all_the_others_decided_only_the_bell_ringer_hears_their_orders_and_again_after_reconnecting()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        var match = await StartAsync(host, HeroClass.Campanero, HeroClass.Errata, HeroClass.Hilvan);
        await using var _ = match;
        var (campanero, b, c) = (match.Clients[0], match.Clients[1], match.Clients[2]);

        Assert.Equal("ACCEPTED", (await b.SubmitAsync(match.Id, 1, "ATTACK", "E")).Result);
        await Task.Delay(200);
        Assert.Empty(campanero.All<LastWordMessage>("LastWord"));

        Assert.Equal("ACCEPTED", (await c.SubmitAsync(match.Id, 1, "MOVE", "N")).Result);
        var heard = await campanero.WaitForAsync<LastWordMessage>("LastWord");
        Assert.Equal(1, heard.Round);
        Assert.False(heard.TypesOnly);
        var byB = heard.Orders.Single(o => o.PlayerId == b.Player.Id.ToString("D"));
        Assert.Equal("ATTACK", byB.Type);
        Assert.Equal("E", byB.Direction);
        Assert.Equal("MOVE", heard.Orders.Single(o => o.PlayerId == c.Player.Id.ToString("D")).Type);

        // Nadie más lo recibe y la ronda sigue abierta: él todavía puede responder.
        Assert.Empty(b.All<LastWordMessage>("LastWord"));
        Assert.Empty(c.All<LastWordMessage>("LastWord"));
        Assert.Empty(b.All<RoundResolvedMessage>("RoundResolved"));

        // Vuelve a entrar con otra conexión mientras la ronda sigue abierta: se lo cuentan otra vez.
        var (back, _) = await RealtimeSetup.JoinAsync(host, campanero.Player, match.Id);
        await using var __ = back;
        var again = await back.WaitForAsync<LastWordMessage>("LastWord");
        Assert.Equal(heard.Orders.Count, again.Orders.Count);

        Assert.Equal("ACCEPTED", (await back.SubmitAsync(match.Id, 1, "GUARD")).Result);
        await b.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
    }

    [Fact]
    public async Task Someone_in_their_grace_period_does_not_count_for_everybody_decided()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        var match = await StartAsync(host, HeroClass.Campanero, HeroClass.Errata, HeroClass.Hilvan);
        await using var _ = match;
        var (campanero, b, c) = (match.Clients[0], match.Clients[1], match.Clients[2]);

        Assert.Equal("ACCEPTED", (await b.SubmitAsync(match.Id, 1, "MOVE", "N")).Result);
        await c.DisposeAsync();

        var heard = await campanero.WaitForAsync<LastWordMessage>("LastWord");
        Assert.Equal("GUARD", heard.Orders.Single(o => o.PlayerId == c.Player.Id.ToString("D")).Type);
    }

    [Fact]
    public async Task As_a_renegade_the_bell_ringer_only_hears_the_types()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        var match = await StartAsync(host, HeroClass.Campanero, HeroClass.Errata);
        await using var _ = match;
        var (campanero, b) = (match.Clients[0], match.Clients[1]);
        await InjectAsync(host, match.Id, s => s with { Heroes = s.Heroes.SetItem(0, s.Heroes[0] with { Status = HeroStatus.Renegade }) });

        Assert.Equal("ACCEPTED", (await b.SubmitAsync(match.Id, 1, "ATTACK", "E")).Result);

        var heard = await campanero.WaitForAsync<LastWordMessage>("LastWord");
        Assert.True(heard.TypesOnly);
        Assert.Equal("ATTACK", heard.Orders.Single().Type);
        Assert.Null(heard.Orders.Single().Direction);
    }

    // ───────────── Repique ─────────────

    [Fact]
    public async Task After_the_bell_the_others_get_less_time_late_orders_are_refused_and_the_round_waits_for_the_ringer()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Ascua:Realtime:RoundDuration"] = "00:02:00",
            ["Ascua:Realtime:BellDuration"] = "00:00:01",
        });
        var match = await StartAsync(host, HeroClass.Campanero, HeroClass.Errata);
        await using var _ = match;
        var (campanero, b) = (match.Clients[0], match.Clients[1]);

        Assert.Equal("ACCEPTED", (await campanero.SubmitAsync(match.Id, 1, "ABILITY", "N")).Result);
        Assert.Equal("ACCEPTED", (await b.SubmitAsync(match.Id, 1, "GUARD")).Result);

        var opened = await b.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 2);
        Assert.Equal(campanero.Player.Id.ToString("D"), opened.BellRingerId);
        Assert.NotNull(opened.BellDeadline);
        Assert.True(DateTimeOffset.Parse(opened.BellDeadline!, System.Globalization.CultureInfo.InvariantCulture) < DateTimeOffset.Parse(opened.Deadline, System.Globalization.CultureInfo.InvariantCulture));
        var resolved = await b.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Contains(resolved.Delta.Events, e => (string)e["type"] == "BELL_RUNG");
        Assert.Equal(opened.BellDeadline, resolved.Delta.RoundDeadline);
        var ringerSees = await campanero.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Equal(opened.Deadline, ringerSees.Delta.RoundDeadline);
        Assert.Equal(campanero.Player.Id.ToString("D"), ringerSees.Delta.Bell?.RingerId);

        // Vence el plazo reducido: se avisa a todos, se le cuenta al Campanero y ya no se aceptan órdenes de los demás.
        var closed = await b.WaitForAsync<SnapshotMessage>("Snapshot", s => s.View.Bell?.Closed == true);
        Assert.True(closed.View.Bell!.Closed);
        var heard = await campanero.WaitForAsync<LastWordMessage>("LastWord", m => m.Round == 2, TimeSpan.FromSeconds(5));
        Assert.Equal("GUARD", heard.Orders.Single().Type);
        Assert.Equal("ORDER_TOO_LATE", (await b.SubmitAsync(match.Id, 2, "MOVE", "N")).Error?.Code);

        // La ronda sigue abierta para él (conserva todo el reloj) y se cierra cuando decide.
        Assert.DoesNotContain(b.All<RoundResolvedMessage>("RoundResolved"), m => m.Round == 2);
        Assert.Equal("ACCEPTED", (await campanero.SubmitAsync(match.Id, 2, "MOVE", "N")).Result);
        await b.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 2);

        // Dura una sola ronda.
        var next = await b.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 3);
        Assert.Null(next.BellDeadline);
    }

    [Fact]
    public async Task If_the_ringer_decides_first_the_others_keep_their_seconds()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Ascua:Realtime:RoundDuration"] = "00:02:00",
            ["Ascua:Realtime:BellDuration"] = "00:00:30",
        });
        var match = await StartAsync(host, HeroClass.Campanero, HeroClass.Errata);
        await using var _ = match;
        var (campanero, b) = (match.Clients[0], match.Clients[1]);
        await InjectAsync(host, match.Id, s => s with { BellRound = s.Round, BellRinger = s.Heroes[0].Player });

        Assert.Equal("ACCEPTED", (await campanero.SubmitAsync(match.Id, 1, "GUARD")).Result);
        await Task.Delay(300);
        Assert.Empty(b.All<RoundResolvedMessage>("RoundResolved"));

        Assert.Equal("ACCEPTED", (await b.SubmitAsync(match.Id, 1, "GUARD")).Result);
        await b.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
    }

    [Fact]
    public async Task The_bell_deadline_is_durable_it_comes_back_from_postgres_after_a_restart()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Ascua:Realtime:RoundDuration"] = "00:02:00",
            ["Ascua:Realtime:BellDuration"] = "00:00:07",
        });
        var match = await StartAsync(host, HeroClass.Campanero, HeroClass.Errata);
        await using var _ = match;
        await InjectAsync(host, match.Id, s => s with { BellRound = s.Round, BellRinger = s.Heroes[0].Player });

        var fromPostgres = await host.Services.GetRequiredService<IMatchStore>().LoadAsync(match.Id, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(7), fromPostgres!.BellDuration);
        Assert.Equal(fromPostgres.Deadline - TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(7), fromPostgres.BellDeadline);
        Assert.Equal(fromPostgres.BellDeadline, fromPostgres.WakeAt);
    }

    // ───────────── Vistas por el hub ─────────────

    [Fact]
    public async Task The_wire_shows_each_hero_what_they_should_see_and_nothing_else()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        var match = await StartAsync(host, HeroClass.Hilvan, HeroClass.Polizon, HeroClass.Usurera, HeroClass.Escucha);
        await using var _ = match;
        var (hilvan, polizon, usurera, escucha) = (match.Clients[0], match.Clients[1], match.Clients[2], match.Clients[3]);
        var hostEnemy = 77;
        await InjectAsync(host, match.Id, s =>
        {
            var heroes = s.Heroes
                .SetItem(0, s.Heroes[0] with { Debt = 2, DebtTo = s.Heroes[2].Player, DebtSinceRound = 1, EchoQueue = [] })
                .SetItem(1, s.Heroes[1] with { HostEnemyId = hostEnemy, Position = new Position(0, 0) });
            var room = s.CurrentRoom with { Ground = [new GroundEmbers(s.Heroes[3].Position, 2)] };
            return s with
            {
                Heroes = heroes,
                Enemies = [.. s.Enemies, Enemy.Create(hostEnemy, EnemyKind.Scavenger, new Position(0, 0))],
                Rooms = s.Rooms.SetItem(0, room),
            };
        });

        var seenByUsurera = (await usurera.GetSnapshotAsync(match.Id)).View!;
        var seenByHilvan = (await hilvan.GetSnapshotAsync(match.Id)).View!;
        var seenByEscucha = (await escucha.GetSnapshotAsync(match.Id)).View!;
        var seenByPolizon = (await polizon.GetSnapshotAsync(match.Id)).View!;

        // Hilván: dos cuerpos, y sus aliados ven los dos.
        Assert.NotNull(seenByHilvan.Me.SecondBody);
        Assert.NotNull(seenByUsurera.Allies.Single(a => a.Hero == "HILVAN").SecondBody);

        // Polizón: nadie lo ubica; solo él sabe en quién va.
        Assert.Null(seenByUsurera.Allies.Single(a => a.Hero == "POLIZON").Position);
        Assert.Null(seenByUsurera.Me.HostEnemyId);
        Assert.Equal(hostEnemy, seenByPolizon.Me.HostEnemyId);

        // La deuda: la ven el deudor y la Usurera; nadie más.
        Assert.Equal(2, seenByHilvan.Me.Debt);
        Assert.Equal(usurera.Player.Id.ToString("D"), seenByHilvan.Me.DebtTo);
        Assert.Equal(2, seenByUsurera.Allies.Single(a => a.Hero == "HILVAN").DebtToYou);
        Assert.Null(seenByEscucha.Allies.Single(a => a.Hero == "HILVAN").DebtToYou);

        // La Escucha: todo el terreno, y ve las ascuas que tiene debajo.
        Assert.Equal(Room.DefaultSize * Room.DefaultSize, seenByEscucha.Tiles.Count);
        Assert.Single(seenByEscucha.GroundEmbers);
        Assert.DoesNotContain(seenByEscucha.Enemies, e => e.EnemyId == hostEnemy);
    }

    [Fact]
    public async Task Betrayals_stay_secret_until_the_final_message_reveals_them()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:RoundDuration"] = "00:02:00" });
        var match = await StartAsync(host, HeroClass.Errata, HeroClass.Hilvan);
        await using var _ = match;
        var (a, b) = (match.Clients[0], match.Clients[1]);
        var traitor = new PlayerId(a.Player.Id.ToString("D"));
        var victim = new PlayerId(b.Player.Id.ToString("D"));
        var wardenPosition = MatchFactory.WardenPosition;
        await InjectAsync(host, match.Id, s => s with
        {
            RoomIndex = s.Rooms.Count - 1,
            Betrayals = [new Betrayal(traitor, victim, 2), new Betrayal(traitor, victim, 1)],
            Enemies = [Enemy.Create(90, EnemyKind.Warden, wardenPosition, 24) with { Hp = 1, Phase = 2 }],
            Heroes = [s.Heroes[0] with { Position = wardenPosition.Step(Direction.South), Facing = Direction.North }, s.Heroes[1] with { Position = new Position(8, 8), SecondBody = new Position(6, 8) }],
        });

        var before = await b.GetSnapshotAsync(match.Id);
        Assert.DoesNotContain("BETRAYAL", System.Text.Json.JsonSerializer.Serialize(before.View), StringComparison.OrdinalIgnoreCase);

        Assert.Equal("ACCEPTED", (await a.SubmitAsync(match.Id, 1, "ATTACK", "N")).Result);
        Assert.Equal("ACCEPTED", (await b.SubmitAsync(match.Id, 1, "GUARD")).Result);

        var finished = await b.WaitForAsync<MatchFinishedMessage>("MatchFinished");
        var betrayal = Assert.Single(finished.Betrayals);
        Assert.Equal(a.Player.Id.ToString("D"), betrayal.TraitorId);
        Assert.Equal(b.Player.Id.ToString("D"), betrayal.VictimId);
        Assert.Equal(3, betrayal.Damage);
        var resolved = await b.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Contains(resolved.Delta.Events, e => (string)e["type"] == "BETRAYAL_REVEALED");
    }

    [Fact]
    public async Task A_whole_match_with_new_heroes_can_be_played_through_the_hub_and_replayed()
    {
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Ascua:Realtime:RoundDuration"] = "00:00:20",
            ["Ascua:Realtime:TestRoundLimit"] = "8",
        });
        var match = await StartAsync(host, HeroClass.Polizon, HeroClass.Avaro, HeroClass.Mampostero, HeroClass.Anteayer);
        await using var _ = match;
        string[] directions = ["N", "E", "S", "W"];
        string[] types = ["MOVE", "ATTACK", "ABILITY", "INTERACT"];
        for (var round = 1; round <= 8; round++)
        {
            var current = round;
            foreach (var client in match.Clients)
            {
                await client.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == current, TimeSpan.FromSeconds(30));
            }

            foreach (var client in match.Clients)
            {
                var reply = await client.SubmitAsync(match.Id, round, types[round % types.Length], directions[(round + client.GetHashCode()) & 3]);
                if (reply.Error is { Code: "SKILL_ON_COOLDOWN" or "INVALID_ORDER" })
                {
                    await client.SubmitAsync(match.Id, round, "GUARD");
                }
            }

            foreach (var client in match.Clients)
            {
                await client.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == current, TimeSpan.FromSeconds(30));
            }
        }

        var finished = await match.Clients[0].WaitForAsync<MatchFinishedMessage>("MatchFinished", null, TimeSpan.FromSeconds(30));
        Assert.Equal(4, finished.Standings.Count);
        Assert.Equal("EMBER_EXTINGUISHED", finished.Outcome);

        // Los eventos nuevos se pueden leer de vuelta para la reproducción.
        var steps = await host.Services.GetRequiredService<IMatchHistoryStore>().GetStepsAsync(match.Id, 1, 8, CancellationToken.None);
        Assert.NotEmpty(steps);
    }

    // ───────────── Ayudas ─────────────

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

    /// <summary>Confirma un estado ajustado con el lock de la partida, como una mutación más del motor (Postgres + Redis).</summary>
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
