using System.Globalization;

namespace Ascua.LoadTests;

/// <summary>
/// Parámetros (todos con <c>--nombre valor</c>):
/// <list type="bullet">
/// <item><c>--url</c> base de la API (por defecto http://localhost:8080).</item>
/// <item><c>--stages</c> partidas simultáneas por etapa, separadas por coma (p. ej. <c>10,20,40</c>). Una sola = carga constante.</item>
/// <item><c>--stage-seconds</c> duración de cada etapa.</item>
/// <item><c>--idle-seconds</c> pausa sin partidas antes de la primera etapa (línea base de CPU/RAM).</item>
/// <item><c>--think-ms</c> tiempo máximo de "pensar" de cada bot (0..think-ms, por defecto 2000).</item>
/// <item><c>--skip-rate</c> probabilidad de que un bot no envíe orden en una ronda (fuerza cierres por plazo).</item>
/// <item><c>--clock</c> <c>sync</c> (mismo host, por defecto) o <c>estimate</c> (corrige el desfase con los ACK).</item>
/// <item><c>--out</c> carpeta de resultados; <c>--stats</c> CSV de <c>docker stats</c>; <c>--label</c> nombre de la corrida.</item>
/// </list>
/// </summary>
public sealed class LoadOptions
{
    public Uri BaseUrl { get; init; } = new("http://localhost:8080");

    public IReadOnlyList<int> Stages { get; init; } = [10];

    public TimeSpan StageDuration { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan IdleDuration { get; init; } = TimeSpan.FromSeconds(15);

    public int ThinkMaxMs { get; init; } = 2000;

    public double SkipRate { get; init; }

    public bool EstimateClockOffset { get; init; }

    public string OutputDirectory { get; init; } = "reports/local";

    public string? StatsFile { get; init; }

    public string Label { get; init; } = "carga";

    public TimeSpan MatchTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Segundos del inicio de cada etapa que no cuentan (transición mientras arrancan las partidas nuevas).</summary>
    public TimeSpan StageWarmup { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxMatches => Stages.Max();

    public static LoadOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Argumento inesperado: {args[i]}");
            }

            values[args[i][2..]] = args[i + 1];
        }

        var url = values.GetValueOrDefault("url", "http://localhost:8080");
        return new LoadOptions
        {
            BaseUrl = new Uri(url.EndsWith('/') ? url : url + "/"),
            Stages = [.. values.GetValueOrDefault("stages", "10").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Int)],
            StageDuration = TimeSpan.FromSeconds(Int(values.GetValueOrDefault("stage-seconds", "60"))),
            IdleDuration = TimeSpan.FromSeconds(Int(values.GetValueOrDefault("idle-seconds", "15"))),
            ThinkMaxMs = Int(values.GetValueOrDefault("think-ms", "2000")),
            SkipRate = double.Parse(values.GetValueOrDefault("skip-rate", "0"), CultureInfo.InvariantCulture),
            EstimateClockOffset = values.GetValueOrDefault("clock", "sync") == "estimate",
            OutputDirectory = values.GetValueOrDefault("out", "reports/local"),
            StatsFile = values.GetValueOrDefault("stats"),
            Label = values.GetValueOrDefault("label", "carga"),
        };
    }

    private static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);
}
