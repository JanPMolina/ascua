using System.Globalization;
using System.Text;
using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Rules;
using Ascua.Domain.Views;

namespace Ascua.Sim;

/// <summary>Resultado de una partida simulada: lo justo para las métricas de 05-heroes.md §14.</summary>
public sealed record GameRecord(
    IReadOnlyList<HeroClass> Heroes,
    OutcomeKind Outcome,
    DefeatReason Reason,
    int Rounds,
    IReadOnlyList<HeroClass> Winners,
    IReadOnlyList<HeroClass> Fallen,
    bool HadRenegade,
    IReadOnlyDictionary<HeroClass, HeroGameStats> Stats,
    int Rooms,
    IReadOnlyList<int> RoundsByRoom,
    IReadOnlyList<int> RoundsToClear)
{
    /// <summary>Lo que se mide aparte: empujones, tipos de sala, misiones y el frío.</summary>
    public V3Stats V3 { get; init; } = new();
}

/// <summary>Cuánto se usó lo nuevo en una partida.</summary>
public sealed class V3Stats
{
    public int Pushes { get; set; }
    public int Betrayals { get; set; }
    public Dictionary<RoomType, int> Rooms { get; } = [];
    public int PactsFulfilled { get; set; }

    /// <summary>Rondas en que golpeó el frío (07-version-4.md §7).</summary>
    public int Chills { get; set; }
}

/// <summary>Lo que hizo un héroe en una partida: sirve para ver si está roto o si no hace nada.</summary>
public sealed record HeroGameStats(int SkillUses, int Kills, int Embers, bool Fell, bool WentExtinct, int DamageToAllies);

/// <summary>Una partida completa jugada por bots contra el motor. Mismas semillas, misma partida.</summary>
public static class MatchSimulator
{
    /// <summary>Ninguna partida debería llegar aquí: el frío las termina mucho antes.</summary>
    public const int MaxRounds = 1000;

    public static GameRecord Play(IReadOnlyList<HeroClass> heroes, ulong seed, BotOptions options, Action<GameState, IReadOnlyDictionary<PlayerId, Order>>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(heroes);
        var engineRng = new SeededRandom(seed);
        var setups = heroes.Select((h, i) => new PlayerSetup(new PlayerId($"s{i}"), h)).ToList();
        var bots = setups.ToDictionary(s => s.Player, s => new HeroBot(s.Player, options, new SeededRandom(seed ^ (0x9E3779B97F4A7C15UL * (ulong)(s.Player.Value[1] + 1)))));
        var (state, _) = MatchFactory.Create(setups, engineRng);
        var stats = setups.ToDictionary(s => s.Player, _ => new MutableStats());
        var hadRenegade = false;
        var fallen = new HashSet<PlayerId>();
        var roundsByRoom = new int[state.Rooms.Count];
        var roundsToClear = new int[state.Rooms.Count];
        var v3 = new V3Stats();

        state = PickDrafts(state, bots);
        if (options.StartRoom > 0)
        {
            state = state with { RoomIndex = options.StartRoom, Enemies = state.Rooms[options.StartRoom].InitialEnemies };
        }

        var guard = 0;
        while (state.Phase == MatchPhase.InProgress)
        {
            if (++guard > MaxRounds)
            {
                throw new InvalidOperationException($"La partida con semilla {seed} no terminó en {MaxRounds} rondas: el frío debería haberla cerrado.");
            }

            state = PickDrafts(state, bots);
            foreach (var hero in state.Heroes.Where(h => h.IsChoosingPath).ToList())
            {
                var path = bots[hero.Player].ChoosePath();
                hadRenegade |= path == PathKind.Vengeance;
                state = MatchRules.ChoosePath(state, hero.Player, path).State;
            }

            var orders = DecideAll(state, bots);
            trace?.Invoke(state, orders);
            roundsByRoom[state.RoomIndex]++;
            var (next, events) = Resolver.Resolve(state, orders, engineRng);
            Tally(events, stats, fallen, v3);
            if (next.RoomIndex == state.RoomIndex && next.Enemies.IsEmpty && roundsToClear[state.RoomIndex] == 0)
            {
                roundsToClear[state.RoomIndex] = roundsByRoom[state.RoomIndex];
            }

            if (next.RoomIndex != state.RoomIndex && next.Phase == MatchPhase.InProgress)
            {
                var type = next.CurrentRoom.Type;
                v3.Rooms[type] = v3.Rooms.GetValueOrDefault(type) + 1;
            }

            state = next;
        }

        var outcome = state.Outcome!;
        v3.PactsFulfilled = outcome.Scores.Count(s => s.PactFulfilled);
        var classOf = setups.ToDictionary(s => s.Player, s => s.Class);
        return new GameRecord(
            [.. heroes],
            outcome.Kind,
            outcome.Reason,
            state.Round,
            [.. outcome.Winners.Select(w => classOf[w])],
            [.. fallen.Select(p => classOf[p])],
            hadRenegade,
            state.Heroes.ToDictionary(
                h => h.Class,
                h => new HeroGameStats(
                    stats[h.Player].SkillUses,
                    stats[h.Player].Kills,
                    Outcomes.FinalEmbers(h),
                    h.Progress.HasFallen,
                    h.Extinctions > 0,
                    stats[h.Player].DamageToAllies)),
            state.RoomIndex + 1,
            roundsByRoom,
            roundsToClear)
        {
            V3 = v3,
        };
    }

