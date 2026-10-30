using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ascua.Integration.Tests;

/// <summary>
/// <c>contracts/openapi.json</c> es el contrato REST versionado: debe ser idéntico al documento que
/// genera la API. Si cambia un endpoint, esta prueba falla hasta que se regenere la copia.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OpenApiContractTests(AscuaApiFactory factory)
{
    private const string UpdateVariable = "ASCUA_UPDATE_OPENAPI";

    private static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        // Las descripciones están en español: tildes y eñes legibles en el archivo.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public async Task Versioned_openapi_matches_the_document_the_api_generates()
    {
        // El contrato es el de producción: sin los endpoints de desarrollo que la fábrica habilita.
        using var host = factory.WithSettings(new Dictionary<string, string?> { ["Ascua:Realtime:EnableDevEndpoints"] = "false" });
        using var client = host.CreateClient();
        var generated = Normalize(JsonNode.Parse(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative)))!);
        Assert.DoesNotContain("/api/v1/dev/", generated.ToJsonString(), StringComparison.Ordinal);

        var path = Path.Combine(RepositoryRoot(), "contracts", "openapi.json");
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(path, generated.ToJsonString(Output) + "\n");
            return;
        }

        var regenerate = $"Regenéralo con `{UpdateVariable}=1 dotnet test Ascua.slnx --filter {nameof(OpenApiContractTests)}` y revisa el diff.";
        Assert.True(File.Exists(path), $"Falta {path}. {regenerate}");
        var versioned = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var difference = FirstDifference(versioned, generated, "$");
        Assert.True(difference is null, $"contracts/openapi.json no coincide con el código en {difference}. {regenerate}");
    }

    /// <summary>Quita lo que depende de dónde corre la API (la URL del servidor), no del código.</summary>
    private static JsonNode Normalize(JsonNode document)
    {
        document.AsObject().Remove("servers");
        return document;
    }

    private static string? FirstDifference(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected, actual)
        {
            case (JsonObject e, JsonObject a):
                foreach (var name in e.Select(p => p.Key).Union(a.Select(p => p.Key), StringComparer.Ordinal))
                {
                    if (!e.ContainsKey(name) || !a.ContainsKey(name))
                    {
                        return $"{path}.{name} ({(e.ContainsKey(name) ? "sobra en la copia" : "falta en la copia")})";
                    }

                    if (FirstDifference(e[name], a[name], $"{path}.{name}") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            case (JsonArray e, JsonArray a):
                for (var i = 0; i < Math.Max(e.Count, a.Count); i++)
                {
                    if (i >= e.Count || i >= a.Count)
                    {
                        return $"{path}[{i}] (largo {e.Count} en la copia, {a.Count} en el código)";
                    }

                    if (FirstDifference(e[i], a[i], $"{path}[{i}]") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            default:
                return JsonNode.DeepEquals(expected, actual)
                    ? null
                    : $"{path} (copia: {expected?.ToJsonString() ?? "null"}, código: {actual?.ToJsonString() ?? "null"})";
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ascua.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No se encontró Ascua.slnx desde {AppContext.BaseDirectory}.");
    }
}
