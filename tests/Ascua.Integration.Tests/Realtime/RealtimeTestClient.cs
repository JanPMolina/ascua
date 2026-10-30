using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ascua.Api.Endpoints;
using Ascua.Api.Realtime;
using Ascua.Application.Matches;
using Ascua.Domain.Model;
using MessagePack;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Ascua.Integration.Tests.Realtime;

/// <summary>Usuario registrado con su JWT de acceso.</summary>
public sealed record TestPlayer(Guid Id, string Username, string AccessToken);

/// <summary>Atajos para registrar jugadores, lanzar partidas y pedir tickets.</summary>
public static class RealtimeSetup
{
    private const string Password = "correcto-caballo-bateria";

    public static async Task<TestPlayer> RegisterAsync(WebApplicationFactory<Program> host)
    {
        using var http = host.CreateClient();
        var username = $"rt{Guid.NewGuid():N}"[..20];
        using var register = await http.PostAsJsonAsync(
            new Uri("/api/v1/auth/register", UriKind.Relative), new { username, email = $"{username}@example.com", password = Password });
        register.EnsureSuccessStatusCode();
        var profile = (await register.Content.ReadFromJsonAsync<UserProfileResponse>())!;
        return await LoginAsync(host, new TestPlayer(profile.Id, username, ""));
    }

    /// <summary>JWT nuevo para un jugador ya registrado (otra instancia firma con otra clave efímera).</summary>
    public static async Task<TestPlayer> LoginAsync(WebApplicationFactory<Program> host, TestPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        using var http = host.CreateClient();
        using var login = await http.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative), new { username = player.Username, password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
        return player with { AccessToken = tokens.AccessToken };
    }

    public static Task<Guid> LaunchAsync(WebApplicationFactory<Program> host, params TestPlayer[] players) =>
        host.Services.GetRequiredService<IMatchLauncher>().LaunchAsync(
            new LaunchMatchRequest(Guid.NewGuid(), [.. players.Select((p, i) => new MatchSeat(p.Id, (HeroClass)(i % 4)))]),
            CancellationToken.None);

    public static async Task<HttpResponseMessage> RequestTicketAsync(WebApplicationFactory<Program> host, TestPlayer player, Guid matchId)
    {
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", player.AccessToken);
        return await http.PostAsJsonAsync(new Uri(RealtimeEndpoints.TicketsPath, UriKind.Relative), new { matchId });
    }

    public static async Task<string> TicketAsync(WebApplicationFactory<Program> host, TestPlayer player, Guid matchId)
    {
        using var response = await RequestTicketAsync(host, player, matchId);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RealtimeTicketResponse>())!.Ticket;
    }

    public static Uri HubUri(WebApplicationFactory<Program> host, string ticket, string protocolVersion = "1") =>
        new(host.Server.BaseAddress, $"realtime/match?protocolVersion={protocolVersion}&access_token={ticket}");

    public static async Task<RealtimeTestClient> ConnectAsync(WebApplicationFactory<Program> host, TestPlayer player, Guid matchId)
    {
        var client = RealtimeTestClient.Create(host, await TicketAsync(host, player, matchId), player);
        await client.Connection.StartAsync();
        return client;
    }

    /// <summary>Conecta y llama a JoinMatch; falla si no recibe el ACK.</summary>
    public static async Task<(RealtimeTestClient Client, JoinReply Ack)> JoinAsync(WebApplicationFactory<Program> host, TestPlayer player, Guid matchId)
    {
        var client = await ConnectAsync(host, player, matchId);
        var ack = await client.JoinAsync(matchId);
        Assert.True(ack.Error is null, ack.Error?.Code);
        return (client, ack);
    }
}

/// <summary>Cliente SignalR (solo WebSockets + MessagePack, como el contrato) que guarda todo lo que recibe.</summary>
public sealed class RealtimeTestClient : IAsyncDisposable
{
    private readonly ConcurrentQueue<(string Name, object Payload)> _received = new();

    private RealtimeTestClient(HubConnection connection, TestPlayer player)
    {
        Connection = connection;
        Player = player;
    }

    public HubConnection Connection { get; }

    public TestPlayer Player { get; }

    public IReadOnlyCollection<(string Name, object Payload)> Received => _received;

    public static RealtimeTestClient Create(WebApplicationFactory<Program> host, string ticket, TestPlayer player)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(RealtimeSetup.HubUri(host, ticket), options =>
            {
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
                options.WebSocketFactory = async (context, cancellationToken) =>
                    await host.Server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
            })
            .AddMessagePackProtocol()
            .Build();

