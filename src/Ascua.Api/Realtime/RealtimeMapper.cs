using System.Globalization;
using Ascua.Application.Matches;
using Ascua.Domain.Events;
using Ascua.Domain.Model;
using Ascua.Domain.Views;

namespace Ascua.Api.Realtime;

/// <summary>Datos de servidor que acompañan a la vista: quién está conectado y qué órdenes hay en la ronda.</summary>
public sealed record ViewContext(IReadOnlySet<Guid> Connected, IReadOnlyDictionary<PlayerId, Order> Orders);

/// <summary>
/// Traduce el dominio al contrato. Solo trabaja sobre <see cref="PlayerView"/> y los eventos ya
/// filtrados por <see cref="PlayerViewProjector"/>: nunca ve la verdad completa de otro jugador.
/// </summary>
public static class RealtimeMapper
{
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Id(Guid id) => id.ToString("D");

    public static SnapshotMessage Snapshot(MatchSnapshot snapshot, MatchParticipant participant, ViewContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(context);
        var view = PlayerViewProjector.For(snapshot.State, participant.PlayerId);
        return new SnapshotMessage
        {
            MatchId = Id(snapshot.MatchId),
            Seq = snapshot.State.LastSeq,
            ServerTime = Timestamp(now),
            View = new PlayerViewDto
            {
                Status = snapshot.Status.ToCode(),
                Round = Math.Clamp(snapshot.State.Round, 0, 60),
                Floor = view.RoomIndex + 1,
                RoundDeadline = snapshot.Status == MatchStatus.InProgress && snapshot.Deadline is { } deadline ? Timestamp(deadline) : null,
                Me = Self(snapshot, participant, view, context),
                Allies = [.. view.Allies.Select(a => Other(snapshot, a, context))],
                Enemies = Enemies(view),
                Tiles = Tiles(view),
            },
        };
    }

    public static RoundResolvedMessage RoundResolved(
        MatchSnapshot before, MatchSnapshot after, IReadOnlyList<DomainEvent> events, bool defaultGuard, MatchParticipant participant, ViewContext context)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(participant);
        var previous = PlayerViewProjector.For(before.State, participant.PlayerId);
        var current = PlayerViewProjector.For(after.State, participant.PlayerId);
        var floorChanged = current.RoomIndex != previous.RoomIndex;

        var previousEnemies = Enemies(previous).ToDictionary(e => e.EnemyId, Fingerprint);
        var enemies = Enemies(current);
        var previousTiles = floorChanged ? [] : Tiles(previous).ToDictionary(t => (t.X, t.Y), t => t.Kind);

        var visible = new List<IDictionary<string, object>>();
        if (defaultGuard)
        {
            visible.Add(new Dictionary<string, object> { ["type"] = "DEFAULT_GUARD_APPLIED", ["actorId"] = participant.PlayerId.Value });
        }

        visible.AddRange(PlayerViewProjector.VisibleEvents(after.State, events, participant.PlayerId).Select(VisibleEvent).OfType<IDictionary<string, object>>());

