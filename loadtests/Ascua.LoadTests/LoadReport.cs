using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ascua.LoadTests;

/// <summary>Resultado de una etapa (todas las cifras en el intervalo de la etapa sin su transición inicial).</summary>
public sealed record StageResult(
    string Name,
    int Matches,
    double AvgActiveMatches,
    int Rounds,
    int RoundsByDeadline,
    double P50,
    double P95,
    double P99,
    double Max,
    double OrderRttP95,
    long OrdersSent,
    long OrdersAccepted,
    long Stale,
    long OrderErrors,
    long Duplicates,
    long SeqGaps,
    long LostOrders,
    long MissingBroadcasts,
    long SetupErrors,
    double ErrorRate,
    double? CpuPercent,
    double? MemoryMiB,
    double? CpuPerMatch,
    double? MemoryPerMatchMiB)
{
    public bool MeetsGoal => Rounds > 0 && P95 < 200 && ErrorRate < 0.01 && LostOrders == 0 && Duplicates == 0 && SeqGaps == 0;
}

public sealed record LoadReport(string Label, string BaseUrl, DateTime StartedAt, double? ClockOffsetMedianMs, IReadOnlyList<StageResult> Stages, string? Breakpoint)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>La primera etapa con partidas cumple la meta (lo que exige el humo de CI).</summary>
    public bool MeetsGoal => Stages.FirstOrDefault(s => s.Matches > 0)?.MeetsGoal ?? false;

    public string Markdown => ToMarkdown();

    public static LoadReport Build(LoadOptions options, Recorder recorder, DateTime startedAt)
    {
        var samples = recorder.Samples.ToList();
        var stats = options.StatsFile is { } file && File.Exists(file) ? DockerStats.Read(file) : [];
        var idleEnd = startedAt + options.IdleDuration;

        // Línea base: la pausa sin partidas (sin sus primeros 5 s).
        var idle = Window(stats, startedAt.AddSeconds(5), idleEnd);
        var idleCpu = idle.Count > 0 ? idle.Average(s => s.CpuPercent) : (double?)null;
        var idleMem = idle.Count > 0 ? idle.Average(s => s.MemoryMiB) : (double?)null;

        var stages = new List<StageResult>();
        if (options.IdleDuration > TimeSpan.Zero && idleCpu is not null)
        {
            stages.Add(new StageResult("reposo", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, idleCpu, idleMem, null, null));
        }

        for (var i = 0; i < options.Stages.Count; i++)
        {
            var from = idleEnd + (options.StageDuration * i) + options.StageWarmup;
            var to = idleEnd + (options.StageDuration * (i + 1));
            var inStage = samples.Where(s => s.At >= from && s.At < to).ToList();
            long Count(Kind kind) => (long)inStage.Where(s => s.Kind == kind).Sum(s => s.Value);

            var latencies = inStage.Where(s => s.Kind is Kind.RoundLatency or Kind.RoundLatencyDeadline).Select(s => s.Value).Order().ToArray();
            var rtts = inStage.Where(s => s.Kind == Kind.OrderRtt).Select(s => s.Value).Order().ToArray();
            var active = recorder.ActiveMatches.Where(a => a.At >= from && a.At < to).Select(a => (double)a.Active).DefaultIfEmpty(0).Average();

            var errors = Count(Kind.OrderError) + Count(Kind.Duplicate) + Count(Kind.SeqGap) + Count(Kind.LostOrder)
                + Count(Kind.MissingBroadcast) + Count(Kind.SetupError) + Count(Kind.OrderStale);
            var operations = Count(Kind.OrderSent) + (Count(Kind.RoundCompleted) * 4) + Count(Kind.MissingBroadcast) + Count(Kind.MatchStarted) + Count(Kind.SetupError);

            var window = Window(stats, from, to);
            double? cpu = window.Count > 0 ? window.Average(s => s.CpuPercent) : null;
            double? mem = window.Count > 0 ? window.Average(s => s.MemoryMiB) : null;
            stages.Add(new StageResult(
                $"{options.Stages[i]} partidas",
                options.Stages[i],
                Math.Round(active, 1),
                latencies.Length,
                inStage.Count(s => s.Kind == Kind.RoundLatencyDeadline),
                Percentile(latencies, 0.50),
                Percentile(latencies, 0.95),
                Percentile(latencies, 0.99),
                latencies.Length > 0 ? Math.Round(latencies[^1], 1) : 0,
                Percentile(rtts, 0.95),
                Count(Kind.OrderSent),
                Count(Kind.OrderAccepted),
                Count(Kind.OrderStale),
                Count(Kind.OrderError),
                Count(Kind.Duplicate),
                Count(Kind.SeqGap),
                Count(Kind.LostOrder),
                Count(Kind.MissingBroadcast),
                Count(Kind.SetupError),
                operations > 0 ? (double)errors / operations : 0,
                Round(cpu),
                Round(mem),
                cpu is not null && active > 0 ? Math.Round((cpu.Value - (idleCpu ?? 0)) / active, 2) : null,
                // Memoria: cota superior (todo el proceso / partidas). El delta contra el reposo no sirve: el GC
                // sigue liberando lo que dejó el registro de bots y la resta da negativa.
                mem is not null && active > 0 ? Math.Round(mem.Value / active, 2) : null));
        }

        var loaded = stages.Where(s => s.Matches > 0).ToList();
        var firstBad = loaded.FindIndex(s => !s.MeetsGoal);
        string? breakpoint = firstBad switch
        {
            < 0 => null,
            0 => $"ya la primera etapa ({loaded[0].Matches} partidas) incumple la meta",
            _ => $"soporta {loaded[firstBad - 1].Matches} partidas; se degrada con {loaded[firstBad].Matches}",
        };

        var offsets = samples.Where(s => s.Kind == Kind.ClockOffset).Select(s => s.Value).Order().ToArray();
        return new LoadReport(options.Label, options.BaseUrl.ToString(), startedAt, offsets.Length > 0 ? Percentile(offsets, 0.5) : null, stages, breakpoint);
    }

    public void Write(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "resultados.json"), JsonSerializer.Serialize(this, Json));
        File.WriteAllText(Path.Combine(directory, "resumen.md"), Markdown);
    }

    private string ToMarkdown()
    {
        var md = new StringBuilder();
        md.AppendLine(CultureInfo.InvariantCulture, $"# Carga: {Label}");
        md.AppendLine();
        md.AppendLine(CultureInfo.InvariantCulture, $"- Destino: `{BaseUrl}` — inicio {StartedAt:yyyy-MM-dd HH:mm:ss} UTC");
        md.AppendLine(CultureInfo.InvariantCulture, $"- Desfase de reloj servidor−cliente estimado (mediana): {(ClockOffsetMedianMs is { } o ? $"{o:0.0} ms" : "n/d")}");
        md.AppendLine(CultureInfo.InvariantCulture, $"- Punto de quiebre: {Breakpoint ?? "no se alcanzó en las etapas probadas"}");
        md.AppendLine();
        md.AppendLine("| Etapa | Activas | Rondas (por plazo) | p50 ms | p95 ms | p99 ms | máx ms | RTT orden p95 | Órdenes env/ACK | Tardías | Errores | Huecos seq | Perdidas | Dup. | Sin difusión | Tasa error | CPU % | RAM MiB | CPU %/partida | MiB/partida (cota) | Meta |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var s in Stages)
        {
            md.AppendLine(CultureInfo.InvariantCulture,
                $"| {s.Name} | {s.AvgActiveMatches} | {s.Rounds} ({s.RoundsByDeadline}) | {s.P50} | {s.P95} | {s.P99} | {s.Max} | {s.OrderRttP95} | {s.OrdersSent}/{s.OrdersAccepted} | {s.Stale} | {s.OrderErrors + s.SetupErrors} | {s.SeqGaps} | {s.LostOrders} | {s.Duplicates} | {s.MissingBroadcasts} | {s.ErrorRate:P2} | {N(s.CpuPercent)} | {N(s.MemoryMiB)} | {N(s.CpuPerMatch)} | {N(s.MemoryPerMatchMiB)} | {(s.Matches == 0 ? "—" : s.MeetsGoal ? "✅" : "❌")} |");
        }

        return md.ToString();
    }

    private static string N(double? value) => value is { } v ? v.ToString("0.##", CultureInfo.InvariantCulture) : "n/d";

    private static double? Round(double? value) => value is { } v ? Math.Round(v, 2) : null;

    private static List<DockerStats> Window(IReadOnlyList<DockerStats> stats, DateTime from, DateTime to) =>
        [.. stats.Where(s => s.At >= from && s.At < to)];

    /// <summary>Percentil por rango más cercano.</summary>
    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return Math.Round(sorted[index], 1);
    }
}

