using Ascua.Domain.Abstractions;
using Ascua.Domain.Model;
using Ascua.Domain.Views;

namespace Ascua.Sim;

/// <summary>Parámetros de cómo juegan los bots.</summary>
/// <param name="BetrayalPercent">Probabilidad de golpear a un aliado cuando se puede (fuego amigo o anónimo).</param>
/// <param name="VengeancePercent">Probabilidad de elegir el Camino de la Venganza al extinguirse.</param>
/// <param name="BellMissPercent">Con el Repique, probabilidad de que otro jugador no decida a tiempo (guarda).</param>
public sealed record BotOptions(int BetrayalPercent = 10, int VengeancePercent = 30, int BellMissPercent = 20);

/// <summary>
/// Un jugador simulado. Decide solo con su <see cref="PlayerView"/> (más lo que recuerda de lo que vio en esta sala):
/// esquiva lo anunciado, ataca en línea, abre cofres, va a la escalera y usa su habilidad cuando se da su situación.
/// No miente ni hace faroles: detecta héroes rotos y errores, no reemplaza partidas entre personas (05-heroes.md §14).
/// </summary>
public sealed class HeroBot(PlayerId player, BotOptions options, IRandom rng)
{
    private readonly Dictionary<Position, Terrain> _known = [];
    private readonly Dictionary<int, Position> _chests = [];
    private readonly Dictionary<Position, int> _lastSeen = [];
    private readonly HashSet<Position> _openedChests = [];
    private readonly Dictionary<Position, int> _bumped = [];
    private Order? _lastOrder;
    private List<Position> _lastBodies = [];
    private readonly List<Position> _trail = [];
    private bool _triedStairs;
    private int _room = -1;

    // Foto de la ronda que se está decidiendo.
    private PlayerView _view = null!;
    private SelfView _self = null!;
    private List<Position> _bodies = [];
    private HashSet<Position> _danger = [];
    private Dictionary<Position, EnemyView> _enemies = [];
    private Dictionary<Position, AllyView> _others = [];

    public PlayerId Player { get; } = player;

    /// <summary>Elige camino al extinguirse: casi siempre Amistad; la Venganza, con la probabilidad configurada.</summary>
    public PathKind ChoosePath() => Roll(options.VengeancePercent) ? PathKind.Vengeance : PathKind.Friendship;

    /// <summary>Con el Repique sonando, ¿este bot no llega a decidir a tiempo?</summary>
    public bool MissesTheBell() => Roll(options.BellMissPercent);

    /// <summary>Elige una bendición del draft: la primera que le sirve más a su héroe.</summary>
    public int ChooseBlessing(SelfView self)
    {
        ArgumentNullException.ThrowIfNull(self);
        var options = self.DraftOptions;
        for (var i = 0; i < options.Count; i++)
        {
            // La Lámpara es casi lo único que cambia cómo juega la Escucha.
            if (self.Class == HeroClass.Escucha && options[i] == Blessing.Lamp)
            {
                return i;
            }

            if (self.Class == HeroClass.Avaro && options[i] == Blessing.DoubleHeart)
            {
                return i;
            }
        }

        return rng.Next(options.Count);
    }

    /// <summary>La orden de esta ronda. <paramref name="heard"/>: lo que oye el Campanero si es el último en hablar.</summary>
    public Order Decide(PlayerView view, IReadOnlyList<(PlayerId Player, Order Order)>? heard = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        Observe(view);
        var order = _self.Status switch
        {
            HeroStatus.EmberSpirit => DecideEmber(),
            HeroStatus.Renegade => _self.HostEnemyId is null ? DecideRenegade() : DecideInHost(),
            _ when _self.HostEnemyId is not null => DecideInHost(),
            _ => DecideHero(heard),
        };
        _lastOrder = order;
        _lastBodies = _bodies;
        return order;
    }

