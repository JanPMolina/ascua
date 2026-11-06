using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

// Bot de Ascua: registra N jugadores, arranca una partida con el endpoint de desarrollo
// (POST /api/v1/dev/matches, solo local/dev) y la juega hasta el final con lo que ve cada jugador: camina hacia el
// enemigo más cercano o la escalera, pega de paso y abre o levanta lo que le quede a mano. La sala mide lo que diga la
// vista (9 o 11).
// Uso: dotnet run --project src/Ascua.Bot -- [baseUrl] [jugadores 2-5] [espera máx. ms]
// La prueba de carga (NBomber, MessagePack, N partidas simultáneas) vive en loadtests/.
var baseUrl = new Uri(args.Length > 0 ? args[0] : "http://localhost:8080");
var playerCount = args.Length > 1 ? Math.Clamp(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), 2, 5) : 2;
var maxThinkMs = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 500;
string[] heroes = ["HILVAN", "ANTEAYER", "POLIZON", "USURERA", "ESCUCHA", "AVARO", "MAMPOSTERO", "ERRATA", "CAMPANERO"];
var heroOffset = Random.Shared.Next(heroes.Length);

using var http = new HttpClient { BaseAddress = baseUrl };
await Api.WaitUntilReadyAsync(http);
var players = new List<BotPlayer>();
for (var i = 0; i < playerCount; i++)
{
    players.Add(await BotPlayer.RegisterAsync(http, heroes[(i + heroOffset) % heroes.Length]));
}

var launch = await Api.PostAsync(http, players[0].Token, "/api/v1/dev/matches",
    new { players = players.Select(p => new { userId = p.UserId, hero = p.Hero }) });
var matchId = (await launch.Content.ReadFromJsonAsync<LaunchResponse>())!.MatchId;
Console.WriteLine($"Partida {matchId} con {string.Join(", ", players.Select(p => p.Username))}");

var finished = new TaskCompletionSource<MatchFinished>(TaskCreationOptions.RunContinuationsAsynchronously);
var connections = new List<HubConnection>();
foreach (var player in players)
{
    var ticket = (await (await Api.PostAsync(http, player.Token, "/api/v1/realtime/tickets", new { matchId }))
        .Content.ReadFromJsonAsync<TicketResponse>())!.Ticket;

    // Solo WebSockets, sin negotiate; el ticket va en access_token (ADR-0006). JSON: el bot corre en local/dev.
    var connection = new HubConnectionBuilder()
        .WithUrl(new Uri(baseUrl, $"realtime/match?protocolVersion=4&access_token={ticket}"), options =>
        {
            options.SkipNegotiation = true;
            options.Transports = HttpTransportType.WebSockets;
        })
        .Build();

    connection.On<DraftOffer>("DraftOffer", offer => _ = Task.Run(async () =>
    {
        var choice = offer.Options[Random.Shared.Next(offer.Options.Count)];
        var reply = await connection.InvokeAsync<Reply>("DraftPick", new { matchId, offerId = offer.OfferId, blessingId = choice });
        Console.WriteLine($"  {player.Username} elige {choice}{reply.Describe()}");
    }));

    connection.On<PathOffer>("PathOffer", offer => _ = Task.Run(async () =>
    {
        var choice = offer.Options[Random.Shared.Next(offer.Options.Count)];
        var reply = await connection.InvokeAsync<Reply>("ChoosePath", new { matchId, offerId = offer.OfferId, path = choice });
        Console.WriteLine($"  {player.Username} se extinguió y elige {choice}{reply.Describe()}");
    }));

    // Misión: una de las dos que le ofrecen, al azar.
    connection.On<PactOffer>("PactOffer", offer => _ = Task.Run(async () =>
    {
        var choice = offer.Options[Random.Shared.Next(offer.Options.Count)];
        var reply = await connection.InvokeAsync<Reply>("ChoosePact", new { matchId, offerId = offer.OfferId, pactId = choice.PactId });
        Console.WriteLine($"  {player.Username} elige misión {choice.PactId}{reply.Describe()}");
    }));

    connection.On<RoundOpened>("RoundOpened", opened => _ = Task.Run(async () =>
    {
        await Task.Delay(Random.Shared.Next(Math.Max(1, maxThinkMs)));
        var view = (await connection.InvokeAsync<SnapshotMessage>("GetSnapshot", new { matchId })).View;
        var reply = await connection.InvokeAsync<Reply>("SubmitOrder", Decide(matchId, opened.Round, view));
        if (reply.Error?.Code is "INVALID_ORDER")
        {
            reply = await connection.InvokeAsync<Reply>("SubmitOrder", Guard(matchId, opened.Round));
        }

        if (reply.Error is not null && reply.Error.Code != "PLAYER_DOWN")
        {
            Console.WriteLine($"  {player.Username} ronda {opened.Round}{reply.Describe()}");
        }
    }));

    // Último en hablar: si este bot es el Campanero, se entera de las órdenes de los demás.
    connection.On<LastWord>("LastWord", heard => Console.WriteLine($"  {player.Username} oye {heard.Orders.Count} órdenes de la ronda {heard.Round}"));

    connection.On<RoundResolved>("RoundResolved", resolved =>
    {
        if (player == players[0])
        {
            Console.WriteLine($"Ronda {resolved.Round} resuelta (seq {resolved.PreviousSeq} → {resolved.Seq}, {resolved.Delta.Events.Count} eventos visibles)");
        }

    });

    connection.On<MatchFinished>("MatchFinished", result => finished.TrySetResult(result));
    connection.Closed += error =>
    {
        if (error is not null)
        {
            Console.WriteLine($"  {player.Username} desconectado: {error.Message}");
        }

        return Task.CompletedTask;
    };

    await connection.StartAsync();
    var join = await connection.InvokeAsync<Reply>("JoinMatch", new { matchId });
    Console.WriteLine($"{player.Username} ({player.Hero}) unido{join.Describe()}");
    connections.Add(connection);
}