    private static GameState PickDrafts(GameState state, Dictionary<PlayerId, HeroBot> bots)
    {
        foreach (var hero in state.Heroes.Where(h => !h.DraftOptions.IsEmpty).ToList())
        {
            var view = PlayerViewProjector.For(state, hero.Player);
            state = MatchRules.ChooseBlessing(state, hero.Player, bots[hero.Player].ChooseBlessing(view.Self)).State;
        }

        foreach (var hero in state.Heroes.Where(h => !h.PactOptions.IsEmpty).ToList())
        {
            var view = PlayerViewProjector.For(state, hero.Player);
            state = MatchRules.ChoosePact(state, hero.Player, bots[hero.Player].ChoosePact(view.Self)).State;
        }

        return state;
    }

    /// <summary>
    /// Las órdenes de la ronda, como las daría cada jugador con su vista. El Campanero decide al final, oyendo a los
    /// demás; con el Repique, otros pueden no llegar a tiempo.
    /// </summary>
    private static Dictionary<PlayerId, Order> DecideAll(GameState state, Dictionary<PlayerId, HeroBot> bots)
    {
        var orders = new Dictionary<PlayerId, Order>();
        var listener = LastWord.Listener(state);
        var bell = state.IsBellRound ? state.BellRinger : null;
        foreach (var hero in state.Heroes.Where(h => h.CanAct && h.Player != listener?.Player))
        {
            var bot = bots[hero.Player];
            var order = bot.Decide(PlayerViewProjector.For(state, hero.Player));
            if (bell is not null && hero.Player != bell && bot.MissesTheBell())
            {
                continue;
            }

            orders[hero.Player] = order;
        }

        if (listener is not null)
        {
            var connected = state.Heroes.Select(h => h.Player).ToHashSet();
            var heard = LastWord.Heard(state, orders, connected);
            orders[listener.Player] = bots[listener.Player].Decide(PlayerViewProjector.For(state, listener.Player), heard);
        }

        return orders;
    }

    private static void Tally(IReadOnlyList<DomainEvent> events, Dictionary<PlayerId, MutableStats> stats, HashSet<PlayerId> fallen, V3Stats v3)
    {
        foreach (var domainEvent in events)
        {
            switch (domainEvent)
            {
                case SkillUsed used:
                    stats[used.Player].SkillUses++;
                    break;
                case HostBoarded boarded:
                    stats[boarded.Player].SkillUses++;
                    break;
                case EnemyKilled { Killer: { } killer }:
                    stats[killer].Kills++;
                    break;
                case HeroDowned downed:
                    fallen.Add(downed.Player);
                    break;
                case HeroExtinguished extinct:
                    fallen.Add(extinct.Player);
                    break;
                case BetrayalRecorded betrayal:
                    stats[betrayal.Traitor].DamageToAllies += betrayal.Damage;
                    v3.Betrayals++;
                    break;
                case Pushed:
                    v3.Pushes++;
                    break;
                case Chilled:
                    v3.Chills++;
                    break;
                case EntityDamaged { Source.Hero: { } source, Target.Hero: { } target } when source != target:
                    stats[source].DamageToAllies += domainEvent is EntityDamaged d ? d.Amount : 0;
                    break;
                default:
                    break;
            }
        }
    }

    private sealed class MutableStats
    {
        public int SkillUses { get; set; }

        public int Kills { get; set; }

        public int DamageToAllies { get; set; }
    }
}

