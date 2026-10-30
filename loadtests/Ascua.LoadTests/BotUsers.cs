using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Ascua.LoadTests;

/// <summary>Jugador bot registrado. El token de acceso dura 10 min: se renueva con el refresh token.</summary>
public sealed class BotUser(Guid id, string username)
{
    public Guid Id { get; } = id;

    public string Username { get; } = username;

    public string AccessToken { get; set; } = "";

    public string RefreshToken { get; set; } = "";

    public DateTime IssuedAt { get; set; }
}

/// <summary>REST de la API: registro, login, refresh, partida de desarrollo y tickets del hub.</summary>
public sealed class AscuaApi(HttpClient http)
{
    private const string Password = "carga-ascua-local-1234";
    private static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(7);

    public async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 120; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(new Uri("/health/ready", UriKind.Relative), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // La API todavía no escucha.
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new InvalidOperationException("La API no estuvo lista en 120 s.");
    }

    public async Task<BotUser> RegisterAsync(string prefix, CancellationToken cancellationToken)
    {
        var username = $"{prefix}{Guid.NewGuid():N}"[..20];
        var profile = await PostAsync<Profile>(null, "/api/v1/auth/register", new { username, email = $"{username}@carga.local", password = Password }, cancellationToken);
        var user = new BotUser(profile.Id, username);
        Apply(user, await PostAsync<Tokens>(null, "/api/v1/auth/login", new { username, password = Password }, cancellationToken));
        return user;
    }

    /// <summary>Renueva el token de acceso si está por vencer (refresh no gasta Argon2, a diferencia del login).</summary>
    public async Task EnsureFreshAsync(BotUser user, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - user.IssuedAt < RenewAfter)
        {
            return;
        }

        Apply(user, await PostAsync<Tokens>(null, "/api/v1/auth/refresh", new { refreshToken = user.RefreshToken }, cancellationToken));
    }

    public async Task<Guid> LaunchDevMatchAsync(IReadOnlyList<BotUser> players, CancellationToken cancellationToken)
    {
        string[] heroes = ["YUNQUE", "CHISPA", "CUERVO", "RAIZ"];
        var body = new { players = players.Select((p, i) => new { userId = p.Id, hero = heroes[i % heroes.Length] }) };
        return (await PostAsync<Launched>(players[0].AccessToken, "/api/v1/dev/matches", body, cancellationToken)).MatchId;
    }

    public async Task<string> TicketAsync(BotUser user, Guid matchId, CancellationToken cancellationToken) =>
        (await PostAsync<TicketResponse>(user.AccessToken, "/api/v1/realtime/tickets", new { matchId }, cancellationToken)).Ticket;

    private static void Apply(BotUser user, Tokens tokens)
    {
        user.AccessToken = tokens.AccessToken;
        user.RefreshToken = tokens.RefreshToken;
        user.IssuedAt = DateTime.UtcNow;
    }

    /// <summary>POST con reintento ante 429 (respeta <c>Retry-After</c>) y 5xx transitorios.</summary>
    private async Task<T> PostAsync<T>(string? token, string path, object body, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (await response.Content.ReadFromJsonAsync<T>(cancellationToken))!;
            }

            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!retryable || attempt >= 5)
            {
                throw new HttpRequestException($"{path} respondió {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
            }

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt);
            await Task.Delay(wait, cancellationToken);
        }
    }

    private sealed record Profile(Guid Id);

    private sealed record Tokens(string AccessToken, string RefreshToken);

    private sealed record Launched(Guid MatchId);

    private sealed record TicketResponse(string Ticket);
}