        return new RoundResolvedMessage
        {
            MatchId = Id(after.MatchId),
            Round = Math.Clamp(before.State.Round, 1, 60),
            PreviousSeq = before.State.LastSeq,
            Seq = after.State.LastSeq,
            Delta = new PlayerViewDeltaDto
            {
                Status = after.Status.ToCode(),
                FloorChanged = floorChanged ? current.RoomIndex + 1 : null,
                Me = Self(after, participant, current, context),

                // Son a lo sumo 3 y "decided" se reinicia en cada ronda: se envían siempre.
                AlliesChanged = [.. current.Allies.Select(a => Other(after, a, context))],
                EnemiesUpserted = [.. enemies.Where(e => !previousEnemies.TryGetValue(e.EnemyId, out var old) || old != Fingerprint(e))],
                EnemiesRemoved = [.. previousEnemies.Keys.Where(id => floorChanged || enemies.All(e => e.EnemyId != id))],
                TilesChanged = [.. Tiles(current).Where(t => !previousTiles.TryGetValue((t.X, t.Y), out var kind) || kind != t.Kind)],
                Events = [.. visible.Take(128)],
            },
        };
    }

    public static MatchFinishedMessage Finished(MatchSnapshot snapshot, string publicBaseUrl, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var outcome = snapshot.State.Outcome;
        var standings = new List<StandingDto>();
        if (outcome is not null)
        {
            var ranked = outcome.Scores.OrderByDescending(s => s.Total).ToList();
            foreach (var score in ranked)
            {
                var participant = snapshot.Participants.First(p => p.PlayerId == score.Player);
                standings.Add(new StandingDto
                {
                    PlayerId = participant.PlayerId.Value,
                    DisplayName = participant.DisplayName,
                    Hero = Hero(participant.Hero),
                    FinalState = HeroState(snapshot.State.FindHero(score.Player)?.Status ?? HeroStatus.Retired),
                    Embers = Math.Clamp(score.Total, 0, 999),
                    Placement = 1 + ranked.Count(other => other.Total > score.Total),
                    Pact = new RevealedPactDto { PactId = PactId(score.Pact), Description = PactDescription(score.Pact), Fulfilled = score.PactFulfilled },
                });
            }
        }
        else
        {
            // FAULTED/EXPIRED: no hay resultado del dominio y los pactos no se revelan.
            standings.AddRange(snapshot.Participants.Select(p => new StandingDto
            {
                PlayerId = p.PlayerId.Value,
                DisplayName = p.DisplayName,
                Hero = Hero(p.Hero),
                FinalState = HeroState(snapshot.State.FindHero(p.PlayerId)?.Status ?? HeroStatus.Retired),
                Embers = Math.Clamp(snapshot.State.FindHero(p.PlayerId)?.Embers ?? 0, 0, 999),
                Placement = 1,
            }));
        }

        return new MatchFinishedMessage
        {
            MatchId = Id(snapshot.MatchId),
            Seq = snapshot.State.LastSeq,
            Outcome = snapshot.Status switch
            {
                MatchStatus.Faulted => "FAULTED",
                MatchStatus.Terminated => "TERMINATED",
                _ => Outcome(outcome?.Kind),
            },
            Rounds = Math.Clamp(snapshot.State.Round, 0, 60),
            Winners = [.. (outcome?.Winners ?? []).Select(w => w.Value)],
            Standings = standings,
            FinishedAt = Timestamp(now),
            ReplayUrl = $"{publicBaseUrl.TrimEnd('/')}/api/v1/matches/{Id(snapshot.MatchId)}/replay",
        };
    }

    public static DraftOfferMessage? DraftOffer(MatchSnapshot snapshot, MatchParticipant participant, DateTimeOffset now, TimeSpan altarWindow)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(participant);
        var hero = snapshot.State.FindHero(participant.PlayerId);
        if (hero is null || hero.DraftOptions.IsEmpty || !snapshot.Status.IsActive())
        {
            return null;
        }

        var initial = snapshot.Status == MatchStatus.Drafting;
        return new DraftOfferMessage
        {
            MatchId = Id(snapshot.MatchId),
            OfferId = Id(MatchEngine.OfferIdFor(snapshot.MatchId, hero)),
            Source = initial ? "INITIAL_DRAFT" : "ALTAR",
            Options = [.. hero.DraftOptions.Select(BlessingId)],
            Deadline = Timestamp(initial && snapshot.Deadline is { } deadline ? deadline : now + altarWindow),
        };
    }

    public static IDictionary<string, object> OrderDto(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var dto = new Dictionary<string, object> { ["type"] = OrderType(order.Type) };
        if (order.Direction is { } direction)
        {
            dto["direction"] = Direction(direction);
        }

        return dto;
    }

    private static SelfHeroDto Self(MatchSnapshot snapshot, MatchParticipant participant, PlayerView view, ViewContext context)
    {
        var self = view.Self;
        var pending = context.Orders.GetValueOrDefault(participant.PlayerId);
        return new SelfHeroDto
        {
            PlayerId = participant.PlayerId.Value,
            DisplayName = participant.DisplayName,
            Hero = Hero(self.Class),
            State = HeroState(self.Status),
            Hp = Math.Clamp(self.Hp, 0, 8),
            MaxHp = Math.Clamp(self.MaxHp, 1, 8),
            Shield = Math.Clamp(self.Shield, 0, 10),
            Position = Position(self.Position),
            Facing = Direction(self.Facing),
            Embers = Math.Clamp(self.Embers, 0, 999),
            Charges = Math.Clamp(self.Charges, 0, 3),
            AbilityCooldown = Math.Clamp(self.SkillCooldown, 0, 4),
            StealthRoundsLeft = Math.Clamp(self.StealthRounds, 0, 2),
            DownRoundsLeft = self.Status == HeroStatus.Down ? Math.Clamp(HeroCatalog.RoundsUntilExtinct - self.DownRounds, 0, 3) : null,
            Blessings = [.. self.Blessings.Distinct().Select(BlessingId)],
            Pact = new PactDto { PactId = PactId(self.Pact), Description = PactDescription(self.Pact) },
            Decided = snapshot.Status == MatchStatus.InProgress && pending is not null,
            PendingOrder = snapshot.Status == MatchStatus.InProgress && pending is not null ? OrderDto(pending) : null,
        };
    }

    private static OtherHeroDto Other(MatchSnapshot snapshot, AllyView ally, ViewContext context)
    {
        var participant = snapshot.Participants.First(p => p.PlayerId == ally.Player);

        // La vida máxima real delataría bendiciones (Corazón doble, Puño de piedra): se informa la base del héroe.
        var maxHp = Math.Max(HeroCatalog.For(ally.Class).MaxHp, ally.Hp);
        return new OtherHeroDto
        {
            PlayerId = ally.Player.Value,
            DisplayName = participant.DisplayName,
            Hero = Hero(ally.Class),
            State = HeroState(ally.Status),
            Hp = Math.Clamp(ally.Hp, 0, 8),
            MaxHp = Math.Clamp(maxHp, 1, 8),
            Position = ally.Position is { } position ? Position(position) : null,
            Facing = ally.Facing is { } facing ? Direction(facing) : null,
            Embers = Math.Clamp(ally.Embers, 0, 999),
            BlessingCount = Math.Clamp(ally.BlessingCount, 0, 12),
            Connected = context.Connected.Contains(participant.UserId),
            Decided = snapshot.Status == MatchStatus.InProgress && context.Orders.ContainsKey(ally.Player),
        };
    }

    private static List<EnemyDto> Enemies(PlayerView view) =>
        [.. view.Enemies.Select(e => new EnemyDto
        {
            EnemyId = e.Id,
            Kind = EnemyKind(e.Kind),
            Hp = Math.Clamp(e.Hp, 0, 64),
            MaxHp = Math.Clamp(e.MaxHp, 1, 64),
            Position = Position(e.Position),
            Facing = Direction(e.Facing),
            Phase = e.Kind == Domain.Model.EnemyKind.Warden ? Math.Clamp(e.Phase, 1, 2) : null,
            Telegraph = [.. view.Telegraphs.Where(t => t.EnemyId == e.Id).SelectMany(t => t.Tiles).Distinct().Take(17).Select(Position)],
        })];

    private static List<TileDto> Tiles(PlayerView view)
    {
        var chests = view.Chests.ToDictionary(c => c.Position, c => c.Opened ? "CHEST_OPENED" : "CHEST");
        var fires = view.Fires.Select(f => f.Position).ToHashSet();
        return [.. view.Tiles.Select(t => new TileDto
        {
            X = t.Position.X,
            Y = t.Position.Y,
            Kind = fires.Contains(t.Position) ? "FIRE" : chests.GetValueOrDefault(t.Position) ?? Terrain(t.Terrain),
        })];
    }

    private static string Fingerprint(EnemyDto e) =>
        $"{e.Kind}|{e.Hp}|{e.MaxHp}|{e.Position.X},{e.Position.Y}|{e.Facing}|{e.Phase}|{string.Join(';', e.Telegraph.Select(p => $"{p.X},{p.Y}"))}";

    private static Dictionary<string, object>? VisibleEvent(DomainEvent domainEvent) => domainEvent switch
    {
        HeroMoved e => Event("MOVED", actor: e.Player.Value, position: e.To),
        MoveBounced e => Event("BOUNCED", actor: e.Player.Value, position: e.Attempted),
        HeroAttacked e => Event("ATTACKED", actor: e.Player.Value),
        SkillUsed e => Event("ABILITY_USED", actor: e.Player.Value),
        HeroGuarded e => Event("GUARDED", actor: e.Player.Value),
        EntityDamaged e => Event("DAMAGED", actor: Ref(e.Source), target: Ref(e.Target), amount: e.Amount),
        HitPrevented e => Event("BLOCKED", actor: Ref(e.Source), target: Ref(e.Target)),
        Healed e => Event("HEALED", target: Ref(e.Target), amount: e.Amount),
        StatusApplied { Effect: StatusEffect.Frozen } e => Event("FROZEN", target: Ref(e.Target)),
        HeroDowned e => Event("DOWNED", target: e.Player.Value),
        HeroRevived e => Event("REVIVED", actor: e.Reviver.Value, target: e.Target.Value),
        HeroExtinguished e => Event("EXTINGUISHED", target: e.Player.Value, amount: -e.EmbersLost),
        EnemyKilled e => Event("ENEMY_KILLED", actor: e.Killer?.Value, target: EnemyRef(e.EnemyId)),
        EnemySpawned e => Event("ENEMY_SPAWNED", target: EnemyRef(e.EnemyId), position: e.Position),
        ChestOpened e => Event("CHEST_OPENED", actor: e.Player.Value),
        EmbersChanged { Reason: EmberReason.StolenFromAlly } e => Event("EMBERS_STOLEN", actor: e.Player.Value, amount: e.Delta),
        EmbersChanged { Reason: EmberReason.StolenByAlly } e => Event("EMBERS_STOLEN", target: e.Player.Value, amount: e.Delta),
        EmbersChanged { Delta: > 0 } e => Event("EMBERS_GAINED", actor: e.Player.Value, amount: e.Delta),
        BlessingChosen e => Event("BLESSING_GAINED", actor: e.Player.Value),
        TrapTriggered e => Event("TRAP_TRIGGERED", position: e.Position),
        FireStarted e => Event("FIRE_STARTED", position: e.Position),
        WardenEnraged e => Event("BOSS_PHASE_CHANGED", target: EnemyRef(e.EnemyId)),
        RoomEntered e => Event("FLOOR_ENTERED", amount: e.RoomIndex + 1),
        _ => null,
    };

    private static Dictionary<string, object> Event(string type, string? actor = null, string? target = null, Position? position = null, int? amount = null)
    {
        var dto = new Dictionary<string, object> { ["type"] = type };
        if (actor is not null)
        {
            dto["actorId"] = actor;
        }

        if (target is not null)
        {
            dto["targetId"] = target;
        }

        if (position is { } p && Room.IsInside(p))
        {
            dto["position"] = new Dictionary<string, object> { ["x"] = p.X, ["y"] = p.Y };
        }

        if (amount is { } a)
        {
            dto["amount"] = Math.Clamp(a, -99, 99);
        }

        return dto;
    }

    private static string? Ref(EntityRef? entity) => entity switch
    {
        { Hero: { } hero } => hero.Value,
        { EnemyId: { } enemy } => EnemyRef(enemy),
        _ => null,
    };

    private static string EnemyRef(int enemyId) => $"enemy:{enemyId}";

    private static PositionDto Position(Position p) => new() { X = p.X, Y = p.Y };

    public static string Direction(Direction direction) => direction switch
    {
        Domain.Model.Direction.North => "N",
        Domain.Model.Direction.South => "S",
        Domain.Model.Direction.East => "E",
        _ => "W",
    };

    public static string OrderType(OrderType type) => type switch
    {
        Domain.Model.OrderType.Guard => "GUARD",
        Domain.Model.OrderType.Move => "MOVE",
        Domain.Model.OrderType.Attack => "ATTACK",
        Domain.Model.OrderType.Skill => "ABILITY",
        _ => "INTERACT",
    };

    public static string Hero(HeroClass hero) => hero switch
    {
        HeroClass.Anvil => "YUNQUE",
        HeroClass.Spark => "CHISPA",
        HeroClass.Raven => "CUERVO",
        _ => "RAIZ",
    };

    public static string HeroState(HeroStatus status) => status switch
    {
        HeroStatus.Active => "ALIVE",
        HeroStatus.Down => "DOWN",
        HeroStatus.Extinct => "EXTINCT",
        _ => "RETIRED",
    };

    public static string BlessingId(Blessing blessing) => blessing switch
    {
        Blessing.LivingEmber => "BRASA_VIVA",
        Blessing.FireStep => "PASO_IGNEO",
        Blessing.StoneFist => "PUNO_DE_PIEDRA",
        Blessing.DoubleHeart => "CORAZON_DOBLE",
        Blessing.HungryBlade => "HOJA_HAMBRIENTA",
        Blessing.BrokenMirror => "ESPEJO_ROTO",
        Blessing.Lamp => "LAMPARA",
        Blessing.Lockpick => "GANZUA",
        Blessing.LastBreath => "ULTIMO_ALIENTO",
        Blessing.Greed => "CODICIA",
        Blessing.Frost => "ESCARCHA",
        _ => "OJO_DE_BUHO",
    };

    private static string EnemyKind(EnemyKind kind) => kind switch
    {
        Domain.Model.EnemyKind.Scavenger => "CARRONERO",
        Domain.Model.EnemyKind.Moth => "POLILLA",
        Domain.Model.EnemyKind.Slime => "GELATINA",
        Domain.Model.EnemyKind.Hopper => "SALTASAPO",
        Domain.Model.EnemyKind.Crossbowman => "BALLESTERO",
        Domain.Model.EnemyKind.Totem => "TOTEM",
        _ => "CELADOR",
    };

    private static string Terrain(Terrain terrain) => terrain switch
    {
        Domain.Model.Terrain.Wall => "WALL",
        Domain.Model.Terrain.Spikes => "SPIKES",
        Domain.Model.Terrain.Crack => "CRACK",
        Domain.Model.Terrain.Brazier => "BRAZIER",
        Domain.Model.Terrain.Altar => "ALTAR",
        Domain.Model.Terrain.Stairs => "STAIRS",
        _ => "FLOOR",
    };

    private static string PactId(PactKind pact) => pact switch
    {
        PactKind.SlayWarden => "FINAL_BLOW_WARDEN",
        PactKind.NeverFall => "NEVER_DOWNED",
        PactKind.OpenThreeChests => "OPEN_3_CHESTS",
        PactKind.ReviveTwoAllies => "REVIVE_2_ALLIES",
        _ => "BETRAY_TWICE_UNSEEN",
    };

    private static string PactDescription(PactKind pact) => pact switch
    {
        PactKind.SlayWarden => "Dar el golpe final al Celador.",
        PactKind.NeverFall => "Terminar sin haber caído.",
        PactKind.OpenThreeChests => "Abrir 3 cofres.",
        PactKind.ReviveTwoAllies => "Levantar a 2 aliados.",
        _ => "Golpear 2 veces a un aliado por la espalda sin que te descubra.",
    };

    private static string Outcome(OutcomeKind? kind) => kind switch
    {
        OutcomeKind.Victory => "WARDEN_DEFEATED",
        OutcomeKind.Draw => "DRAW",
        OutcomeKind.CollectiveDefeat => "EMBER_EXTINGUISHED",
        _ => "ABANDONED",
    };

    /// <summary>Del texto del contrato al dominio. <c>null</c> si no es un valor válido.</summary>
    public static OrderType? ParseOrderType(string? value) => value switch
    {
        "GUARD" => Domain.Model.OrderType.Guard,
        "MOVE" => Domain.Model.OrderType.Move,
        "ATTACK" => Domain.Model.OrderType.Attack,
        "ABILITY" => Domain.Model.OrderType.Skill,
        "INTERACT" => Domain.Model.OrderType.Interact,
        _ => null,
    };

    public static Direction? ParseDirection(string? value) => value switch
    {
        "N" => Domain.Model.Direction.North,
        "S" => Domain.Model.Direction.South,
        "E" => Domain.Model.Direction.East,
        "W" => Domain.Model.Direction.West,
        _ => null,
    };

    public static Blessing? ParseBlessing(string? value) =>
        Enum.GetValues<Blessing>().Select(b => (Blessing?)b).FirstOrDefault(b => BlessingId(b!.Value) == value);

    public static HeroClass? ParseHero(string? value) =>
        Enum.GetValues<HeroClass>().Select(h => (HeroClass?)h).FirstOrDefault(h => Hero(h!.Value) == value);
}