/// <summary>Corridas sobre todas las combinaciones: reproducibles con semilla fija e independientes de los hilos.</summary>
public static class BalanceRun
{
    public static IEnumerable<HeroClass[]> Combinations(int size)
    {
        var heroes = Enum.GetValues<HeroClass>();
        var indexes = Enumerable.Range(0, size).ToArray();
        while (true)
        {
            yield return [.. indexes.Select(i => heroes[i])];
            var position = size - 1;
            while (position >= 0 && indexes[position] == heroes.Length - size + position)
            {
                position--;
            }

            if (position < 0)
            {
                yield break;
            }

            indexes[position]++;
            for (var i = position + 1; i < size; i++)
            {
                indexes[i] = indexes[i - 1] + 1;
            }
        }
    }

    /// <summary>Juega <paramref name="gamesPerCombo"/> partidas de cada combinación de cada tamaño, rotando los asientos.</summary>
    public static IReadOnlyList<GameRecord> Run(int[] sizes, int gamesPerCombo, ulong baseSeed, BotOptions options, int? parallelism = null)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        var work = new List<(HeroClass[] Heroes, ulong Seed)>();
        foreach (var size in sizes)
        {
            var comboIndex = 0UL;
            foreach (var combo in Combinations(size))
            {
                for (var game = 0; game < gamesPerCombo; game++)
                {
                    var rotation = game % size;
                    var seated = combo.Skip(rotation).Concat(combo.Take(rotation)).ToArray();
                    var seed = baseSeed + ((ulong)size * 1_000_000_000UL) + (comboIndex * 100_000UL) + (ulong)game;
                    work.Add((seated, seed));
                }

                comboIndex++;
            }
        }

        var records = new GameRecord[work.Count];
        Parallel.For(
            0,
            work.Count,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism ?? Environment.ProcessorCount },
            i => records[i] = MatchSimulator.Play(work[i].Heroes, work[i].Seed, options));
        return records;
    }
}

/// <summary>Las métricas de 05-heroes.md §14 sobre un conjunto de partidas.</summary>
public sealed class BalanceReport
{
    private readonly IReadOnlyList<GameRecord> _games;

    public BalanceReport(IReadOnlyList<GameRecord> games)
    {
        ArgumentNullException.ThrowIfNull(games);
        _games = games;
    }

    public double WardenFallRate(int? players = null) =>
        Rate(Of(players), g => g.Outcome is OutcomeKind.Victory or OutcomeKind.Draw);

    public double AverageRounds(int? players = null)
    {
        var games = Of(players);
        return games.Count == 0 ? 0 : games.Average(g => (double)g.Rounds);
    }

    /// <summary>Héroes que terminan caídos o extintos al menos una vez, sobre todos los héroes jugados.</summary>
    public double FallenRate() =>
        (double)_games.Sum(g => g.Fallen.Count) / _games.Sum(g => g.Heroes.Count);

    public double ThresholdRate()
    {
        var renegades = _games.Where(g => g.HadRenegade).ToList();
        return renegades.Count == 0 ? 0 : Rate(renegades, g => g.Outcome == OutcomeKind.ThresholdClaimed);
    }

    public int RenegadeGames => _games.Count(g => g.HadRenegade);

    /// <summary>Tasa de victoria en 4 jugadores: partes de victoria sobre partidas con ganador (azar = 25%).</summary>
    public double WinRate(HeroClass hero) => WinRate(hero, 4);

    /// <summary>Tasa de victoria con tantos jugadores (azar = 1 / jugadores).</summary>
    public double WinRate(HeroClass hero, int players)
    {
        var decided = _games.Where(g => g.Heroes.Count == players && g.Heroes.Contains(hero) && g.Winners.Count > 0).ToList();
        return decided.Count == 0 ? 0 : decided.Sum(g => g.Winners.Contains(hero) ? 1.0 / g.Winners.Count : 0) / decided.Count;
    }

    /// <summary>Partidas que llegaron a la ronda del frío.</summary>
    public double ChillRate(int? players = null) => Rate(Of(players), g => g.Rounds >= GameState.ChillStartRound);

    public double FallenRate(int players)
    {
        var games = Of(players);
        return games.Count == 0 ? 0 : (double)games.Sum(g => g.Fallen.Count) / games.Sum(g => g.Heroes.Count);
    }

    public int MaxRounds() => _games.Count == 0 ? 0 : _games.Max(g => g.Rounds);

