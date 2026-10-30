using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

// Bot de Ascua: registra N jugadores, arranca una partida con el endpoint de desarrollo
// (POST /api/v1/dev/matches, solo local/dev) y la juega con órdenes al azar hasta el final.
// Uso: dotnet run --project src/Ascua.Bot -- [baseUrl] [jugadores 2-4] [espera máx. ms]
// TODO(Jan): escenarios de carga con NBomber y protocolo MessagePack.
var baseUrl = new Uri(args.Length > 0 ? args[0] : "http://localhost:8080");
var playerCount = args.Length > 1 ? Math.Clamp(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), 2, 4) : 2;
var maxThinkMs = args.Length > 2 ? int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 500;
string[] heroes = ["YUNQUE", "CHISPA", "CUERVO", "RAIZ"];

using var http = new HttpClient { BaseAddress = baseUrl };
await Api.WaitUntilReadyAsync(http);
var players = new List<BotPlayer>();
for (var i = 0; i < playerCount; i++)
{
    players.Add(await BotPlayer.RegisterAsync(http, heroes[i]));
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
        .WithUrl(new Uri(baseUrl, $"realtime/match?protocolVersion=1&access_token={ticket}"), options =>
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

    connection.On<RoundOpened>("RoundOpened", opened => _ = Task.Run(async () =>
    {
        await Task.Delay(Random.Shared.Next(Math.Max(1, maxThinkMs)));
        var reply = await connection.InvokeAsync<Reply>("SubmitOrder", RandomOrder(matchId, opened.Round));
        if (reply.Error?.Code is "INVALID_ORDER")
        {
            reply = await connection.InvokeAsync<Reply>("SubmitOrder", Guard(matchId, opened.Round));
        }

        if (reply.Error is not null && reply.Error.Code != "PLAYER_DOWN")
        {
            Console.WriteLine($"  {player.Username} ronda {opened.Round}{reply.Describe()}");
        }
    }));

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
foreach (var standing in outcome.Standings.OrderBy(s => s.Placement))
{
    Console.WriteLine($"  {standing.Placement}. {standing.DisplayName} {standing.Hero} {standing.FinalState} — {standing.Embers} ascuas");
}

foreach (var connection in connections)
{
    await connection.DisposeAsync();
}

static object RandomOrder(Guid matchId, int round)
{
    string[] types = ["MOVE", "MOVE", "ATTACK", "ATTACK", "ABILITY", "INTERACT"];
    string[] directions = ["N", "S", "E", "W"];
    return Random.Shared.Next(6) == 0
        ? Guard(matchId, round)
        : new
        {
            matchId,
            round,
            clientActionId = Guid.CreateVersion7(),
            order = new { type = types[Random.Shared.Next(types.Length)], direction = directions[Random.Shared.Next(4)] },
        };
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

internal sealed record RoundOpened(int Round);

internal sealed record VisibleEvents(IReadOnlyList<object> Events);

internal sealed record RoundResolved(int Round, long PreviousSeq, long Seq, VisibleEvents Delta);

internal sealed record Standing(string DisplayName, string Hero, string FinalState, int Embers, int Placement);

internal sealed record MatchFinished(string Outcome, int Rounds, IReadOnlyList<Standing> Standings);
