using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using Ascua.Api.Realtime;
using Ascua.Application.Matches;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Infrastructure.Persistence;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Ascua.Integration.Tests.Realtime;

/// <summary>
/// Contenedores propios: estas pruebas pausan Postgres y Redis y expiran partidas inactivas, así que no
/// comparten base con la colección "api" (que corre en paralelo).
/// </summary>
[CollectionDefinition(Name)]
public sealed class ResilienceCollection : ICollectionFixture<AscuaApiFactory>
{
    public const string Name = "resiliencia";
}

/// <summary>
/// Garantías de resiliencia del enunciado: durabilidad ante reinicios, caída de dependencias, aislamiento
/// de una partida que falla y limpieza de partidas abandonadas. Cada prueba levanta sus propias instancias
/// sobre los mismos contenedores y las desecha al terminar.
/// </summary>
[Collection(ResilienceCollection.Name)]
public sealed class ResilienceTests(AscuaApiFactory factory)
{
    [Fact]
    public async Task A_match_continues_with_contiguous_seq_after_restarting_the_instance_mid_round()
    {
        // La duración de ronda queda fija en la partida al crearla, así que rige en las tres instancias.
        var first = Instance(("Ascua:Realtime:RoundDuration", "00:00:04"));
        var a = await RealtimeSetup.RegisterAsync(first);
        var b = await RealtimeSetup.RegisterAsync(first);
        var matchId = await RealtimeSetup.LaunchAsync(first, a, b);
        var (ca, _) = await RealtimeSetup.JoinAsync(first, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(first, b, matchId);
        await using var _ = ca;
        await using var __ = cb;
        await StartRoundsAsync(matchId, ca, cb);
        await SubmitGuardAsync(ca, matchId, 1);
        await SubmitGuardAsync(cb, matchId, 1);
        await ca.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 2);
        var confirmed = await EventLogAsync(first, matchId);

        // Ronda 2 abierta y sin órdenes (desconectarse no la cierra): la instancia muere a mitad de ronda y,
        // sin ningún cliente conectado, solo el plazo guardado en Redis puede cerrarla en la instancia nueva.
        await first.DisposeAsync();
        await using var second = Instance();
        var a2 = await RealtimeSetup.LoginAsync(second, a);
        var b2 = await RealtimeSetup.LoginAsync(second, b);
        await EventuallyAsync(() => MatchAsync(second, matchId), m => m.Round >= 3, "la ronda 2 se cierre por plazo");

        // Los clientes reconectan con ticket nuevo y retoman en el mismo seq que quedó en Postgres.
        var (ca2, ack2) = await RealtimeSetup.JoinAsync(second, a2, matchId);
        var (cb2, _) = await RealtimeSetup.JoinAsync(second, b2, matchId);
        await using var ___ = ca2;
        await using var ____ = cb2;
        Assert.Equal(3, ack2.Snapshot!.View.Round);
        Assert.Equal((await MatchAsync(second, matchId)).LastSeq, ack2.Snapshot.Seq);

        // Orden confirmada (ACK) a mitad de ronda y otro reinicio: la jugada no se pierde.
        Assert.Equal("ACCEPTED", (await ca2.SubmitAsync(matchId, 3, "MOVE", "N")).Result);
        await second.DisposeAsync();
        await using var third = Instance();
        var a3 = await RealtimeSetup.LoginAsync(third, a);
        var b3 = await RealtimeSetup.LoginAsync(third, b);
        await EventuallyAsync(() => MatchAsync(third, matchId), m => m.Round >= 4, "la ronda 3 se cierre");

        var (ca3, ack3) = await RealtimeSetup.JoinAsync(third, a3, matchId);
        var (cb3, _) = await RealtimeSetup.JoinAsync(third, b3, matchId);
        await using var _____ = ca3;
        await using var ______ = cb3;
        var round = ack3.Snapshot!.View.Round;
        await SubmitGuardAsync(ca3, matchId, round, allowStale: true);
        await SubmitGuardAsync(cb3, matchId, round, allowStale: true);
        var resolved = await ca3.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == round);
        Assert.Equal(ack3.Snapshot.Seq, resolved.PreviousSeq);