    private string BySize(Func<int?, double> metric, string format)
    {
        var sizes = _games.Select(g => g.Heroes.Count).Distinct().Order();
        return string.Join("; ", sizes.Select(s => $"{s} jug.: {metric(s).ToString(format, CultureInfo.InvariantCulture)}"));
    }

    /// <summary>Con este héroe en el cuarteto, ¿cuánto cae el Celador comparado con el total?</summary>
    public double WardenFallWith(HeroClass hero) =>
        Rate(_games.Where(g => g.Heroes.Count == 4 && g.Heroes.Contains(hero)).ToList(), g => g.Outcome is OutcomeKind.Victory or OutcomeKind.Draw);

    public double AverageSkillUses(HeroClass hero) => AverageOf(hero, s => s.SkillUses);

    public double AverageKills(HeroClass hero) => AverageOf(hero, s => s.Kills);

    public double AverageEmbers(HeroClass hero) => AverageOf(hero, s => s.Embers);

    public double AverageDamageToAllies(HeroClass hero) => AverageOf(hero, s => s.DamageToAllies);

    public double FellRate(HeroClass hero)
    {
        var games = _games.Where(g => g.Heroes.Count == 4 && g.Heroes.Contains(hero)).ToList();
        return games.Count == 0 ? 0 : (double)games.Count(g => g.Stats[hero].Fell || g.Stats[hero].WentExtinct) / games.Count;
    }

    /// <summary>
    /// El peor dúo: cuántas veces la tasa esperada gana uno de los dos (esperada: 50% en un cuarteto) y cuánto cambia
    /// la caída del Celador con esa pareja.
    /// </summary>
    public IReadOnlyList<DuoResult> Duos()
    {
        var heroes = Enum.GetValues<HeroClass>();
        var quartets = _games.Where(g => g.Heroes.Count == 4).ToList();
        var overallFall = Rate(quartets, g => g.Winners.Count > 0);
        var results = new List<DuoResult>();
        for (var i = 0; i < heroes.Length; i++)
        {
            for (var j = i + 1; j < heroes.Length; j++)
            {
                var (a, b) = (heroes[i], heroes[j]);
                var with = quartets.Where(g => g.Heroes.Contains(a) && g.Heroes.Contains(b)).ToList();
                var decided = with.Where(g => g.Winners.Count > 0).ToList();
                var share = decided.Count == 0
                    ? 0
                    : decided.Sum(g => (double)g.Winners.Count(w => w == a || w == b) / g.Winners.Count) / decided.Count;
                var fall = Rate(with, g => g.Winners.Count > 0);
                results.Add(new DuoResult(a, b, share / 0.5, overallFall > 0 ? fall / overallFall : 0));
            }
        }

        return results;
    }