    private void Observe(PlayerView view)
    {
        _view = view;
        _self = view.Self;
        if (_room != view.RoomIndex)
        {
            _room = view.RoomIndex;
            _known.Clear();
            _chests.Clear();
            _lastSeen.Clear();
            _openedChests.Clear();
            _bumped.Clear();
            _triedStairs = false;
            _lastOrder = null;
            _trail.Clear();
        }

        _trail.Add(_self.Position);

        foreach (var position in Room.AllPositions())
        {
            if (position.DistanceTo(_self.Position) <= _self.VisionRadius)
            {
                _lastSeen[position] = view.Round;
            }
        }

        foreach (var tile in view.Tiles)
        {
            _known[tile.Position] = tile.Terrain;
        }

        foreach (var chest in view.Chests)
        {
            if (chest.Opened)
            {
                _chests.Remove(chest.Id);
                _openedChests.Add(chest.Position);
            }
            else
            {
                _chests[chest.Id] = chest.Position;
            }
        }

        _bodies = _self.SecondBody is { } second ? [_self.Position, second] : [_self.Position];

        // Si el paso anterior rebotó, esa casilla está tapada por algo que no se ve: se deja de insistir un rato.
        if (_lastOrder is { Type: OrderType.Move, Direction: { } moved } && _lastBodies.SequenceEqual(_bodies))
        {
            _bumped[_self.Position.Step(moved)] = view.Round + 2 + rng.Next(3);
        }
        _danger = [.. view.Telegraphs.SelectMany(t => t.Tiles)];
        foreach (var fire in view.Fires)
        {
            _danger.Add(fire.Position);
        }

        if (view.Round % 2 == 0 && _self.Class != HeroClass.Mampostero)
        {
            foreach (var spikes in _known.Where(k => k.Value == Terrain.Spikes))
            {
                _danger.Add(spikes.Key);
            }
        }

        _enemies = view.Enemies.GroupBy(e => e.Position).ToDictionary(g => g.Key, g => g.First());
        _others = [];
        foreach (var ally in view.Allies)
        {
            if (ally.Position is { } position && ally.Status is not HeroStatus.EmberSpirit)
            {
                _others[position] = ally;
            }

            if (ally.SecondBody is { } body)
            {
                _others[body] = ally;
            }
        }
    }

    private bool Roll(int percent) => percent > 0 && rng.Next(100) < percent;

    private bool SkillReady => _self.SkillCooldown == 0;

    private bool IsHostile(AllyView other) => (other.Status == HeroStatus.Renegade) != (_self.Status == HeroStatus.Renegade);

    private Terrain TerrainAt(Position position) =>
        !Room.IsInside(position) ? Terrain.Wall : _known.GetValueOrDefault(position, Terrain.Floor);

    private bool IsPassable(Position position)
    {
        var terrain = TerrainAt(position);
        if (terrain is Terrain.Wall or Terrain.Brazier or Terrain.Altar)
        {
            return false;
        }

        if (terrain == Terrain.Crack && _self.Class != HeroClass.Mampostero)
        {
            return false;
        }

        return !_chests.ContainsValue(position) && !_openedChests.Contains(position) && _bumped.GetValueOrDefault(position) < _view.Round;
    }

    private bool IsOpen(Position position) => IsPassable(position) && !_enemies.ContainsKey(position) && !_others.ContainsKey(position);

    /// <summary>Lo que hace falta para que un ataque desde <paramref name="origin"/> le haga daño a este enemigo.</summary>
    private static bool IsImmune(EnemyView enemy, Position origin) =>
        enemy.Kind == EnemyKind.Warden && enemy.Phase == 1 && enemy.Position.DirectionTo(origin) == enemy.Facing;

    private int AttackDamage() => _self.Status == HeroStatus.Renegade ? 1 : 1 + (_self.Blessings.Contains(Blessing.StoneFist) ? 1 : 0);

    /// <summary>Puntúa un ataque en esa dirección: cuánto daño útil hace; negativo si le da a un aliado.</summary>
    private int AttackScore(Direction direction)
    {
        var score = LineScore(_self.Position, direction);
        if (_self.SecondBody is { } second)
        {
            score += LineScore(second, direction.Mirrored());
        }

        return score;
    }

    private int LineScore(Position origin, Direction direction)
    {
        var position = origin;
        for (var i = 0; i < _self.Class.Range(); i++)
        {
            position = position.Step(direction);
            var terrain = TerrainAt(position);
            if (terrain is Terrain.Wall or Terrain.Brazier)
            {
                return 0;
            }

            if (_enemies.TryGetValue(position, out var enemy))
            {
                if (_self.Status == HeroStatus.Renegade || IsImmune(enemy, origin))
                {
                    return 0;
                }

                return enemy.Hp <= AttackDamage() ? 6 : 4;
            }

            if (_others.TryGetValue(position, out var other) && other.Status != HeroStatus.Down)
            {
                return IsHostile(other) ? 5 : -10;
            }
        }

        return 0;
    }

