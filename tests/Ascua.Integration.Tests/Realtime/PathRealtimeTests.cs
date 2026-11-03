using Ascua.Api.Realtime;
using Ascua.Application.Matches;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ascua.Integration.Tests.Realtime;

/// <summary>
/// Caminos tras la extinción por el hub (contrato 1.3.0): <c>PathOffer</c>, <c>ChoosePath</c>, el plazo por
/// defecto y partidas con un Renegado y con una Brasa que renace. Para no depender del azar, la extinción se
/// prepara confirmando un estado ajustado en Postgres y Redis, como haría el motor.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PathRealtimeTests(AscuaApiFactory factory)
{
    private static readonly Position Stairs = MatchFactory.StairsPosition;

    [Fact]
    public async Task ChoosePath_is_validated_idempotent_private_and_waited_for_by_the_round()
    {
        // Plazos largos: la prueba no debe competir con el plazo de 15 s de la elección.
        var host = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Ascua:Realtime:RoundDuration"] = "00:02:00",
            ["Ascua:Realtime:PathChoiceDuration"] = "00:02:00",
        });
        var (a, b, matchId) = await NewMatchAsync(host);
        var (ca, _) = await RealtimeSetup.JoinAsync(host, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(host, b, matchId);
        await using var _ = ca;
        await using var __ = cb;
        await StartRoundsAsync(matchId, ca, cb);
        await InjectAsync(host, matchId, state => state with
        {
            Heroes = [Extinct(state.Heroes[0], saved: 5), state.Heroes[1]],
        });

        var snapshot = await ca.GetSnapshotAsync(matchId);
        var offer = await ca.WaitForAsync<PathOfferMessage>("PathOffer");
        Assert.Equal(new[] { "FRIENDSHIP", "VENGEANCE" }, offer.Options);
        Assert.Equal("FRIENDSHIP", offer.DefaultPath);
        Assert.Equal(5, offer.SavedEmbers);
        Assert.Equal(3, offer.VengeanceEmbers);
        Assert.Equal(2, offer.FriendshipEmbersMin);
        Assert.Equal(5, offer.FriendshipEmbersMax);
        Assert.Equal("EXTINCT", snapshot.View!.Me.State);
        Assert.Equal(offer.OfferId, snapshot.View.Me.Path!.OfferId);

        // Validación de esquema y de la oferta.
        var badPath = await ca.ChoosePathAsync(matchId, offer.OfferId, "REVENGE");
        Assert.Equal("INVALID_PAYLOAD", badPath.Error?.Code);
        Assert.Equal("path", badPath.Error!.Details!["field"]);
        Assert.Equal("INVALID_PAYLOAD", (await ca.ChoosePathAsync(matchId, "no-es-un-uuid", "FRIENDSHIP")).Error?.Code);
        Assert.Equal("PATH_CLOSED", (await ca.ChoosePathAsync(matchId, Guid.NewGuid().ToString("D"), "FRIENDSHIP")).Error?.Code);
        Assert.Equal("PATH_CLOSED", (await cb.ChoosePathAsync(matchId, offer.OfferId, "FRIENDSHIP")).Error?.Code);
        var outsider = await RealtimeSetup.RegisterAsync(host);
        var engine = host.Services.GetRequiredService<MatchEngine>();
        Assert.Equal("NOT_PARTICIPANT", (await engine.ChoosePathAsync(matchId, outsider.Id, Guid.Parse(offer.OfferId), PathKind.Vengeance, CancellationToken.None)).Error?.Code);
        // Una segunda conexión de A sin JoinMatch no puede elegir.
        await using (var stranger = await RealtimeSetup.ConnectAsync(host, a, matchId))
        {
            Assert.Equal("NOT_JOINED", (await stranger.ChoosePathAsync(matchId, offer.OfferId, "FRIENDSHIP")).Error?.Code);
        }

        // B ya decidió, pero la ronda espera la elección abierta de A (conectado).
        Assert.Equal("ACCEPTED", (await cb.SubmitAsync(matchId, 1, "GUARD")).Result);
        await Task.Delay(500);
        Assert.Empty(cb.All<RoundResolvedMessage>("RoundResolved"));

        var chosen = await ca.ChoosePathAsync(matchId, offer.OfferId, "VENGEANCE");
        Assert.Equal("ACCEPTED", chosen.Result);
        Assert.Equal("VENGEANCE", chosen.Path);
        var again = await ca.ChoosePathAsync(matchId, offer.OfferId, "VENGEANCE");
        Assert.Equal("DUPLICATE_ACTION", again.Result);
        Assert.Equal(chosen.AcceptedAt, again.AcceptedAt);
        Assert.Equal("PATH_CLOSED", (await ca.ChoosePathAsync(matchId, offer.OfferId, "FRIENDSHIP")).Error?.Code);

        // Al resolverse la ronda todos ven el camino; nunca la oferta ni la elección privada de otro.
        var resolved = await cb.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Contains(resolved.Delta.Events, e => (string)e["type"] == "PATH_CHOSEN" && (string)e["detail"] == "VENGEANCE");
        Assert.Equal("RENEGADE", resolved.Delta.AlliesChanged.Single().State);
        Assert.Empty(cb.All<PathOfferMessage>("PathOffer"));
        Assert.DoesNotContain("PATH_SELECTED", cb.Dump(), StringComparison.Ordinal);
        Assert.DoesNotContain(offer.OfferId, cb.Dump(), StringComparison.Ordinal);

        var types = await EventTypesAsync(host, matchId);
        Assert.Contains(nameof(PathSelected), types);
        Assert.Contains(nameof(PathChosen), types);
    }

    [Fact]
    public async Task An_unanswered_offer_becomes_friendship_when_its_deadline_expires()
    {
        var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:PathChoiceDuration"] = "00:00:01" });
        var (a, b, matchId) = await NewMatchAsync(host);
        var (ca, _) = await RealtimeSetup.JoinAsync(host, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(host, b, matchId);
        await using var _ = ca;
        await using var __ = cb;
        await StartRoundsAsync(matchId, ca, cb);
        await InjectAsync(host, matchId, state => state with { Heroes = [Extinct(state.Heroes[0], saved: 4), state.Heroes[1]] });
        Assert.Equal("ACCEPTED", (await cb.SubmitAsync(matchId, 1, "GUARD")).Result);

        // El temporizador (sondeo de Redis) aplica Amistad al vencer los 15 s (1 s aquí) y la ronda se cierra.
        var auto = await ca.WaitForAsync<SnapshotMessage>("Snapshot", s => s.View.Me.Path?.Choice == "FRIENDSHIP");
        Assert.Equal("EXTINCT", auto.View.Me.State);
        var resolved = await cb.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Contains(resolved.Delta.Events, e => (string)e["type"] == "PATH_CHOSEN" && (string)e["detail"] == "FRIENDSHIP");
        var mine = await ca.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Equal("EMBER_SPIRIT", mine.Delta.Me.State);
        Assert.Equal(4, mine.Delta.Me.Path!.SavedEmbers);
    }

    [Fact]
    public async Task A_match_with_a_renegade_that_appears_in_the_next_room_and_claims_the_threshold()
    {
        var (a, b, matchId) = await NewMatchAsync(factory);
        var (ca, _) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(factory, b, matchId);
        await using var _ = ca;
        await using var __ = cb;
        await StartRoundsAsync(matchId, ca, cb);
        var second = new Room
        {
            Index = 1,
            SpawnPoints = MatchFactory.SpawnPoints,
            InitialEnemies = [Enemy.Create(50, EnemyKind.Scavenger, new Position(4, 4)), Enemy.Create(51, EnemyKind.Moth, new Position(2, 2))],
        };
        await InjectAsync(factory, matchId, state => state with
        {
            Rooms = [FirstRoom(), second, BossRoom()],
            RoomIndex = 0,
            Enemies = [],
            Heroes = [Extinct(state.Heroes[0], saved: 6), state.Heroes[1] with { Position = Stairs.Step(Direction.South) }],
        });

        await ca.GetSnapshotAsync(matchId);
        var offer = await ca.WaitForAsync<PathOfferMessage>("PathOffer");
        Assert.Equal("ACCEPTED", (await ca.ChoosePathAsync(matchId, offer.OfferId, "VENGEANCE")).Result);
        Assert.Equal("ACCEPTED", (await cb.SubmitAsync(matchId, 1, "MOVE", "N")).Result);

        // La misma ronda revela el camino, baja de piso y el Renegado aparece lejos del grupo.
        var seenByB = await cb.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Equal(2, seenByB.Delta.FloorChanged);
        Assert.Contains(seenByB.Delta.Events, e => (string)e["type"] == "PATH_CHOSEN" && (string)e["detail"] == "VENGEANCE");
        var spawned = Assert.Single(seenByB.Delta.Events, e => (string)e["type"] == "RENEGADE_SPAWNED");
        Assert.False(spawned.ContainsKey("position"));
        Assert.Equal(1, seenByB.Delta.Me.Shield);
        Assert.Null(seenByB.Delta.AlliesChanged.Single().Position);

        var seenByA = await ca.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Equal("RENEGADE", seenByA.Delta.Me.State);
        Assert.Equal(PathRules.RenegadeHp(seenByA.Delta.Me.MaxHp, wardenRoom: false), seenByA.Delta.Me.Hp);
        Assert.Equal(3, seenByA.Delta.Me.Embers);
        var current = (await factory.Services.GetRequiredService<MatchEngine>().GetAsync(matchId, CancellationToken.None))!;
        Assert.Single(current.State.Enemies);

        var opened = await ca.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 2);
        Assert.Equal(2, opened.ExpectedPlayers);

        // B abandona: solo queda el Renegado en pie y, al resolverse la ronda, el Umbral reclama.
        Assert.True((await cb.LeaveAsync(matchId)).Abandoned);
        Assert.Equal("ACCEPTED", (await ca.SubmitAsync(matchId, 2, "GUARD")).Result);
        var finished = await ca.WaitForAsync<MatchFinishedMessage>("MatchFinished");
        Assert.Equal("THRESHOLD_CLAIMED", finished.Outcome);
        Assert.Equal(a.Id.ToString("D"), Assert.Single(finished.Winners));
        Assert.Equal("RENEGADE", finished.Standings.Single(s => s.PlayerId == a.Id.ToString("D")).FinalState);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        var match = await db.Matches.AsNoTracking().SingleAsync(m => m.Id == matchId);
        Assert.Contains("ThresholdClaimed", match.Result, StringComparison.Ordinal);
        Assert.Contains(nameof(RenegadeSpawned), await EventTypesAsync(factory, matchId));
    }

    [Fact]
    public async Task A_match_with_an_ember_that_helps_and_is_reborn_on_the_next_floor()
    {
        var (a, b, matchId) = await NewMatchAsync(factory);
        var (ca, _) = await RealtimeSetup.JoinAsync(factory, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(factory, b, matchId);
        await using var _ = ca;
        await using var __ = cb;
        await StartRoundsAsync(matchId, ca, cb);
        await InjectAsync(factory, matchId, state => state with
        {
            Rooms = [FirstRoom(), new Room { Index = 1, SpawnPoints = MatchFactory.SpawnPoints }, BossRoom()],
            RoomIndex = 0,
            Enemies = [],
            Heroes = [Extinct(state.Heroes[0], saved: 10) with { Position = new Position(2, 3) }, state.Heroes[1] with { Position = Stairs.Step(Direction.South) }],
        });

        await ca.GetSnapshotAsync(matchId);
        var offer = await ca.WaitForAsync<PathOfferMessage>("PathOffer");
        Assert.Equal("ACCEPTED", (await ca.ChoosePathAsync(matchId, offer.OfferId, "FRIENDSHIP")).Result);
        Assert.Equal("ACCEPTED", (await cb.SubmitAsync(matchId, 1, "GUARD")).Result);

        var spirit = await ca.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        Assert.Equal("EMBER_SPIRIT", spirit.Delta.Me.State);
        Assert.Equal(10, spirit.Delta.Me.Path!.SavedEmbers);
        Assert.Equal(3, spirit.Delta.Me.Path.RebirthEmbers);
        Assert.Null(spirit.Delta.Me.Path.OfferId);
        var forB = await cb.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);
        var emberForB = forB.Delta.AlliesChanged.Single();
        Assert.Equal("EMBER_SPIRIT", emberForB.State);
        Assert.Equal(3, emberForB.Position!.Y);
        Assert.Equal(0, emberForB.Embers);

        // La Brasa da órdenes (Faro) y el grupo baja de piso: renace en la entrada.
        await ca.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 2 && m.ExpectedPlayers == 2);
        Assert.Equal("ACCEPTED", (await ca.SubmitAsync(matchId, 2, "GUARD")).Result);
        Assert.Equal("ACCEPTED", (await cb.SubmitAsync(matchId, 2, "MOVE", "N")).Result);

        var reborn = await ca.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 2);
        Assert.Equal(2, reborn.Delta.FloorChanged);
        Assert.Contains(reborn.Delta.Events, e => (string)e["type"] == "BEACON_LIT");
        Assert.Contains(reborn.Delta.Events, e => (string)e["type"] == "EMBER_REBORN");
        Assert.Equal("ALIVE", reborn.Delta.Me.State);
        Assert.Equal(PathRules.RebirthHp(reborn.Delta.Me.MaxHp), reborn.Delta.Me.Hp);
        Assert.Equal(PathRules.RebirthEmbers(10, 0), reborn.Delta.Me.Embers);
        Assert.Equal(0, reborn.Delta.Me.Path!.SavedEmbers);
        var rebornForB = await cb.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 2);
        Assert.Contains(rebornForB.Delta.Events, e => (string)e["type"] == "EMBER_REBORN");
        Assert.Equal("ALIVE", rebornForB.Delta.AlliesChanged.Single().State);

        var dumpB = cb.Dump();
        Assert.Empty(cb.All<PathOfferMessage>("PathOffer"));
        Assert.DoesNotContain("ECHO_GAINED", dumpB, StringComparison.Ordinal);
        Assert.DoesNotContain("PATH_OFFERED", dumpB, StringComparison.Ordinal);
        Assert.Contains(nameof(EmberReborn), await EventTypesAsync(factory, matchId));
    }

    private static Hero Extinct(Hero hero, int saved) => hero with
    {
        Status = HeroStatus.Extinct,
        Hp = 0,
        Embers = 0,
        SavedEmbers = saved,
        Extinctions = hero.Extinctions + 1,
        PathOfferPending = true,
        PathChoice = null,
    };

    private static Room FirstRoom() => new Room { Index = 0, SpawnPoints = MatchFactory.SpawnPoints }.WithTerrain(Stairs, Terrain.Stairs);

    private static Room BossRoom() => new()
    {
        Index = 2,
        SpawnPoints = MatchFactory.SpawnPoints,
        InitialEnemies = [Enemy.Create(90, EnemyKind.Warden, MatchFactory.WardenPosition, EnemyCatalog.WardenHpPerPlayer * 2)],
    };

    /// <summary>Confirma un estado ajustado con el lock de la partida, como una mutación más del motor (Postgres + Redis).</summary>
    private static async Task InjectAsync(WebApplicationFactory<Program> host, Guid matchId, Func<GameState, GameState> change)
    {
        var hot = host.Services.GetRequiredService<IMatchHotState>();
        var store = host.Services.GetRequiredService<IMatchStore>();
        await using var lease = await hot.TryLockAsync(matchId, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.NotNull(lease);
        // De Redis primero: trae los plazos de esta partida (PathChoiceDuration no tiene columna en Postgres).
        var current = await hot.GetAsync(matchId, CancellationToken.None) ?? (await store.LoadAsync(matchId, CancellationToken.None))!;
        var next = current with { State = change(current.State), Version = current.Version + 1 };
        Assert.True(await store.CommitAsync(current, next, [], CancellationToken.None));
        await hot.SaveAsync(next, CancellationToken.None);
    }

    private static async Task<List<string>> EventTypesAsync(WebApplicationFactory<Program> host, Guid matchId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        return await db.MatchEvents.Where(e => e.MatchId == matchId).Select(e => e.Type).Distinct().ToListAsync();
    }

    private static async Task<(TestPlayer A, TestPlayer B, Guid MatchId)> NewMatchAsync(WebApplicationFactory<Program> host)
    {
        var a = await RealtimeSetup.RegisterAsync(host);
        var b = await RealtimeSetup.RegisterAsync(host);
        return (a, b, await RealtimeSetup.LaunchAsync(host, a, b));
    }

    private static async Task StartRoundsAsync(Guid matchId, RealtimeTestClient a, RealtimeTestClient b)
    {
        await a.PickFirstAsync(matchId);
        await b.PickFirstAsync(matchId);
        await a.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
        await b.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
    }
}