/// <summary>Muestra de <c>docker stats</c>: línea <c>epochMs,CPU%,uso / límite</c> (la escriben loadtests/run.*).</summary>
public sealed record DockerStats(DateTime At, double CpuPercent, double MemoryMiB)
{
    public static IReadOnlyList<DockerStats> Read(string path)
    {
        var result = new List<DockerStats>();
        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split(',');
            if (parts.Length < 3 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var epochMs))
            {
                continue;
            }

            if (double.TryParse(parts[1].TrimEnd('%'), CultureInfo.InvariantCulture, out var cpu) && Memory(parts[2]) is { } mem)
            {
                result.Add(new DockerStats(DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime, cpu, mem));
            }
        }

        return result;
    }

    /// <summary>"312.5MiB / 1GiB" → 312.5.</summary>
    private static double? Memory(string usage)
    {
        var used = usage.Split('/')[0].Trim();
        (string Unit, double Factor)[] units = [("GiB", 1024), ("MiB", 1), ("KiB", 1.0 / 1024), ("GB", 953.674), ("MB", 0.953674), ("kB", 0.000953674), ("B", 1.0 / (1024 * 1024))];
        foreach (var (unit, factor) in units)
        {
            if (used.EndsWith(unit, StringComparison.Ordinal)
                && double.TryParse(used[..^unit.Length], CultureInfo.InvariantCulture, out var value))
            {
                return value * factor;
            }
        }

        return null;
    }
}
