using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ascua.Api.Security;
using Ascua.Integration.Tests.Realtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ascua.Integration.Tests;

/// <summary>
/// Transporte: CORS y orígenes WebSocket con lista blanca, encabezados de seguridad en toda respuesta y HSTS
/// fuera de Development.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed partial class SecurityHeadersTests(AscuaApiFactory factory) : IDisposable
{
    // El de appsettings.json, que también admite el upgrade WebSocket.
    private const string AllowedOrigin = "http://localhost:8080";
    private const string ForeignOrigin = "https://atacante.example";

    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task Preflight_from_allowed_origin_returns_cors_headers_without_credentials()
    {
        using var response = await PreflightAsync(AllowedOrigin, "PATCH", "/api/v1/users/me", "authorization, content-type");

        // 204 y no 401: el preflight se responde antes de autenticar, aunque el endpoint exija token.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AllowedOrigin, Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("PATCH", Header(response, "Access-Control-Allow-Methods"), StringComparison.Ordinal);
        var allowedHeaders = Header(response, "Access-Control-Allow-Headers");
        Assert.Contains("authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("600", Header(response, "Access-Control-Max-Age"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task Preflight_from_foreign_origin_gets_no_cors_headers()
    {
        using var response = await PreflightAsync(ForeignOrigin, "POST", "/api/v1/auth/login", "content-type");

        Assert.DoesNotContain(response.Headers, header => header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Actual_request_exposes_only_allowed_origin_and_needed_headers()
    {
        using var allowed = await GetAsync("/api/v1/version", AllowedOrigin);
        using var foreign = await GetAsync("/api/v1/version", ForeignOrigin);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(AllowedOrigin, Header(allowed, "Access-Control-Allow-Origin"));
        var exposed = Header(allowed, "Access-Control-Expose-Headers");
        Assert.Contains("Location", exposed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Retry-After", exposed, StringComparison.OrdinalIgnoreCase);

        // La respuesta llega (CORS no es control de acceso del servidor), pero el navegador no deja leerla.
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        Assert.False(foreign.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("/", HttpStatusCode.OK)]
    [InlineData("/js/main.js", HttpStatusCode.OK)]
    [InlineData("/api/v1/version", HttpStatusCode.OK)]
    [InlineData("/openapi/v1.json", HttpStatusCode.OK)]
    [InlineData("/docs/", HttpStatusCode.OK)]
    [InlineData("/api/v1/users/me", HttpStatusCode.Unauthorized)]
    [InlineData("/no-existe", HttpStatusCode.NotFound)]
    public async Task Every_response_carries_security_headers(string path, HttpStatusCode status)
    {
        using var response = await GetAsync(path);

        Assert.Equal(status, response.StatusCode);
        var csp = Header(response, "Content-Security-Policy");
        Assert.Equal(path.StartsWith("/docs", StringComparison.Ordinal) ? SecurityHeaders.DocsPolicy : SecurityHeaders.DefaultPolicy, csp);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal(SecurityHeaders.PermissionsPolicy, Header(response, "Permissions-Policy"));
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Client_policy_forbids_inline_code_and_foreign_origins()
    {
        using var response = await GetAsync("/");
        var csp = Header(response, "Content-Security-Policy");

        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("http", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("*", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Docs_inline_script_is_allowed_by_hash_not_by_unsafe_inline()
    {
        using var response = await GetAsync("/docs/");
        var html = await response.Content.ReadAsStringAsync();
        var csp = Header(response, "Content-Security-Policy");
        var scriptSrc = csp.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src ", StringComparison.Ordinal));

        Assert.DoesNotContain("unsafe-inline", scriptSrc, StringComparison.Ordinal);
        Assert.DoesNotContain("http", csp, StringComparison.Ordinal);
        var inline = InlineScript().Matches(html);
        Assert.NotEmpty(inline);
        foreach (Match script in inline)
        {
            var hash = "sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script.Groups["body"].Value)));
            Assert.True(
                scriptSrc.Contains($"'{hash}'", StringComparison.Ordinal),
                $"El script inline de Scalar cambió: actualice SecurityHeaders.ScalarInitScriptHash a \"{hash}\".");
        }
    }

    [Fact]
    public async Task Hsts_is_sent_over_https_only_outside_development()
    {
        var https = new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://ascua.example") };
        using var development = factory.CreateClient(https);
        using var developmentResponse = await development.GetAsync(new Uri("/health/live", UriKind.Relative));
        Assert.False(developmentResponse.Headers.Contains("Strict-Transport-Security"));

        // Fuera de Development no hay clave JWT efímera salvo que se permita explícitamente.
        using var production = factory.WithWebHostBuilder(builder => builder
            .UseEnvironment("Production")
            .UseSetting("Ascua:Jwt:AllowEphemeralKey", "true"));
        using var productionHttps = production.CreateClient(https);
        using var productionHttp = production.CreateClient();

        using var overHttps = await productionHttps.GetAsync(new Uri("/health/live", UriKind.Relative));
        using var overHttp = await productionHttp.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, overHttps.StatusCode);
        Assert.Equal("max-age=31536000", Header(overHttps, "Strict-Transport-Security"));
        // Por HTTP plano (o en localhost) el navegador lo ignoraría; ASP.NET Core ni lo envía.
        Assert.False(overHttp.Headers.Contains("Strict-Transport-Security"));
    }

    /// <summary>
    /// Kestrel real, no TestServer: el cliente WebSocket del TestServer instala su propio
    /// <c>IHttpWebSocketFeature</c> y <c>UseWebSockets</c> se salta entonces la verificación de <c>Origin</c>.
    /// </summary>
    [Fact]
    public async Task WebSocket_upgrade_from_foreign_origin_is_rejected_before_spending_the_ticket()
    {
        using var kestrel = factory.WithWebHostBuilder(_ => { });
        // Loopback y puerto libre: ignora ASPNETCORE_HTTP_PORTS (8080 en la imagen del SDK) y no choca con nada.
        kestrel.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        kestrel.StartServer();
        var alice = await RealtimeSetup.RegisterAsync(kestrel);
        var bruno = await RealtimeSetup.RegisterAsync(kestrel);
        var matchId = await RealtimeSetup.LaunchAsync(kestrel, alice, bruno);
        var ticket = await RealtimeSetup.TicketAsync(kestrel, alice, matchId);

        using var foreign = await OpenWebSocketAsync(kestrel, ticket, ForeignOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.HttpStatusCode);
        Assert.NotEqual(WebSocketState.Open, foreign.State);

        // El rechazo ocurre antes de autenticar: el mismo ticket sigue sirviendo desde el origen permitido.
        using var allowed = await OpenWebSocketAsync(kestrel, ticket, AllowedOrigin);
        Assert.Equal(HttpStatusCode.SwitchingProtocols, allowed.HttpStatusCode);
        Assert.Equal(WebSocketState.Open, allowed.State);

        // Un cliente que no es navegador (bot, prueba de carga) no envía Origin y entra con su ticket.
        using var withoutOrigin = await OpenWebSocketAsync(kestrel, await RealtimeSetup.TicketAsync(kestrel, alice, matchId), origin: null);
        Assert.Equal(HttpStatusCode.SwitchingProtocols, withoutOrigin.HttpStatusCode);
        Assert.Equal(WebSocketState.Open, withoutOrigin.State);
    }

    private static async Task<ClientWebSocket> OpenWebSocketAsync(WebApplicationFactory<Program> host, string ticket, string? origin)
    {
        using var http = host.CreateClient();
        var uri = new UriBuilder(new Uri(http.BaseAddress!, $"realtime/match?protocolVersion=2&access_token={ticket}")) { Scheme = "ws" }.Uri;
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (origin is not null)
        {
            socket.Options.SetRequestHeader("Origin", origin);
        }

        try
        {
            await socket.ConnectAsync(uri, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // El código de la respuesta queda en HttpStatusCode; la prueba lo verifica.
        }

        return socket;
    }

    private async Task<HttpResponseMessage> PreflightAsync(string origin, string method, string path, string headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri(path, UriKind.Relative));
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return await _client.SendAsync(request);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : throw new InvalidOperationException($"Falta el encabezado {name} en {response.RequestMessage?.RequestUri}.");

    [GeneratedRegex("<script(?![^>]*\\bsrc=)[^>]*>(?<body>.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex InlineScript();
}