var outcome = await finished.Task.WaitAsync(TimeSpan.FromMinutes(30));
Console.WriteLine($"Fin: {outcome.Outcome} en {outcome.Rounds} rondas");
foreach (var award in outcome.Awards ?? [])
{
    Console.WriteLine($"  Título {award.Title}: {award.Reason}");
}

foreach (var standing in outcome.Standings.OrderBy(s => s.Placement))
{
    Console.WriteLine($"  {standing.Placement}. {standing.DisplayName} {standing.Hero} {standing.FinalState} — {standing.Embers} ascuas");
}

foreach (var connection in connections)
{
    await connection.DisposeAsync();
}

// Decide con lo que ve: un paso hacia el enemigo más cercano (sin enemigos, hacia la escalera) y, desde donde quede, una acción.
static object Decide(Guid matchId, int round, BotView view)
{
    string[] directions = ["N", "S", "E", "W"];
    var clientActionId = Guid.CreateVersion7();
    var me = view.Me;
    if (me.State != "ALIVE" && me.State != "RENEGADE")
    {
        // Un Fantasma camina; lo demás no le hace falta a este bot.
        return new { matchId, round, clientActionId, order = (object)new { type = "MOVE", direction = directions[Random.Shared.Next(4)] } };
    }

    var blocked = view.Tiles.Where(t => t.Kind is "WALL" or "BRAZIER" or "ALTAR" or "CHEST" or "CHEST_OPENED" or "CRACK").Select(t => (t.X, t.Y)).ToHashSet();
    var occupied = view.Enemies.Select(e => (e.Position.X, e.Position.Y)).Concat(view.Allies.Where(a => a.Position is not null).Select(a => (a.Position!.X, a.Position!.Y))).ToHashSet();
    (int X, int Y) Next((int X, int Y) from, string direction) => direction switch
    {
        "N" => (from.X, from.Y - 1),
        "S" => (from.X, from.Y + 1),
        "E" => (from.X + 1, from.Y),
        _ => (from.X - 1, from.Y),
    };

    var position = (me.Position.X, me.Position.Y);
    var stairs = view.Tiles.FirstOrDefault(t => t.Kind == "STAIRS");
    (int X, int Y)? goal = view.Enemies.Count > 0
        ? view.Enemies.OrderBy(e => Math.Abs(e.Position.X - position.X) + Math.Abs(e.Position.Y - position.Y)).Select(e => ((int, int)?)(e.Position.X, e.Position.Y)).First()
        : stairs is not null ? (stairs.X, stairs.Y) : (view.Room.Size / 2, (view.Room.Size / 2) - 1);

    // Si no pega desde donde está, da un paso que lo acerque.
    string? step = null;
    string? AttackFrom((int X, int Y) from) =>
        directions.FirstOrDefault(d => view.Enemies.Any(e => (e.Position.X, e.Position.Y) == Next(from, d)));
    if (AttackFrom(position) is null && goal is { } target)
    {
        step = directions
            .Where(d => Next(position, d) is var n && n.X >= 0 && n.X < view.Room.Size && n.Y >= 0 && n.Y < view.Room.Size && !blocked.Contains(n) && !occupied.Contains(n))
            .OrderBy(d => Next(position, d) is var n ? Math.Abs(n.X - target.X) + Math.Abs(n.Y - target.Y) : 0)
            .ThenBy(_ => Random.Shared.Next())
            .FirstOrDefault();
    }

