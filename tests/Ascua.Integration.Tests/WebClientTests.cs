using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Ascua.Integration.Tests;

/// <summary>El cliente web de prueba (wwwroot) se sirve desde la propia API, en el mismo origen.</summary>
[Collection(ApiCollection.Name)]
public sealed partial class WebClientTests(AscuaApiFactory factory)
{
    [Fact]
    public async Task Root_serves_the_test_client()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<script type=\"module\" src=\"js/main.js\"></script>", html, StringComparison.Ordinal);
        // Punto de entrada para quien juega por primera vez: el tutorial no necesita cuenta.
        Assert.Contains("href=\"?tutorial=1\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/js/main.js", "text/javascript")]
    [InlineData("/js/match.js", "text/javascript")]
    [InlineData("/js/tutorial.js", "text/javascript")]
    [InlineData("/js/howto.js", "text/javascript")]
    [InlineData("/js/heroes.js", "text/javascript")]
    [InlineData("/js/settings-panel.js", "text/javascript")]
    [InlineData("/css/app.css", "text/css")]
    // Arte propio servido desde el mismo origen (la CSP no permite imágenes externas).
    [InlineData("/img/logo.svg", "image/svg+xml")]
    [InlineData("/img/favicon.svg", "image/svg+xml")]
    [InlineData("/img/heroes/errata.svg", "image/svg+xml")]
    public async Task Client_assets_are_served(string path, string mediaType)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Vendored_scripts_match_their_subresource_integrity_hashes()
    {
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        var scripts = VendorScript().Matches(html);
        Assert.Equal(2, scripts.Count);
        foreach (Match script in scripts)
        {
            var bytes = await client.GetByteArrayAsync(new Uri("/" + script.Groups["src"].Value, UriKind.Relative));
            var actual = "sha384-" + Convert.ToBase64String(SHA384.HashData(bytes));
            Assert.Equal(script.Groups["sri"].Value, actual);
        }
    }

    [GeneratedRegex("<script src=\"(?<src>vendor/[^\"]+)\" integrity=\"(?<sri>sha384-[^\"]+)\"")]
    private static partial Regex VendorScript();
}
