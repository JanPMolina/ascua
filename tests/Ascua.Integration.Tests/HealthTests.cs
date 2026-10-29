using System.Net;

namespace Ascua.Integration.Tests;

[Collection(ApiCollection.Name)]
public sealed class HealthTests(AscuaApiFactory factory)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/openapi/v1.json")]
    public async Task Endpoint_responds_ok(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_documents_auth_endpoints_and_bearer_scheme()
    {
        using var client = factory.CreateClient();

        var document = await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Contains("\"openapi\": \"3.1", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/login", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/users/me", document, StringComparison.Ordinal);
        Assert.Contains("\"bearerFormat\": \"JWT\"", document, StringComparison.Ordinal);
    }
}