    var from = step is null ? position : Next(position, step);
    if (AttackFrom(from) is { } attack)
    {
        return new { matchId, round, clientActionId, order = (object)new { type = "ATTACK", direction = attack, step } };
    }

    // Lo adyacente que se pueda abrir o levantar; si no, solo caminar.
    if (view.Tiles.Any(t => t.Kind == "CHEST") || view.Allies.Any(a => a.State == "DOWN"))
    {
        var use = directions.FirstOrDefault(d => view.Tiles.Any(t => t.Kind == "CHEST" && (t.X, t.Y) == Next(from, d))
            || view.Allies.Any(a => a is { State: "DOWN", Position: not null } && (a.Position.X, a.Position.Y) == Next(from, d)));
        if (use is not null)
        {
            return new { matchId, round, clientActionId, order = (object)new { type = "INTERACT", direction = use, step } };
        }
    }

    return step is null ? Guard(matchId, round) : new { matchId, round, clientActionId, order = (object)new { type = "MOVE", direction = step } };
}

static object Guard(Guid matchId, int round) =>
    new { matchId, round, clientActionId = Guid.CreateVersion7(), order = new { type = "GUARD" } };

internal sealed record BotPlayer(Guid UserId, string Username, string Hero, string Token)
{
    private const string Password = "bot-ascua-local-1234";

    public static async Task<BotPlayer> RegisterAsync(HttpClient http, string hero)
    {
        var username = $"bot_{Guid.NewGuid():N}"[..16];
        var register = await Api.PostAsync(http, null, "/api/v1/auth/register", new { username, email = $"{username}@bot.local", password = Password });
        var profile = (await register.Content.ReadFromJsonAsync<Profile>())!;
        var login = await Api.PostAsync(http, null, "/api/v1/auth/login", new { username, password = Password });
        var tokens = (await login.Content.ReadFromJsonAsync<Tokens>())!;
        return new BotPlayer(profile.Id, username, hero, tokens.AccessToken);
    }
}

internal static class Api
{
    /// <summary>En docker compose el bot puede arrancar antes que la API: espera a /health/ready.</summary>
    public static async Task WaitUntilReadyAsync(HttpClient http)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(new Uri("/health/ready", UriKind.Relative));
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (attempt < 60)
            {
                // La API todavía no escucha.
            }

            if (attempt >= 60)
            {
                throw new InvalidOperationException("La API no estuvo lista en 60 s.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, string? token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{path} respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        return response;
    }
}

internal sealed record Profile(Guid Id);

internal sealed record Tokens(string AccessToken);

internal sealed record LaunchResponse(Guid MatchId);

internal sealed record TicketResponse(string Ticket);

internal sealed record ErrorBody(string Code, string Message);

internal sealed record Reply(ErrorBody? Error, string? Result)
{
    public string Describe() => Error is null ? (Result is null ? "" : $" ({Result})") : $" → {Error.Code}: {Error.Message}";
}

internal sealed record DraftOffer(Guid OfferId, IReadOnlyList<string> Options);

internal sealed record PathOffer(Guid OfferId, IReadOnlyList<string> Options);

internal sealed record PactOption(string PactId);

internal sealed record PactOffer(Guid OfferId, IReadOnlyList<PactOption> Options);

internal sealed record AwardLine(string Title, string Reason);

internal sealed record RoundOpened(int Round);

internal sealed record HeardOrder(string PlayerId, string Type, string? Direction);

internal sealed record LastWord(int Round, IReadOnlyList<HeardOrder> Orders, bool TypesOnly);

internal sealed record BotPosition(int X, int Y);

internal sealed record BotTile(int X, int Y, string Kind);

internal sealed record BotEnemy(BotPosition Position, string Kind);

internal sealed record BotAlly(string PlayerId, string State, BotPosition? Position);

internal sealed record BotMe(string State, BotPosition Position);

internal sealed record BotRoom(int Size);

internal sealed record BotView(BotMe Me, IReadOnlyList<BotAlly> Allies, IReadOnlyList<BotEnemy> Enemies, IReadOnlyList<BotTile> Tiles, BotRoom Room);

internal sealed record SnapshotMessage(BotView View);

internal sealed record SeenEvent(string Type, string? ActorId, string? TargetId, string? Phase);

internal sealed record DeltaMe(BotPosition Position);

internal sealed record VisibleEvents(IReadOnlyList<SeenEvent> Events, IReadOnlyList<BotAlly> AlliesChanged, DeltaMe? Me);

internal sealed record RoundResolved(int Round, long PreviousSeq, long Seq, VisibleEvents Delta);

internal sealed record Standing(string DisplayName, string Hero, string FinalState, int Embers, int Placement);

internal sealed record MatchFinished(string Outcome, int Rounds, IReadOnlyList<Standing> Standings, IReadOnlyList<AwardLine>? Awards);
