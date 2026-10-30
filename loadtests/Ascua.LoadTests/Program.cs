using System.Collections.Concurrent;
using Ascua.LoadTests;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

// Prueba de carga de Ascua (docs/rendimiento.md). N partidas simultáneas de 4 bots por MessagePack;
// cada etapa de --stages mantiene esa cantidad de partidas (al terminar una, el mismo "copy" lanza otra).
// Uso: dotnet run --project loadtests/Ascua.LoadTests -- --url http://localhost:8080 --stages 10,20,40 --stage-seconds 60
var options = LoadOptions.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);

using var http = new HttpClient { BaseAddress = options.BaseUrl, Timeout = TimeSpan.FromSeconds(60) };
var api = new AscuaApi(http);
var recorder = new Recorder();

// Cada partida toma 4 usuarios libres y los devuelve al terminar: un usuario nunca está en dos partidas a la vez.
var freeUsers = new ConcurrentQueue<BotUser[]>();
var started = DateTime.UtcNow;
using var sampling = new CancellationTokenSource();
Task? sampler = null;

async Task<BotUser[]> RegisterGroupAsync(CancellationToken cancellationToken)
{
    var group = new BotUser[4];
    for (var i = 0; i < group.Length; i++)
    {
        group[i] = await api.RegisterAsync("lt_", cancellationToken);
    }

    return group;
}

var scenario = Scenario.Create("partida", async context =>
    {
        if (!freeUsers.TryDequeue(out var group))
        {
            group = await RegisterGroupAsync(context.ScenarioCancellationToken);
        }

        try
        {
            var session = new MatchSession(api, options, recorder, group);
            return await session.PlayAsync(context.ScenarioCancellationToken)
                ? Response.Ok()
                : Response.Fail(message: "no se pudo arrancar la partida");
        }
        finally
        {
            freeUsers.Enqueue(group);
        }
    })
    .WithInit(async _ =>
    {
        await api.WaitUntilReadyAsync(CancellationToken.None);
        Console.WriteLine($"Registrando {options.MaxMatches * 4} bots en {options.BaseUrl}…");

        // Registro con poca concurrencia: cada alta y login cuesta un Argon2id en el servidor.
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(Enumerable.Range(0, options.MaxMatches).Select(async _ =>
        {
            await gate.WaitAsync();
            try
            {
                freeUsers.Enqueue(await RegisterGroupAsync(CancellationToken.None));
            }
            finally
            {
                gate.Release();
            }
        }));

        started = DateTime.UtcNow;
        sampler = recorder.SampleActiveAsync(sampling.Token);
        Console.WriteLine($"Bots listos; empieza la carga a las {started:HH:mm:ss} UTC");
    })
    .WithoutWarmUp()
    .WithMaxFailCount(int.MaxValue)
    .WithLoadSimulations([
        Simulation.Pause(options.IdleDuration),
        .. options.Stages.Select(copies => Simulation.KeepConstant(copies, options.StageDuration)),
    ]);

NBomberRunner.RegisterScenarios(scenario)
    .WithTestSuite("ascua")
    .WithTestName(options.Label)
    .WithReportFolder(Path.Combine(options.OutputDirectory, "nbomber"))
    .WithReportFormats(ReportFormat.Html, ReportFormat.Md)
    .Run();

await sampling.CancelAsync();
if (sampler is not null)
{
    await sampler;
}

var report = LoadReport.Build(options, recorder, started);
report.Write(options.OutputDirectory);
Console.WriteLine(report.Markdown);
return report.MeetsGoal ? 0 : 2;