        // Ni pérdidas ni duplicados: lo confirmado antes del primer reinicio sigue intacto, el seq es
        // contiguo, cada ronda se resolvió una sola vez y el MOVE de A de la ronda 3 se aplicó.
        var log = await EventLogAsync(third, matchId);
        Assert.Equal(confirmed, log.Take(confirmed.Count));
        Assert.Equal(Enumerable.Range(1, log.Count).Select(i => (long)i), log.Select(e => e.Seq));
        var resolutions = log.Where(e => e.Type == nameof(Domain.Events.RoundResolved)).Select(e => e.Round).ToList();
        Assert.Equal(resolutions.Distinct(), resolutions);
        Assert.Equal(round, resolutions.Count);
        Assert.Contains(log, e => e.Round == 3
            && e.Type is nameof(HeroMoved) or nameof(MoveBounced)
            && e.Payload.Contains(a.Id.ToString("D"), StringComparison.Ordinal));
        Assert.Equal(resolved.Seq, (await MatchAsync(third, matchId)).LastSeq);
    }

    [Theory]
    [InlineData("redis")]
    [InlineData("postgres")]
    public async Task A_dependency_outage_turns_readiness_red_without_killing_the_process_and_the_match_resumes(string dependency)
    {
        // Plazos de conexión cortos para que /health/ready conteste pronto con la dependencia congelada.
        await using var host = Instance(
            ("Ascua:Realtime:RoundDuration", "00:00:02"),
            ("ConnectionStrings:Postgres", $"{factory.Postgres.GetConnectionString()};Timeout=2;Command Timeout=2"),
            ("ConnectionStrings:Redis", $"{factory.Redis.GetConnectionString()},connectTimeout=1000,syncTimeout=1000,asyncTimeout=1000"));
        using var http = host.CreateClient();
        var a = await RealtimeSetup.RegisterAsync(host);
        var b = await RealtimeSetup.RegisterAsync(host);
        var matchId = await RealtimeSetup.LaunchAsync(host, a, b);
        var (ca, _) = await RealtimeSetup.JoinAsync(host, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(host, b, matchId);
        await using var _ = ca;
        await using var __ = cb;
        await StartRoundsAsync(matchId, ca, cb);
        var opened = await ca.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(http, "/health/ready"));

        IContainer container = dependency == "redis" ? factory.Redis : factory.Postgres;
        await container.PauseAsync();
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await StatusAsync(http, "/health/ready"));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(http, "/health/live"));

            // El plazo de la ronda 1 vence durante la caída: el temporizador falla, lo registra y sigue vivo.
            var remaining = DateTimeOffset.Parse(opened.Deadline, CultureInfo.InvariantCulture) - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining + TimeSpan.FromMilliseconds(300));
            }

            Assert.Equal(HttpStatusCode.OK, await StatusAsync(http, "/health/live"));
        }
        finally
        {
            await container.UnpauseAsync();
        }

        await EventuallyAsync(() => StatusAsync(http, "/health/ready"), s => s == HttpStatusCode.OK, "/health/ready vuelva a OK");

        // El plazo vencido no se perdió: la ronda 1 se cierra con GUARD y la partida sigue con normalidad.
        var first = await ca.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1, TimeSpan.FromSeconds(30));
        Assert.Contains(first.Delta.Events, e => (string)e["type"] == "DEFAULT_GUARD_APPLIED");
        await SubmitGuardAsync(ca, matchId, 2, allowStale: true);
        await SubmitGuardAsync(cb, matchId, 2, allowStale: true);
        var second = await cb.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 2);
        Assert.Equal(first.Seq, second.PreviousSeq);

        // Y se pueden crear partidas nuevas.
        var another = await RealtimeSetup.LaunchAsync(host, a, b);
        Assert.Equal("DRAFTING", (await MatchAsync(host, another)).Status);
    }

    [Fact]
    public async Task An_exception_in_one_match_faults_only_that_match_and_releases_its_players()
    {
        await using var host = Instance(("Ascua:Realtime:RoundDuration", "00:01:00"));
        var meters = host.Services.GetRequiredService<IMeterFactory>();
        long faults = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Scope == meters && instrument.Name == "ascua.matches.faulted")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref faults, value));
        listener.Start();

        var (broken, ba, bb) = await PlayingMatchAsync(host);
        var (healthy, ha, hb) = await PlayingMatchAsync(host);
        await using var _ = ba;
        await using var __ = bb;
        await using var ___ = ha;
        await using var ____ = hb;

        var redis = host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        foreach (var player in new[] { ba.Player, bb.Player })
        {
            await redis.StringSetAsync($"ascua:user:{player.Id:N}:active_match", broken.ToString("N"));
        }

        // Estado incoherente (la partida dice IN_PROGRESS y su GameState no) con el plazo vencido: el temporizador
        // real cierra la ronda y el dominio lanza, como lo haría un error de reglas en esa sola partida.
        var hot = host.Services.GetRequiredService<IMatchHotState>();
        var current = (await host.Services.GetRequiredService<MatchEngine>().GetAsync(broken, CancellationToken.None))!;
        await hot.SaveAsync(
            current with
            {
                Version = current.Version + 1,
                State = current.State with { Phase = MatchPhase.Drafting },
                Deadline = DateTimeOffset.UtcNow.AddSeconds(-1),
            },
            CancellationToken.None);

        // Mientras tanto, la otra partida juega su ronda en la misma instancia.
        await SubmitGuardAsync(ha, healthy, 1);
        await SubmitGuardAsync(hb, healthy, 1);
        await hb.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 1);

        foreach (var client in new[] { ba, bb })
        {
            var error = await client.WaitForAsync<ErrorPushDto>("Error", e => e.Error?.Code == "MATCH_NOT_ACTIVE");
            Assert.Equal("FAULTED", error.Error!.Details!["status"]);
            Assert.Equal("FAULTED", (await client.WaitForAsync<MatchFinishedMessage>("MatchFinished")).Outcome);
        }

        Assert.Equal("FAULTED", (await MatchAsync(host, broken)).Status);
        Assert.Equal(1, Interlocked.Read(ref faults));
        Assert.Contains(factory.Logs.Entries, e => e.Contains($"La partida {broken} falló", StringComparison.Ordinal));
        var registry = host.Services.GetRequiredService<IActiveMatchRegistry>();
        Assert.Null(await registry.GetAsync(ba.Player.Id, CancellationToken.None));
        Assert.Null(await registry.GetAsync(bb.Player.Id, CancellationToken.None));
        Assert.Equal("MATCH_NOT_ACTIVE", (await ba.SubmitAsync(broken, 1, "GUARD")).Error?.Code);

        // La sana no se enteró: sigue en curso, sin errores, y resuelve la ronda siguiente.
        await SubmitGuardAsync(ha, healthy, 2);
        await SubmitGuardAsync(hb, healthy, 2);
        await ha.WaitForAsync<RoundResolvedMessage>("RoundResolved", m => m.Round == 2);
        Assert.Equal("IN_PROGRESS", (await MatchAsync(host, healthy)).Status);
        Assert.Empty(ha.All<ErrorPushDto>("Error"));
        Assert.Empty(hb.All<ErrorPushDto>("Error"));
        using var http = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(http, "/health/live"));
    }

    [Fact]
    public async Task An_abandoned_match_expires_and_releases_the_active_match_mark()
    {
        await using var host = Instance(
            ("Ascua:Realtime:InactivityTimeout", "00:00:01"),
            ("Ascua:Realtime:CleanupInterval", "00:00:00.250"));
        var a = await RealtimeSetup.RegisterAsync(host);
        var b = await RealtimeSetup.RegisterAsync(host);
        var matchId = await RealtimeSetup.LaunchAsync(host, a, b);
        var redis = host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        foreach (var player in new[] { a, b })
        {
            await redis.StringSetAsync($"ascua:user:{player.Id:N}:active_match", matchId.ToString("N"));
        }

        // A entra y nadie juega (B ni siquiera se conecta): sin cambios por InactivityTimeout, la limpieza la expira.
        var (ca, _) = await RealtimeSetup.JoinAsync(host, a, matchId);
        await using var _ = ca;
        await EventuallyAsync(() => MatchAsync(host, matchId), m => m.Status == "EXPIRED", "la partida expire");

        var error = await ca.WaitForAsync<ErrorPushDto>("Error", e => e.Error?.Code == "MATCH_NOT_ACTIVE");
        Assert.Equal("EXPIRED", error.Error!.Details!["status"]);
        var registry = host.Services.GetRequiredService<IActiveMatchRegistry>();
        Assert.Null(await registry.GetAsync(a.Id, CancellationToken.None));
        Assert.Null(await registry.GetAsync(b.Id, CancellationToken.None));
        Assert.Equal("MATCH_NOT_ACTIVE", (await ca.JoinAsync(matchId)).Error?.Code);
    }

    /// <summary>Otra instancia de la API sobre los mismos contenedores, sin espera de drenaje al desecharla.</summary>
    private WebApplicationFactory<Program> Instance(params (string Key, string Value)[] settings)
    {
        var all = new Dictionary<string, string?> { ["Ascua:Realtime:DrainDelay"] = "00:00:00.050" };
        foreach (var (key, value) in settings)
        {
            all[key] = value;
        }

        return factory.WithSettings(all);
    }

    /// <summary>Partida de dos jugadores conectados, con el draft hecho y la ronda 1 abierta.</summary>
    private static async Task<(Guid MatchId, RealtimeTestClient A, RealtimeTestClient B)> PlayingMatchAsync(WebApplicationFactory<Program> host)
    {
        var a = await RealtimeSetup.RegisterAsync(host);
        var b = await RealtimeSetup.RegisterAsync(host);
        var matchId = await RealtimeSetup.LaunchAsync(host, a, b);
        var (ca, _) = await RealtimeSetup.JoinAsync(host, a, matchId);
        var (cb, _) = await RealtimeSetup.JoinAsync(host, b, matchId);
        await StartRoundsAsync(matchId, ca, cb);
        return (matchId, ca, cb);
    }

    /// <summary>Ambos eligen bendición y esperan la apertura de la ronda 1.</summary>
    private static async Task StartRoundsAsync(Guid matchId, RealtimeTestClient a, RealtimeTestClient b)
    {
        await a.PickFirstAsync(matchId);
        await b.PickFirstAsync(matchId);
        await a.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
        await b.WaitForAsync<RoundOpenedMessage>("RoundOpened", m => m.Round == 1);
    }

    /// <summary>GUARD aceptado; con <paramref name="allowStale"/> también vale que el plazo ya la haya cerrado.</summary>
    private static async Task SubmitGuardAsync(RealtimeTestClient client, Guid matchId, int round, bool allowStale = false)
    {
        var reply = await client.SubmitAsync(matchId, round, "GUARD");
        Assert.True(
            reply.Result == "ACCEPTED" || reply.Error?.Code == "PLAYER_DOWN" || (allowStale && reply.Error?.Code == "STALE_ROUND"),
            reply.Error?.Code);
    }

    private static async Task<HttpStatusCode> StatusAsync(HttpClient http, string path)
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative));
        return response.StatusCode;
    }

    private static async Task<MatchRecord> MatchAsync(WebApplicationFactory<Program> host, Guid matchId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        return await db.Matches.AsNoTracking().SingleAsync(m => m.Id == matchId);
    }

    private static async Task<List<LoggedEvent>> EventLogAsync(WebApplicationFactory<Program> host, Guid matchId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AscuaDbContext>();
        return await db.MatchEvents
            .Where(e => e.MatchId == matchId)
            .OrderBy(e => e.Seq)
            .Select(e => new LoggedEvent(e.Seq, e.Round, e.Type, e.Payload))
            .ToListAsync();
    }

    /// <summary>Sondea hasta que se cumpla la condición (sin esperas fijas largas).</summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> probe, Func<T, bool> done, string what, TimeSpan? timeout = null)
    {
        var giveUpAt = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var value = await probe();
            if (done(value))
            {
                return value;
            }

            if (DateTime.UtcNow > giveUpAt)
            {
                throw new TimeoutException($"Se agotó la espera a que {what}. Último valor: {value}");
            }

            await Task.Delay(100);
        }
    }

    private sealed record LoggedEvent(long Seq, int Round, string Type, string Payload);
}