    public string ToMarkdown(string title, int gamesPerCombo, ulong seed, BotOptions options, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(options);
        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;
        text.AppendLine(culture, $"### {title}");
        text.AppendLine();
        text.AppendLine(culture, $"{_games.Count} partidas ({gamesPerCombo} por combinación, semilla {seed}, traición {options.BetrayalPercent}%, venganza {options.VengeancePercent}%, Repique perdido {options.BellMissPercent}%), en {elapsed.TotalSeconds:F0} s.");
        text.AppendLine();
        text.AppendLine("| Métrica | Meta | Resultado |");
        text.AppendLine("|---|---|---|");
        text.AppendLine(culture, $"| Partidas en que cae el Celador | 35%–65% | **{WardenFallRate():P1}** ({BySize(p => WardenFallRate(p), "P1")}) |");
        var heroes = Enum.GetValues<HeroClass>();
        text.AppendLine(culture, $"| Tasa de victoria por héroe (4 jug.; azar = 25%) | 18%–32% | **{heroes.Min(WinRate):P1}**–**{heroes.Max(WinRate):P1}** |");
        var duos = Duos();
        var worst = duos.OrderByDescending(d => d.WinRatio).First();
        var worstFall = duos.OrderByDescending(d => d.WardenFallRatio).First();
        text.AppendLine(culture, $"| Ningún dúo por encima de 1,5× lo esperado | <1,5× | victoria: **{worst.WinRatio:F2}×** ({worst.A}+{worst.B}); caída del Celador: **{worstFall.WardenFallRatio:F2}×** ({worstFall.A}+{worstFall.B}) |");
        text.AppendLine(culture, $"| Rondas por partida (informativa) | 25–55 | **{AverageRounds():F1}** ({BySize(p => AverageRounds(p), "F1")}); la más larga: {MaxRounds()} |");
        text.AppendLine(culture, $"| Héroes caídos o extintos al menos una vez | 30%–70% | **{FallenRate():P1}** ({BySize(p => FallenRate(p!.Value), "P1")}) |");
        text.AppendLine(culture, $"| Partidas con Renegado en que gana «El Umbral reclama» | <25% | **{ThresholdRate():P1}** (de {RenegadeGames} partidas con Renegado) |");
        text.AppendLine();
        text.AppendLine("| Héroe | Victoria | Celador cae con él | Cae o se extingue | Habilidad/partida | Golpes finales | Ascuas | Daño a aliados |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var hero in heroes.OrderByDescending(WinRate))
        {
            text.AppendLine(culture, $"| {hero} | {WinRate(hero):P1} | {WardenFallWith(hero):P1} | {FellRate(hero):P1} | {AverageSkillUses(hero):F1} | {AverageKills(hero):F1} | {AverageEmbers(hero):F1} | {AverageDamageToAllies(hero):F1} |");
        }

        text.AppendLine();
        var fives = heroes.Select(h => WinRate(h, 5)).Where(r => r > 0).ToList();
        if (fives.Count > 0)
        {
            text.AppendLine(culture, $"Victoria por héroe con 5 jugadores (azar = 20%): {fives.Min():P1}–{fives.Max():P1}.");
            text.AppendLine();
        }

        var outcomes = _games.GroupBy(g => g.Outcome.ToString()).OrderBy(g => g.Key);
        text.AppendLine(culture, $"Resultados: {string.Join(", ", outcomes.Select(g => $"{g.Key} {(double)g.Count() / _games.Count:P1}"))}. Salas alcanzadas (media): {_games.Average(g => (double)g.Rooms):F1}.");
        var rooms = _games.Max(g => g.RoundsByRoom.Count);
        var perRoom = Enumerable.Range(0, rooms).Select(i =>
        {
            var reached = _games.Where(g => g.Rooms > i).ToList();
            return reached.Count == 0 ? "-" : $"{reached.Average(g => (double)g.RoundsByRoom[i]):F1}";
        });
        var toClear = Enumerable.Range(0, rooms).Select(i =>
        {
            var cleared = _games.Where(g => g.RoundsToClear[i] > 0).ToList();
            return cleared.Count == 0 ? "-" : $"{cleared.Average(g => (double)g.RoundsToClear[i]):F1}";
        });
        text.AppendLine(culture, $"Rondas por sala (si se llega): {string.Join(" / ", perRoom)}; hasta despejarla: {string.Join(" / ", toClear)}.");
        text.AppendLine(culture, $"Por partida: {_games.Average(g => g.V3.Pushes):F2} empujones, {_games.Average(g => g.V3.Betrayals):F2} traiciones anónimas, {_games.Average(g => g.V3.PactsFulfilled):F2} misiones cumplidas.");
        text.AppendLine(culture, $"Llegan al frío (ronda {GameState.ChillStartRound}): {ChillRate():P1} ({BySize(p => ChillRate(p), "P1")}).");
        var doors = _games.SelectMany(g => g.V3.Rooms).GroupBy(r => r.Key).OrderBy(r => r.Key);
        text.AppendLine(culture, $"Tipos de sala al entrar: {string.Join(", ", doors.Select(d => $"{d.Key} {d.Sum(r => r.Value)}"))}.");
        return text.ToString();
    }

    private IReadOnlyList<GameRecord> Of(int? players) =>
        players is { } count ? [.. _games.Where(g => g.Heroes.Count == count)] : _games;

    private double AverageOf(HeroClass hero, Func<HeroGameStats, int> pick)
    {
        var games = _games.Where(g => g.Heroes.Count == 4 && g.Heroes.Contains(hero)).ToList();
        return games.Count == 0 ? 0 : games.Average(g => (double)pick(g.Stats[hero]));
    }

    private static double Rate(IReadOnlyList<GameRecord> games, Func<GameRecord, bool> predicate) =>
        games.Count == 0 ? 0 : (double)games.Count(predicate) / games.Count;
}

/// <summary>Un dúo: <see cref="WinRatio"/> veces lo esperado de victoria y <see cref="WardenFallRatio"/> veces la caída media del Celador.</summary>
public sealed record DuoResult(HeroClass A, HeroClass B, double WinRatio, double WardenFallRatio);
