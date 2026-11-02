using System.Diagnostics;
using System.Globalization;
using Ascua.Sim;

// Simulador de balance (planteamiento/05-heroes.md §14): juega partidas completas directo contra el motor, con bots que
// deciden solo con su vista, sobre todas las combinaciones de 2, 3 y 4 jugadores. Reproducible con semilla fija.
// Uso: dotnet run -c Release --project tools/Ascua.Sim -- [--games 200] [--seed 20261102] [--betrayal 10]
//      [--vengeance 30] [--bell-miss 20] [--sizes 2,3,4] [--title "Corrida"] [--out archivo.md]
var games = 200;
var seed = 20261102UL;
var betrayal = 10;
var vengeance = 30;
var bellMiss = 20;
var sizes = new[] { 2, 3, 4 };
var title = "Corrida";
string? output = null;
string? traceHeroes = null;

for (var i = 0; i + 1 < args.Length; i += 2)
{
    var value = args[i + 1];
    switch (args[i])
    {
        case "--games":
            games = int.Parse(value, CultureInfo.InvariantCulture);
            break;
        case "--seed":
            seed = ulong.Parse(value, CultureInfo.InvariantCulture);
            break;
        case "--betrayal":
            betrayal = int.Parse(value, CultureInfo.InvariantCulture);
            break;
        case "--vengeance":
            vengeance = int.Parse(value, CultureInfo.InvariantCulture);
            break;
        case "--bell-miss":
            bellMiss = int.Parse(value, CultureInfo.InvariantCulture);
            break;
        case "--sizes":
            sizes = [.. value.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
            break;
        case "--title":
            title = value;
            break;
        case "--out":
            output = value;
            break;
        case "--trace":
            traceHeroes = value;
            break;
        default:
            Console.Error.WriteLine($"Opción desconocida: {args[i]}");
            return 2;
    }
}

var options = new BotOptions(betrayal, vengeance, bellMiss);
if (traceHeroes is not null)
{
    // Una sola partida, ronda a ronda, para depurar a los bots: --trace Polizon,Avaro --seed 7
    var heroes = traceHeroes.Split(',').Select(Enum.Parse<Ascua.Domain.Model.HeroClass>).ToList();
    var record = MatchSimulator.Play(heroes, seed, options, (state, orders) =>
    {
        var line = string.Join(" | ", state.Heroes.Select(h =>
            $"{h.Class} {h.Status} {h.Position.X},{h.Position.Y} hp{h.Hp} e{h.Embers} {(orders.TryGetValue(h.Player, out var o) ? $"{o.Type} {o.Direction}" : "-")}"));
        var enemies = string.Join(" ", state.Enemies.Select(e => $"{e.Kind}@{e.Position.X},{e.Position.Y}/{e.Hp}"));
        Console.WriteLine($"R{state.Round} S{state.RoomIndex}: {line} || {enemies}");
    });
    Console.WriteLine(record);
    return 0;
}

var clock = Stopwatch.StartNew();
var records = BalanceRun.Run(sizes, games, seed, options);
var markdown = new BalanceReport(records).ToMarkdown(title, games, seed, options, clock.Elapsed);
Console.WriteLine(markdown);
if (output is not null)
{
    await File.WriteAllTextAsync(output, markdown);
}

return 0;