    private Direction? BestAttack(int atLeast = 1)
    {
        Direction? best = null;
        var bestScore = atLeast - 1;
        foreach (var direction in Directions.All)
        {
            var score = AttackScore(direction);
            if (score > bestScore)
            {
                best = direction;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Si un aliado está a tiro, a veces le pega: fuego amigo con robo de ascuas.</summary>
    private Direction? Betrayal()
    {
        if (!Roll(options.BetrayalPercent))
        {
            return null;
        }

        foreach (var direction in Directions.All)
        {
            if (AttackScore(direction) < 0)
            {
                return direction;
            }
        }

        return null;
    }

    /// <summary>¿Mata este ataque a un enemigo cuyo golpe me va a caer? (la fase de héroes va antes que la de enemigos).</summary>
    private Direction? KillThreat()
    {
        var threats = _view.Telegraphs
            .Where(t => t.EnemyId is not null && t.Tiles.Intersect(_bodies).Any())
            .Select(t => t.EnemyId!.Value)
            .ToHashSet();
        foreach (var direction in Directions.All)
        {
            var position = _self.Position;
            for (var i = 0; i < _self.Class.Range(); i++)
            {
                position = position.Step(direction);
                if (TerrainAt(position) is Terrain.Wall or Terrain.Brazier)
                {
                    break;
                }

                if (_enemies.TryGetValue(position, out var enemy))
                {
                    if (threats.Contains(enemy.Id) && enemy.Hp <= AttackDamage() && !IsImmune(enemy, _self.Position) && AttackScore(direction) > 0)
                    {
                        return direction;
                    }

                    break;
                }
            }
        }

        return null;
    }

    private bool InDanger => _bodies.Exists(_danger.Contains);

    private bool CanTakeAHit => _self.Class == HeroClass.Avaro ? _self.Embers >= 3 : _self.Hp + _self.Shield >= 3;

    /// <summary>A dónde quedaría cada cuerpo con este movimiento (cada uno rebota por su cuenta).</summary>
    private List<Position> AfterMove(Direction direction)
    {
        var result = new List<Position>(2);
        var first = _self.Position.Step(direction);
        var firstTarget = IsOpen(first) ? first : _self.Position;
        if (_self.SecondBody is not { } second)
        {
            result.Add(firstTarget);
            return result;
        }

        var secondStep = second.Step(direction.Mirrored());
        var secondTarget = IsOpen(secondStep) ? secondStep : second;
        if (firstTarget == secondTarget)
        {
            firstTarget = _self.Position;
            secondTarget = second;
        }

        result.Add(firstTarget);
        result.Add(secondTarget);
        return result;
    }

    /// <summary>El mejor paso para no quedar bajo un golpe; prefiere acercarse a <paramref name="goal"/>.</summary>
    private Direction? SafestMove(Position? goal = null)
    {
        Direction? best = null;
        var bestScore = int.MinValue;
        foreach (var direction in Directions.All)
        {
            var after = AfterMove(direction);
            if (after.SequenceEqual(_bodies) || after.Exists(_danger.Contains))
            {
                continue;
            }

            var score = goal is { } target ? -after.Min(p => p.DistanceTo(target)) : 0;
            if (score > bestScore)
            {
                best = direction;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Primer paso del camino más corto hacia una casilla que cumpla <paramref name="isGoal"/>; el primer paso puede evitar el peligro.</summary>
    private (Direction? First, int Length) PathTo(Func<Position, bool> isGoal, bool safeFirstStep)
    {
        var start = _self.Position;
        var queue = new Queue<(Position Position, Direction? First, int Length)>();
        var seen = new HashSet<Position> { start };
        queue.Enqueue((start, null, 0));
        while (queue.Count > 0)
        {
            var (current, first, length) = queue.Dequeue();
            if (first is not null && isGoal(current))
            {
                return (first, length);
            }

            foreach (var direction in Directions.All)
            {
                var next = current.Step(direction);
                if (current == start && (!IsOpen(next) || (safeFirstStep && AfterMove(direction).Exists(_danger.Contains))))
                {
                    continue;
                }

                if (seen.Add(next) && IsPassable(next))
                {
                    queue.Enqueue((next, first ?? direction, length + 1));
                }
            }
        }

        return (null, int.MaxValue);
    }

    /// <summary>¿Va y vuelve entre dos casillas? Le pasa a dos que se persiguen a la vez: parar un momento rompe el baile.</summary>
    private bool IsPacing =>
        _trail.Count >= 4 && _trail[^1] == _trail[^3] && _trail[^2] == _trail[^4] && _trail[^1] != _trail[^2];

    /// <summary>
    /// Camina hacia la meta sin meterse bajo un golpe: rodea si se puede y, si no, espera a que pase. Con
    /// <paramref name="charge"/> va derecho aunque le peguen (si no, un Ballestero no se alcanza nunca).
    /// </summary>
    private Order Walk(Func<Position, bool> isGoal, Position? hint = null, bool charge = false)
    {
        if (isGoal(_self.Position))
        {
            return Order.DefaultGuard;
        }

        if (IsPacing && Roll(50))
        {
            var sidesteps = Directions.All.Where(d => !AfterMove(d).SequenceEqual(_bodies)).ToList();
            return sidesteps.Count > 0 ? new Order(OrderType.Move, sidesteps[rng.Next(sidesteps.Count)]) : Order.DefaultGuard;
        }

        var (step, length) = PathTo(isGoal, safeFirstStep: !charge);
        if (!charge && !InDanger)
        {
            // Si por el camino seguro hay que dar un rodeo, conviene esperar a que pase el golpe o se bajen los pinchos.
            var (_, direct) = PathTo(isGoal, safeFirstStep: false);
            if (length > direct + 1)
            {
                return Order.DefaultGuard;
            }
        }

        if (step is { } direction)
        {
            return new Order(OrderType.Move, direction);
        }

        if (!InDanger)
        {
            return Order.DefaultGuard;
        }

        return SafestMove(hint) is { } safe ? new Order(OrderType.Move, safe) : Order.DefaultGuard;
    }

    /// <summary>Casillas desde las que se le puede pegar a un enemigo (sin estar de frente al Celador en fase 1).</summary>
    private HashSet<Position> AttackSpots(EnemyView enemy)
    {
        var spots = new HashSet<Position>();
        foreach (var direction in Directions.All)
        {
            var spot = enemy.Position;
            for (var i = 0; i < _self.Class.Range(); i++)
            {
                spot = spot.Step(direction);
                if (TerrainAt(spot) is Terrain.Wall or Terrain.Brazier)
                {
                    break;
                }

                if (!IsImmune(enemy, spot))
                {
                    spots.Add(spot);
                }
            }
        }

        return spots;
    }

    private EnemyView? NearestEnemy(Func<EnemyView, bool>? filter = null) =>
        _view.Enemies
            .Where(e => filter?.Invoke(e) ?? true)
            .OrderBy(e => _bodies.Min(b => b.DistanceTo(e.Position)))
            .ThenBy(e => e.Id)
            .FirstOrDefault();

    private Position StairsTile() =>
        _known.FirstOrDefault(k => k.Value == Terrain.Stairs).Key is var stairs && TerrainAt(stairs) == Terrain.Stairs
            ? stairs
            : new Position(4, 0);

    // ───────────── Héroe ─────────────

    private Order DecideHero(IReadOnlyList<(PlayerId Player, Order Order)>? heard)
    {
        if (KillThreat() is { } kill)
        {
            return new Order(OrderType.Attack, kill);
        }

        if (InDanger)
        {
            if (SkillToEscape() is { } escape)
            {
                return escape;
            }

            // Con vida de sobra se aguanta el golpe y se pega: esquivar sin parar no gana peleas.
            if (CanTakeAHit && BestAttack() is { } trade)
            {
                return new Order(OrderType.Attack, trade);
            }

            if (ChargeTarget() is { } prey)
            {
                var spots = AttackSpots(prey);
                return Walk(spots.Contains, prey.Position, charge: true);
            }

            return SafestMove(StairsTile()) is { } dodge ? new Order(OrderType.Move, dodge) : Order.DefaultGuard;
        }

        if (AdjacentFallen() is { } lift)
        {
            return lift;
        }

        if (SkillReady && SkillOfOpportunity(heard) is { } skill)
        {
            return skill;
        }

        if (Betrayal() is { } betrayal)
        {
            return new Order(OrderType.Attack, betrayal);
        }

        if (BestAttack() is { } attack)
        {
            return new Order(OrderType.Attack, attack);
        }

        if (AdjacentInteraction() is { } interaction)
        {
            return interaction;
        }

        return Advance();
    }

    private Order? AdjacentFallen()
    {
        foreach (var direction in Directions.All)
        {
            if (_others.TryGetValue(_self.Position.Step(direction), out var other) && other.Status == HeroStatus.Down)
            {
                return new Order(OrderType.Interact, direction);
            }

            if (_self.SecondBody is { } second && _others.TryGetValue(second.Step(direction.Mirrored()), out other) && other.Status == HeroStatus.Down)
            {
                return new Order(OrderType.Interact, direction);
            }
        }

        return null;
    }

    private Order? AdjacentInteraction()
    {
        foreach (var direction in Directions.All)
        {
            var tile = _self.Position.Step(direction);
            if (_chests.ContainsValue(tile) || (TerrainAt(tile) == Terrain.Altar && _self.SilverKeys > 0 && _self.DraftOptions.IsEmpty))
            {
                return new Order(OrderType.Interact, direction);
            }

            if (_self.SecondBody is { } second)
            {
                var mirrored = second.Step(direction.Mirrored());
                if (_chests.ContainsValue(mirrored))
                {
                    return new Order(OrderType.Interact, direction);
                }
            }
        }

        return null;
    }

    /// <summary>Un enemigo que vale la pena perseguir aunque el camino duela: el Ballestero huye a distancia y dispara en línea.</summary>
    private EnemyView? ChargeTarget() =>
        _self.Hp + _self.Shield >= 4 || (_self.Class == HeroClass.Avaro && _self.Embers >= 4)
            ? NearestEnemy(e => e.Kind == EnemyKind.Crossbowman)
            : null;

    /// <summary>Qué hacer sin nada urgente: levantar a un caído, ir a un enemigo, recoger ascuas, abrir cofres, explorar, escalera.</summary>
    private Order Advance()
    {
        var fallen = _view.Allies.FirstOrDefault(a => a is { Status: HeroStatus.Down, Position: not null });
        if (fallen?.Position is { } fallenAt && _self.Position.DistanceTo(fallenAt) <= 6)
        {
            return Walk(p => p.DistanceTo(fallenAt) == 1, fallenAt);
        }

        // Lo que tiró en su propia casilla no lo puede recoger sin irse y volver.
        var ground = _view.Ground.Where(g => !_bodies.Contains(g.Position)).OrderBy(g => _self.Position.DistanceTo(g.Position)).FirstOrDefault();
        if (ground is not null && (_self.Class == HeroClass.Avaro || _self.Position.DistanceTo(ground.Position) <= 3) && IsOpen(ground.Position))
        {
            return Walk(p => p == ground.Position, ground.Position);
        }

        if (_self.Class == HeroClass.Usurera && LoanWalk() is { } toDebtor)
        {
            return toDebtor;
        }

        // Los monstruos vienen solos: no hace falta ir a buscarlos lejos mientras se avanza hacia la escalera.
        var enemy = NearestEnemy(e => e.Kind is EnemyKind.Warden or EnemyKind.Totem or EnemyKind.Crossbowman || _triedStairs || _bodies.Min(b => b.DistanceTo(e.Position)) <= 3);
        if (enemy is not null)
        {
            if (_self.Class == HeroClass.Hilvan && StretchAround(enemy) is { } stretch)
            {
                return stretch;
            }

            var spots = AttackSpots(enemy);
            return Walk(spots.Contains, enemy.Position, charge: ChargeTarget() == enemy);
        }

        if (_self.Class == HeroClass.Escucha && _view.Noise.Count > 0)
        {
            var noise = _view.Noise.OrderBy(n => _self.Position.DistanceTo(n)).First();
            return Walk(p => p.DistanceTo(noise) <= 1, noise);
        }

        // Los cofres dan ascuas pero el reloj aprieta: solo si quedan a mano (o si su pacto los pide).
        var chestReach = _self.Pact == PactKind.OpenThreeChests ? 6 : 3;
        var nearChests = _chests.Values.Where(c => _self.Position.DistanceTo(c) <= chestReach).ToHashSet();
        if (nearChests.Count > 0)
        {
            return Walk(p => Directions.All.Any(d => nearChests.Contains(p.Step(d))));
        }

        var altar = _known.FirstOrDefault(k => k.Value == Terrain.Altar).Key;
        if (_self.SilverKeys > 0 && TerrainAt(altar) == Terrain.Altar)
        {
            return Walk(p => p.DistanceTo(altar) == 1, altar);
        }

        // Nadie a la vista: a la escalera, viendo la sala por el camino; si no se abre, queda alguien: a patrullar lo que hace más tiempo no se ve.
        var stairsTile = StairsTile();
        if (_view.RoomIndex < _view.RoomCount - 1 && !_triedStairs)
        {
            _triedStairs = _bodies.Contains(stairsTile);
            if (!_triedStairs)
            {
                return Walk(p => p == stairsTile, stairsTile);
            }
        }

        var oldest = Room.AllPositions().Where(IsPassable).OrderBy(p => _lastSeen.GetValueOrDefault(p)).ThenBy(p => _self.Position.DistanceTo(p)).FirstOrDefault();
        if (_lastSeen.GetValueOrDefault(oldest) < _view.Round)
        {
            return Walk(p => p.DistanceTo(oldest) <= _self.VisionRadius, oldest);
        }

        _triedStairs = false;
        return Order.DefaultGuard;
    }

    /// <summary>La Usurera va hacia quien le puede deber: un caído o un aliado bastante herido a la vista.</summary>
    private Order? LoanWalk()
    {
        if (!SkillReady)
        {
            return null;
        }

        var needy = _view.Allies
            .Where(a => a.Position is not null && !IsHostile(a) && a.Class != HeroClass.Avaro)
            .Where(a => a.Status == HeroStatus.Down || (a.Status == HeroStatus.Active && a.Hp + 2 <= HeroCatalog.For(a.Class).MaxHp))
            .OrderBy(a => _self.Position.DistanceTo(a.Position!.Value))
            .FirstOrDefault();
        if (needy?.Position is not { } at || _self.Position.DistanceTo(at) > 6)
        {
            return null;
        }

        return Walk(p => p.DistanceTo(at) <= HeroRules.LoanReach && (p.X == at.X || p.Y == at.Y), at);
    }

    /// <summary>
    /// Hilván busca dejar al enemigo entre sus cuerpos: con ellos en una fila, los lleva a la del enemigo (norte y sur los
    /// mueven igual) y los abre (este y oeste los mueven en espejo) hasta que queda en medio.
    /// </summary>
    private Order? StretchAround(EnemyView enemy)
    {
        if (_self.SecondBody is not { } second || second.Y != _self.Position.Y)
        {
            return null;
        }

        if (IsPacing)
        {
            return null;
        }

        var row = _self.Position.Y;
        if (enemy.Position.Y != row)
        {
            var vertical = enemy.Position.Y < row ? Direction.North : Direction.South;
            return AfterMove(vertical).Exists(_danger.Contains) || AfterMove(vertical).SequenceEqual(_bodies) ? null : new Order(OrderType.Move, vertical);
        }

        var (left, right) = _self.Position.X < second.X ? (_self.Position, second) : (second, _self.Position);
        if (enemy.Position.X > left.X && enemy.Position.X < right.X)
        {
            return null;
        }

        // Abrirlos: el cuerpo de la izquierda va al oeste y el de la derecha al este.
        var opening = _self.Position.X < second.X ? Direction.West : Direction.East;
        return AfterMove(opening).Exists(_danger.Contains) || AfterMove(opening).SequenceEqual(_bodies) ? null : new Order(OrderType.Move, opening);
    }

    /// <summary>Habilidades que sirven para no comerse el golpe que viene.</summary>
    private Order? SkillToEscape()
    {
        if (!SkillReady)
        {
            return null;
        }

        return _self.Class switch
        {
            HeroClass.Anteayer when _self.RewindTile is { } tile && !_danger.Contains(tile) => new Order(OrderType.Skill, Direction.North),
            HeroClass.Errata => CorrectPlan(),
            HeroClass.Avaro => BribePlan(mustSaveMe: true),
            HeroClass.Mampostero => ShiftPlan(),
            HeroClass.Polizon => BoardPlan(),
            _ => null,
        };
    }

    /// <summary>Cada héroe usa su habilidad cuando se da su situación.</summary>
    private Order? SkillOfOpportunity(IReadOnlyList<(PlayerId Player, Order Order)>? heard) => _self.Class switch
    {
        HeroClass.Errata => CorrectPlan(),
        HeroClass.Anteayer => _self.RewindHp is { } hp && hp >= _self.Hp + 2 ? new Order(OrderType.Skill, Direction.North) : null,
        HeroClass.Hilvan => TugPlan(),
        HeroClass.Polizon => BoardPlan(),
        HeroClass.Escucha => ShoutPlan(),
        HeroClass.Usurera => LoanPlan(),
        HeroClass.Avaro => BribePlan(mustSaveMe: false),
        HeroClass.Mampostero => ShiftPlan(),
        HeroClass.Campanero => BellPlan(heard),
        _ => null,
    };

    /// <summary>Errata: desvía si saca a alguien del golpe o se lo pone encima a un monstruo.</summary>
    private Order? CorrectPlan()
    {
        var tiles = _view.Telegraphs.SelectMany(t => t.Tiles).ToList();
        if (!tiles.Exists(t => t.DistanceTo(_self.Position) <= HeroRules.CorrectReach))
        {
            return null;
        }

        Direction? best = null;
        var bestScore = 0;
        foreach (var direction in Directions.All)
        {
            var score = 0;
            foreach (var tile in tiles)
            {
                var moved = tile.Step(direction);
                score += TileThreat(tile) - TileThreat(moved);
                if (TerrainAt(moved) != Terrain.Wall && _enemies.ContainsKey(moved))
                {
                    score += 2;
                }
            }

            // Traición: si algo le va a caer a un aliado, está dispuesta a dejarlo (o a provocarlo).
            if (Roll(options.BetrayalPercent) && tiles.Exists(t => _others.TryGetValue(t.Step(direction), out var a) && !IsHostile(a) && a.Status != HeroStatus.Down))
            {
                score += 4;
            }

            if (score > bestScore)
            {
                best = direction;
                bestScore = score;
            }
        }

        return best is { } chosen ? new Order(OrderType.Skill, chosen) : null;
    }

    /// <summary>Cuánto duele que caiga un golpe en esa casilla: a mí o a un aliado (a un rival, al revés).</summary>
    private int TileThreat(Position tile)
    {
        if (TerrainAt(tile) == Terrain.Wall)
        {
            return 0;
        }

        if (_bodies.Contains(tile))
        {
            return 3;
        }

        if (_others.TryGetValue(tile, out var other) && other.Status != HeroStatus.Down)
        {
            return IsHostile(other) ? -2 : 2;
        }

        return 0;
    }

    /// <summary>Hilván: tira si hay un enemigo entre los cuerpos y ningún aliado.</summary>
    private Order? TugPlan()
    {
        if (_self.SecondBody is not { } second || (second.X != _self.Position.X && second.Y != _self.Position.Y))
        {
            return null;
        }

        var hits = 0;
        var direction = _self.Position.DirectionTo(second)!.Value;
        for (var tile = _self.Position.Step(direction); tile != second; tile = tile.Step(direction))
        {
            if (TerrainAt(tile) == Terrain.Wall)
            {
                return null;
            }

            if (_others.TryGetValue(tile, out var other) && other.Status != HeroStatus.Down && !IsHostile(other))
            {
                return null;
            }

            if (_enemies.ContainsKey(tile) || _others.ContainsKey(tile))
            {
                hits++;
            }
        }

        return hits > 0 ? new Order(OrderType.Skill, Direction.North) : null;
    }

    /// <summary>Polizón: entra si está herido o el enemigo adyacente tiene compañía; al Celador lo gira si hay aliados cerca.</summary>
    private Order? BoardPlan()
    {
        foreach (var direction in Directions.All)
        {
            if (!_enemies.TryGetValue(_self.Position.Step(direction), out var enemy))
            {
                continue;
            }

            if (enemy.Kind == EnemyKind.Warden)
            {
                if (enemy.Phase == 1 && _others.Keys.Any(p => p.DistanceTo(enemy.Position) <= 2))
                {
                    return new Order(OrderType.Skill, direction);
                }

                continue;
            }

            if (enemy.Kind != EnemyKind.Totem && (_self.Hp <= 2 || enemy.Kind == EnemyKind.Crossbowman || _view.Enemies.Count > 1))
            {
                return new Order(OrderType.Skill, direction);
            }
        }

        return null;
    }

    /// <summary>La Escucha grita cuando no percibe a nadie y aguanta el golpe que atrae.</summary>
    private Order? ShoutPlan()
    {
        var allies = _view.Allies.Count(a => a.Status == HeroStatus.Active);
        return _view.Enemies.IsEmpty && _self.Hp >= 4 && allies > 0 ? new Order(OrderType.Skill, Direction.North) : null;
    }

    /// <summary>La Usurera presta a quien esté caído o con la mitad de vida o menos; si no hay a quién, se acerca sola.</summary>
    private Order? LoanPlan()
    {
        foreach (var direction in Directions.All)
        {
            var tile = _self.Position;
            for (var i = 0; i < HeroRules.LoanReach; i++)
            {
                tile = tile.Step(direction);
                if (_others.TryGetValue(tile, out var other)
                    && !IsHostile(other)
                    && other.Class != HeroClass.Avaro
                    && (other.Status == HeroStatus.Down || other.Hp + 2 <= HeroCatalog.For(other.Class).MaxHp))
                {
                    return new Order(OrderType.Skill, direction);
                }
            }
        }

        return null;
    }

    /// <summary>El Avaro soborna lo que le va a pegar, si le alcanza sin quedarse en la última ascua.</summary>
    private Order? BribePlan(bool mustSaveMe)
    {
        foreach (var direction in Directions.All)
        {
            var tile = _self.Position;
            for (var i = 0; i < HeroRules.BribeReach; i++)
            {
                tile = tile.Step(direction);
                if (!_enemies.TryGetValue(tile, out var enemy))
                {
                    continue;
                }

                var cost = enemy.Kind == EnemyKind.Warden ? HeroRules.WardenBribeCost : HeroRules.BribeCost;
                var threatensMe = _view.Telegraphs.Any(t => t.EnemyId == enemy.Id && t.Tiles.Intersect(_bodies).Any());
                var threatensAlly = _view.Telegraphs.Any(t => t.EnemyId == enemy.Id && t.Tiles.Any(_others.ContainsKey));
                if (_self.Embers > cost + (mustSaveMe ? 0 : 3) && (threatensMe || (!mustSaveMe && threatensAlly)))
                {
                    return new Order(OrderType.Skill, direction);
                }

                break;
            }
        }

        return null;
    }

    /// <summary>El Mampostero desliza si un enemigo sale por el borde o si saca a alguien del golpe; no si mete a un aliado.</summary>
    private Order? ShiftPlan()
    {
        Direction? best = null;
        var bestScore = 0;
        foreach (var direction in Directions.All)
        {
            var score = ShiftScore(direction);
            if (score > bestScore)
            {
                best = direction;
                bestScore = score;
            }
        }

        return best is { } chosen ? new Order(OrderType.Skill, chosen) : null;
    }

    private int ShiftScore(Direction direction)
    {
        var horizontal = direction is Direction.East or Direction.West;
        var line = horizontal ? _self.Position.Y : _self.Position.X;
        var step = direction is Direction.East or Direction.South ? 1 : -1;
        var score = 0;
        for (var i = 0; i < Room.Size; i++)
        {
            var from = horizontal ? new Position(i, line) : new Position(line, i);
            var moved = (i + step + Room.Size) % Room.Size;
            var to = horizontal ? new Position(moved, line) : new Position(line, moved);
            var wraps = from.DistanceTo(to) > 1;
            if (_enemies.TryGetValue(from, out var enemy))
            {
                if (wraps)
                {
                    score += enemy.Hp <= HeroRules.WrapDamage ? 6 : 4;
                }
            }
            else if (_others.TryGetValue(from, out var other) && other.Status != HeroStatus.Down)
            {
                if (wraps)
                {
                    score += IsHostile(other) ? 3 : -5;
                }

                score += (_danger.Contains(from) ? 2 : 0) - (_danger.Contains(to) ? 3 : 0);
            }
            else if (from == _self.Position)
            {
                score += (_danger.Contains(from) ? 3 : 0) - (_danger.Contains(to) ? 3 : 0) - (wraps ? 4 : 0);
            }
        }

        return score;
    }

    /// <summary>El Campanero toca si hay pelea y él no tiene otra cosa; si oyó que un aliado le pega a su casilla, se aparta.</summary>
    private Order? BellPlan(IReadOnlyList<(PlayerId Player, Order Order)>? heard)
    {
        if (heard is not null && heard.Any(h => h.Order.Type == OrderType.Attack) && BestAttack() is not null)
        {
            return null;
        }

        return _view.Enemies.Count >= 2 && BestAttack() is null && _view.Allies.Any(a => a.Status == HeroStatus.Active)
            ? new Order(OrderType.Skill, Direction.North)
            : null;
    }

    // ───────────── Polizón dentro de un huésped ─────────────

    private Order DecideInHost()
    {
        var hostId = _self.HostEnemyId!.Value;
        var host = _view.Enemies.FirstOrDefault(e => e.Id == hostId);
        var renegade = _self.Status == HeroStatus.Renegade;

        // Golpear lo que tenga al lado: monstruos si es del grupo; aliados, solo si traiciona.
        foreach (var direction in Directions.All)
        {
            var tile = _self.Position.Step(direction);
            if (!renegade && _enemies.TryGetValue(tile, out var enemy) && enemy.Id != hostId && enemy.Kind != EnemyKind.Warden)
            {
                return new Order(OrderType.Attack, direction);
            }

            if (_others.TryGetValue(tile, out var other) && other.Status != HeroStatus.Down && (IsHostile(other) || Roll(options.BetrayalPercent)))
            {
                return new Order(OrderType.Attack, direction);
            }
        }

        // Curado y con el huésped gastado, sale para no estorbar: el drenaje también lo mata.
        if (host is not null && _self.Hp >= _self.MaxHp && host.Hp <= 1 && !renegade)
        {
            return Order.DefaultGuard;
        }

        var target = renegade
            ? _view.Allies.Where(a => a.Position is not null && IsHostile(a)).Select(a => a.Position).FirstOrDefault()
            : NearestEnemy(e => e.Id != hostId)?.Position;
        if (target is { } goal)
        {
            var step = _self.Position.DirectionTo(goal);
            if (step is { } direction && IsOpen(_self.Position.Step(direction)))
            {
                return new Order(OrderType.Move, direction);
            }
        }

        return Order.DefaultGuard;
    }

    // ───────────── Brasa errante ─────────────

    private Order DecideEmber()
    {
        // Reavivar a un caído adyacente.
        foreach (var direction in Directions.All)
        {
            if (_others.TryGetValue(_self.Position.Step(direction), out var fallen) && fallen.Status == HeroStatus.Down)
            {
                return new Order(OrderType.Interact, direction);
            }
        }

        // Avivar a quien esté en línea y en peligro; Estorbo si un enemigo cercano le apunta a alguien.
        foreach (var direction in Directions.All)
        {
            for (var i = 1; i <= PathRules.KindleRange; i++)
            {
                var tile = _self.Position.Step(direction, i);
                if (SkillReady && _others.TryGetValue(tile, out var ally) && ally.Status == HeroStatus.Active && _danger.Contains(tile))
                {
                    return new Order(OrderType.Skill, direction);
                }

                if (_self.Path.HinderCooldown == 0 && _enemies.TryGetValue(tile, out var enemy) && !(enemy.Kind == EnemyKind.Warden && enemy.Phase == 2)
                    && _view.Telegraphs.Any(t => t.EnemyId == enemy.Id && t.Tiles.Any(_others.ContainsKey)))
                {
                    return new Order(OrderType.Attack, direction);
                }
            }
        }

        // Ir hacia el aliado que más lo necesita; si ya está junto a él, encender el Faro.
        var needy = _view.Allies
            .Where(a => a.Position is not null && a.Status is HeroStatus.Active or HeroStatus.Down)
            .OrderBy(a => a.Status == HeroStatus.Down ? 0 : a.Hp)
            .FirstOrDefault();
        if (needy?.Position is { } at && _self.Position.DistanceTo(at) > 1 && _self.Position.DirectionTo(at) is { } toward)
        {
            return new Order(OrderType.Move, toward);
        }

        return Order.DefaultGuard;
    }

    // ───────────── Renegado ─────────────

    private Order DecideRenegade()
    {
        if (SkillReady && _self.Class == HeroClass.Hilvan && TugPlan() is { } tug)
        {
            return tug;
        }

        if (BestAttack() is { } attack)
        {
            return new Order(OrderType.Attack, attack);
        }

        var prey = _view.Allies
            .Where(a => a.Status == HeroStatus.Active && a.Position is not null)
            .OrderBy(a => _self.Position.DistanceTo(a.Position!.Value))
            .FirstOrDefault();
        if (prey?.Position is { } at)
        {
            return Walk(p => p.DistanceTo(at) <= 1, at);
        }

        // Si no ve a nadie, va a donde suele estar el grupo: hacia la escalera.
        return Walk(p => p.DistanceTo(StairsTile()) <= 2, StairsTile());
    }
}

/// <summary>Alcance del ataque básico de cada héroe.</summary>
internal static class HeroClassExtensions
{
    public static int Range(this HeroClass heroClass) => HeroCatalog.For(heroClass).Range;
}