        var client = new RealtimeTestClient(connection, player);
        client.Listen<RoundOpenedMessage>("RoundOpened");
        client.Listen<PlayerDecidedMessage>("PlayerDecided");
        client.Listen<RoundResolvedMessage>("RoundResolved");
        client.Listen<SnapshotMessage>("Snapshot");
        client.Listen<DraftOfferMessage>("DraftOffer");
        client.Listen<PlayerConnectionChangedMessage>("PlayerConnectionChanged");
        client.Listen<MatchFinishedMessage>("MatchFinished");
        client.Listen<ServerDrainingMessage>("ServerDraining");
        client.Listen<ErrorPushDto>("Error");
        return client;
    }

    public IEnumerable<T> All<T>(string name) => _received.Where(r => r.Name == name).Select(r => (T)r.Payload);

    public async Task<T> WaitForAsync<T>(string name, Func<T, bool>? predicate = null, TimeSpan? timeout = null)
    {
        var giveUpAt = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < giveUpAt)
        {
            var match = All<T>(name).FirstOrDefault(m => predicate?.Invoke(m) ?? true);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"{Player.Username} no recibió {name} a tiempo. Recibió: {string.Join(", ", _received.Select(r => r.Name))}");
    }

    /// <summary>Todo lo recibido en JSON, para buscar fugas de información.</summary>
    public string Dump() => string.Join("\n", _received.Select(r => $"{r.Name} {JsonSerializer.Serialize(r.Payload, r.Payload.GetType())}"));

    public Task<JoinReply> JoinAsync(Guid matchId) =>
        Connection.InvokeAsync<JoinReply>("JoinMatch", new MatchRequest { MatchId = matchId.ToString("D") });

    public Task<SnapshotReply> GetSnapshotAsync(Guid matchId) =>
        Connection.InvokeAsync<SnapshotReply>("GetSnapshot", new MatchRequest { MatchId = matchId.ToString("D") });

    public Task<LeaveReply> LeaveAsync(Guid matchId) =>
        Connection.InvokeAsync<LeaveReply>("LeaveMatch", new MatchRequest { MatchId = matchId.ToString("D") });

    public Task<SubmitReply> SubmitAsync(Guid matchId, int round, string type, string? direction = null, Guid? clientActionId = null) =>
        Connection.InvokeAsync<SubmitReply>("SubmitOrder", new SubmitOrderRequest
        {
            MatchId = matchId.ToString("D"),
            Round = round,
            ClientActionId = (clientActionId ?? Guid.CreateVersion7()).ToString("D"),
            Order = new OrderPayload { Type = type, Direction = direction },
        });

    public Task<DraftReply> DraftPickAsync(Guid matchId, string offerId, string blessingId) =>
        Connection.InvokeAsync<DraftReply>("DraftPick", new DraftPickRequest { MatchId = matchId.ToString("D"), OfferId = offerId, BlessingId = blessingId });

    /// <summary>Espera su oferta de draft y elige la primera opción.</summary>
    public async Task PickFirstAsync(Guid matchId)
    {
        var offer = await WaitForAsync<DraftOfferMessage>("DraftOffer");
        var reply = await DraftPickAsync(matchId, offer.OfferId, offer.Options[0]);
        Assert.True(reply.Error is null, reply.Error?.Code);
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();

    private void Listen<T>(string name) where T : notnull =>
        Connection.On<T>(name, message => _received.Enqueue((name, message)));
}

// Respuestas de invocación: el ACK o { error }. Mismas claves que el contrato.

[MessagePackObject]
public sealed class ErrorBodyDto
{
    [Key("code")]
    public string Code { get; set; } = "";

    [Key("message")]
    public string Message { get; set; } = "";

    [Key("retryAfterMs")]
    public int? RetryAfterMs { get; set; }

    [Key("details")]
    public Dictionary<string, object>? Details { get; set; }
}

[MessagePackObject]
public sealed class ErrorPushDto
{
    [Key("matchId")]
    public string? MatchId { get; set; }

    [Key("error")]
    public ErrorBodyDto? Error { get; set; }
}

[MessagePackObject]
public sealed class JoinReply
{
    [Key("matchId")]
    public string? MatchId { get; set; }

    [Key("playerId")]
    public string? PlayerId { get; set; }

    [Key("seat")]
    public int Seat { get; set; }

    [Key("snapshot")]
    public SnapshotMessage? Snapshot { get; set; }

    [Key("error")]
    public ErrorBodyDto? Error { get; set; }
}

[MessagePackObject]
public sealed class SubmitReply
{
    [Key("round")]
    public int Round { get; set; }

    [Key("clientActionId")]
    public string? ClientActionId { get; set; }

    [Key("result")]
    public string? Result { get; set; }

    [Key("receivedAt")]
    public string? ReceivedAt { get; set; }

    [Key("error")]
    public ErrorBodyDto? Error { get; set; }
}

[MessagePackObject]
public sealed class SnapshotReply
{
    [Key("matchId")]
    public string? MatchId { get; set; }

    [Key("seq")]
    public long Seq { get; set; }

    [Key("view")]
    public PlayerViewDto? View { get; set; }

    [Key("error")]
    public ErrorBodyDto? Error { get; set; }
}

[MessagePackObject]
public sealed class LeaveReply
{
    [Key("abandoned")]
    public bool Abandoned { get; set; }

    [Key("error")]
    public ErrorBodyDto? Error { get; set; }
}

[MessagePackObject]
public sealed class DraftReply
{
    [Key("blessingId")]
    public string? BlessingId { get; set; }

    [Key("error")]
    public ErrorBodyDto? Error { get; set; }
}
