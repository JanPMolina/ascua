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
                Status = StatusCode(snapshot),
                Round = Math.Clamp(snapshot.State.Round, 0, MaxRound),
                Floor = view.RoomIndex + 1,
                RoundDeadline = RoundDeadline(snapshot, participant.PlayerId),
                Me = Self(snapshot, participant, view, context),
                Allies = [.. view.Allies.Select(a => Other(snapshot, a, context))],
                Enemies = Enemies(view),
                Tiles = Tiles(view),
                HiddenTelegraphs = HiddenTelegraphs(view),
                Echoes = Echoes(view),
                GroundEmbers = Ground(view),
                NoiseTiles = [.. view.Noise.Select(Position)],
                Bell = Bell(snapshot, view),
                Room = RoomInfo(view.Room),
                Curses = [.. view.Curses.Select(CurseCode)],
                ChillRound = view.ChillRound,
            },
        };
    }

    /// <summary>Tope de ronda en el contrato: no hay límite de reglas, pero sí uno de validación (el frío termina antes).</summary>
    public const int MaxRound = 9999;

    /// <summary>Tope de vida de un enemigo en el contrato: el Celador con 5 jugadores y Celador imponente llega a 113.</summary>
    public const int MaxEnemyHp = 120;

    public static string StatusCode(MatchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Status.ToCode();
    }

    private static RoomInfoDto RoomInfo(RoomInfoView room)
    {
        var entry = CurseRules.Describe(room.Type);
        return new RoomInfoDto { Type = RoomTypeCode(room.Type), Name = entry.Name, Effect = entry.Effect, FloodedRows = Math.Clamp(room.FloodedRows, 0, room.Size), Size = room.Size };
    }

    /// <summary>Oferta de misión vigente del jugador (privada).</summary>
    public static PactOfferMessage? PactOffer(MatchSnapshot snapshot, MatchParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(participant);
        var hero = snapshot.State.FindHero(participant.PlayerId);
        if (hero is null || hero.PactOptions.IsEmpty || !snapshot.Status.IsActive() || snapshot.Deadline is not { } deadline)
        {
            return null;
        }

        return new PactOfferMessage
        {
            MatchId = Id(snapshot.MatchId),
            OfferId = Id(MatchEngine.PactOfferIdFor(snapshot.MatchId, participant.PlayerId)),
            Options = [.. hero.PactOptions.Select(o => new PactOptionDto { PactId = PactId(o.Kind), Description = PactDescription(o.Kind), TargetId = o.Target?.Value })],
            Deadline = Timestamp(deadline),
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

        visible.AddRange(VisibleEvents(after.State, events, participant.PlayerId));

        return new RoundResolvedMessage
        {
            MatchId = Id(after.MatchId),
            Round = Math.Clamp(before.State.Round, 1, MaxRound),
            PreviousSeq = before.State.LastSeq,
            Seq = after.State.LastSeq,
            Delta = new PlayerViewDeltaDto
            {
                Status = StatusCode(after),
                FloorChanged = floorChanged ? current.RoomIndex + 1 : null,
                Me = Self(after, participant, current, context),

                // Son a lo sumo 3 y "decided" se reinicia en cada ronda: se envían siempre.
                AlliesChanged = [.. current.Allies.Select(a => Other(after, a, context))],
                EnemiesUpserted = [.. enemies.Where(e => !previousEnemies.TryGetValue(e.EnemyId, out var old) || old != Fingerprint(e))],
                EnemiesRemoved = [.. previousEnemies.Keys.Where(id => floorChanged || enemies.All(e => e.EnemyId != id))],
                TilesChanged = [.. Tiles(current).Where(t => !previousTiles.TryGetValue((t.X, t.Y), out var kind) || kind != t.Kind)],
                Events = [.. visible.Take(128)],
                HiddenTelegraphs = HiddenTelegraphs(current),
                Echoes = Echoes(current),
                GroundEmbers = Ground(current),
                NoiseTiles = [.. current.Noise.Select(Position)],
                Bell = Bell(after, current),
                RoundDeadline = RoundDeadline(after, participant.PlayerId),
            },
        };
    }

    /// <summary>
    /// Eventos de una confirmación que <paramref name="viewer"/> puede ver, proyectados con el estado
    /// posterior (lo mismo que recibe en vivo en <c>RoundResolved</c>; la reproducción lo reutiliza).
    /// </summary>
    public static List<IDictionary<string, object>> VisibleEvents(GameState after, IEnumerable<DomainEvent> events, PlayerId viewer) =>
        [.. PlayerViewProjector.VisibleEvents(after, events, viewer).Select(WithPhase).OfType<IDictionary<string, object>>()];

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
            Rounds = Math.Clamp(snapshot.State.Round, 0, MaxRound),
            Winners = [.. (outcome?.Winners ?? []).Select(w => w.Value)],
            Standings = standings,
            FinishedAt = Timestamp(now),
            ReplayUrl = $"{publicBaseUrl.TrimEnd('/')}/api/v1/matches/{Id(snapshot.MatchId)}/replay",
            Betrayals = [.. (outcome?.Betrayals ?? []).Select(b => new BetrayalDto { TraitorId = b.Traitor.Value, VictimId = b.Victim.Value, Damage = b.Damage })],
            Awards = [.. (outcome?.Awards ?? []).Select(AwardDtoOf)],
            Curses = [.. snapshot.State.Curses.Select(CurseCode)],
        };
    }

    private static string Count(int value, string one, string many) => $"{value} {(value == 1 ? one : many)}";

    private static AwardDto AwardDtoOf(Award award)
    {
        var (code, title, reason) = award.Kind switch
        {
            AwardKind.MostBetrayals => ("MOST_BETRAYALS", "El más traidor", Count(award.Value, "traición", "traiciones")),
            AwardKind.MostWardenDamage => ("MOST_WARDEN_DAMAGE", "Azote del Celador", $"{award.Value} de daño al Celador"),
            AwardKind.MostRevives => ("MOST_REVIVES", "Buen samaritano", $"Levantó a {Count(award.Value, "aliado", "aliados")}"),
            AwardKind.RichestDeath => ("RICHEST_DEATH", "Murió rico", $"Murió con {award.Value} de oro"),
            AwardKind.MostKills => ("MOST_KILLS", "Matamonstruos", Count(award.Value, "golpe final", "golpes finales")),
            AwardKind.MostChests => ("MOST_CHESTS", "Saqueador de cofres", $"Abrió {Count(award.Value, "cofre", "cofres")}"),
            _ => ("MOST_PUSHES", "Manos largas", Count(award.Value, "empujón", "empujones")),
        };
        return new AwardDto { AwardId = code, PlayerId = award.Player.Value, Title = title, Reason = reason, Value = award.Value };
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

        if (order.Step is { } step)
        {
            dto["step"] = Direction(step);
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
            Hp = Math.Clamp(self.Hp, 0, 10),
            MaxHp = Math.Clamp(self.MaxHp, 1, 10),
            Shield = Math.Clamp(self.Shield, 0, 10),
            Position = Position(self.Position),
            Facing = Direction(self.Facing),
            Embers = Math.Clamp(self.Embers, 0, 999),
            AbilityCooldown = Math.Clamp(self.SkillCooldown, 0, 12),
            SecondBody = self.SecondBody is { } second ? Position(second) : null,
            HostEnemyId = self.HostEnemyId,
            Debt = Math.Clamp(self.Debt, 0, 99),
            DebtTo = self.DebtTo?.Value,
            RewindTile = self.RewindTile is { } rewind ? Position(rewind) : null,
            RewindHp = self.RewindHp is { } rewindHp ? Math.Clamp(rewindHp, 0, 10) : null,
            DownRoundsLeft = self.Status == HeroStatus.Down ? Math.Clamp(HeroCatalog.RoundsUntilExtinct - self.DownRounds, 0, 3) : null,
            Blessings = [.. self.Blessings.Distinct().Select(BlessingId)],

            // Mientras elige no tiene misión; el objetivo solo lo recibe su dueño.
            Pact = self.PactOptions.IsEmpty ? new PactDto { PactId = PactId(self.Pact), Description = PactDescription(self.Pact), TargetId = self.PactTarget?.Value } : null,
            PushCooldown = Math.Clamp(self.PushCooldown, 0, HeroRules.PushCooldown),
            VisionRadius = Math.Clamp(self.VisionRadius, 1, 9),
            Decided = snapshot.Status == MatchStatus.InProgress && pending is not null,
            PendingOrder = snapshot.Status == MatchStatus.InProgress && pending is not null ? OrderDto(pending) : null,
            Path = SelfPath(snapshot, participant, self),
        };
    }

    /// <summary>Lo privado de los caminos; <c>null</c> mientras no haya nada que mostrar (héroe que nunca se extinguió).</summary>
    private static SelfPathDto? SelfPath(MatchSnapshot snapshot, MatchParticipant participant, SelfView self)
    {
        var path = self.Path;
        if (path.Extinctions == 0 && path.SavedEmbers == 0 && path.Echoes == 0)
        {
            return null;
        }

        var hero = snapshot.State.FindHero(participant.PlayerId);
        return new SelfPathDto
        {
            OfferId = path.OfferPending && hero is not null && snapshot.Status == MatchStatus.InProgress
                ? Id(MatchEngine.PathOfferIdFor(snapshot.MatchId, hero))
                : null,
            Choice = path.Choice is { } choice ? Path(choice) : null,
            SavedEmbers = Math.Clamp(path.SavedEmbers, 0, 999),
            Echoes = Math.Clamp(path.Echoes, 0, 99),
            RebirthEmbers = self.Status == HeroStatus.EmberSpirit && hero is not null ? Math.Clamp(PathRules.RebirthEmbers(hero, path.Echoes), 0, 999) : null,
            HinderCooldown = Math.Clamp(path.HinderCooldown, 0, 2),
            AwaitingSpawn = path.AwaitingSpawn ? (path.SpawnRound is null ? "NEXT_FLOOR" : "ROUND") : null,
            SpawnRound = path.AwaitingSpawn && path.SpawnRound is { } round ? Math.Clamp(round, 1, MaxRound) : null,
        };
    }

    /// <summary>Oferta de camino vigente del jugador (privada); <c>null</c> si no tiene que elegir.</summary>
    public static PathOfferMessage? PathOffer(MatchSnapshot snapshot, MatchParticipant participant, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(participant);
        var hero = snapshot.State.FindHero(participant.PlayerId);
        if (hero is not { IsChoosingPath: true } || snapshot.Status != MatchStatus.InProgress)
        {
            return null;
        }

        return new PathOfferMessage
        {
            MatchId = Id(snapshot.MatchId),
            OfferId = Id(MatchEngine.PathOfferIdFor(snapshot.MatchId, hero)),
            Options = [Path(PathKind.Friendship), Path(PathKind.Vengeance)],
            DefaultPath = Path(PathKind.Friendship),
            Deadline = Timestamp(snapshot.PathDeadline ?? snapshot.Deadline ?? now),
            SavedEmbers = Math.Clamp(hero.SavedEmbers, 0, 999),
            VengeanceEmbers = Math.Clamp(PathRules.RenegadeEmbers(hero), 0, 999),
            FriendshipEmbersMin = Math.Clamp(PathRules.RebirthEmbers(hero, 0), 0, 999),
            FriendshipEmbersMax = Math.Clamp(PathRules.RebirthEmbers(hero, int.MaxValue / 16), 0, 999),
            WardenRoom = snapshot.State.RoomIndex == snapshot.State.Rooms.Count - 1,
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
            Hp = Math.Clamp(ally.Hp, 0, 10),
            MaxHp = Math.Clamp(maxHp, 1, 10),
            Position = ally.Position is { } position ? Position(position) : null,
            Facing = ally.Facing is { } facing ? Direction(facing) : null,
            Embers = Math.Clamp(ally.Embers, 0, 999),
            BlessingCount = Math.Clamp(ally.BlessingCount, 0, 12),
            Connected = context.Connected.Contains(participant.UserId),
            Decided = snapshot.Status == MatchStatus.InProgress && context.Orders.ContainsKey(ally.Player),
            SecondBody = ally.SecondBody is { } second ? Position(second) : null,
            DebtToYou = ally.DebtToMe,
        };
    }

    private static string? RoundDeadline(MatchSnapshot snapshot, PlayerId player) =>
        snapshot.Status == MatchStatus.InProgress && snapshot.DeadlineFor(player) is { } deadline ? Timestamp(deadline) : null;

    /// <summary>Casillas anunciadas por enemigos que no se ven: solo las casillas, sin decir de quién son.</summary>
    private static List<PositionDto> HiddenTelegraphs(PlayerView view) =>
        [.. view.Telegraphs.Where(t => t.EnemyId is null).SelectMany(t => t.Tiles).Distinct().Select(Position)];

    private static List<EchoDto> Echoes(PlayerView view) =>
        [.. view.Echoes.Select(e => new EchoDto
        {
            OwnerId = e.Owner.Value,
            Position = Position(e.Origin),
            Direction = Direction(e.Direction),
            FireRound = Math.Clamp(e.FireRound, 1, MaxRound),
        })];

    private static List<GroundEmbersDto> Ground(PlayerView view) =>
        [.. view.Ground.Select(g => new GroundEmbersDto { Position = Position(g.Position), Amount = Math.Clamp(g.Amount, 1, 99) })];

    private static BellDto? Bell(MatchSnapshot snapshot, PlayerView view) =>
        view.BellRinger is { } ringer && snapshot.Status == MatchStatus.InProgress
            ? new BellDto
            {
                RingerId = ringer.Value,
                Deadline = snapshot.BellDeadline is { } deadline ? Timestamp(deadline) : null,
                Closed = view.BellClosed,
            }
            : null;

    /// <summary>Último en hablar: lo que oye el Campanero. Como Renegado, sin direcciones.</summary>
    public static LastWordMessage LastWord(Guid matchId, LastWordHeard heard)
    {
        ArgumentNullException.ThrowIfNull(heard);
        return new LastWordMessage
        {
            MatchId = Id(matchId),
            Round = Math.Clamp(heard.Round, 1, MaxRound),
            TypesOnly = heard.TypesOnly,
            Orders = [.. heard.Orders.Select(o => new HeardOrderDto
            {
                PlayerId = o.Player.Value,
                Type = OrderType(o.Order.Type),
                Direction = !heard.TypesOnly && o.Order.Direction is { } direction ? Direction(direction) : null,
                Step = !heard.TypesOnly && o.Order.Step is { } step ? Direction(step) : null,
            })],
        };
    }

    public static SignalReceivedMessage Signal(Guid matchId, MatchParticipant sender, QuickSignal signal, DateTimeOffset sentAt)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(signal);
        return new SignalReceivedMessage
        {
            MatchId = Id(matchId),
            FromPlayerId = sender.PlayerId.Value,
            Kind = SignalKindCode(signal.Kind),
            Position = signal.Tile is { } tile ? Position(tile) : null,
            SentAt = Timestamp(sentAt),
        };
    }

    public static string SignalKindCode(SignalKind kind) => kind switch
    {
        SignalKind.Here => "HERE",
        SignalKind.Danger => "DANGER",
        SignalKind.Help => "HELP",
        _ => "MINE",
    };

    public static SignalKind? ParseSignal(string? value) => value switch
    {
        "HERE" => SignalKind.Here,
        "DANGER" => SignalKind.Danger,
        "HELP" => SignalKind.Help,
        "MINE" => SignalKind.Mine,
        _ => null,
    };

    private static List<EnemyDto> Enemies(PlayerView view) =>
        [.. view.Enemies.Select(e => new EnemyDto
        {
            EnemyId = e.Id,
            Kind = EnemyKind(e.Kind),
            Hp = Math.Clamp(e.Hp, 0, MaxEnemyHp),
            MaxHp = Math.Clamp(e.MaxHp, 1, MaxEnemyHp),
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

    /// <summary>Evento del contrato. Solo para eventos ya filtrados (o para la vista completa de un administrador).</summary>
    internal static Dictionary<string, object>? VisibleEvent(DomainEvent domainEvent) => domainEvent switch
    {
        HeroMoved e => Event("MOVED", actor: e.Player.Value, position: e.To, detail: e.Body == 1 ? "SECOND_BODY" : null),
        HitPrevented { Reason: PreventionReason.WarningShield } e => Event("BLOCKED", actor: Ref(e.Source), target: Ref(e.Target), detail: "WARNING_SHIELD"),
        MoveBounced e => Event("BOUNCED", actor: e.Player.Value, position: e.Attempted, detail: e.Body == 1 ? "SECOND_BODY" : null),
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
        EmbersChanged { Delta: > 0 } e => Event("EMBERS_GAINED", actor: e.Player.Value, amount: e.Delta, detail: e.Reason switch
        {
            EmberReason.DebtCollected => "DEBT_COLLECTED",
            EmberReason.Assist => "ASSIST",
            EmberReason.WardenBounty => "WARDEN_BOUNTY",
            _ => null,
        }),
        EmbersChanged { Reason: EmberReason.MiserHit or EmberReason.Bribe or EmberReason.DebtSettled } e => Event("EMBERS_LOST", actor: e.Player.Value, amount: e.Delta, detail: EmberLossCode(e.Reason)),
        BlessingChosen e => Event("BLESSING_GAINED", actor: e.Player.Value),
        TrapTriggered e => Event("TRAP_TRIGGERED", position: e.Position),
        FireStarted e => Event("FIRE_STARTED", position: e.Position),
        WardenEnraged e => Event("BOSS_PHASE_CHANGED", target: EnemyRef(e.EnemyId)),
        RoomEntered e => Event("FLOOR_ENTERED", amount: e.RoomIndex + 1, detail: RoomTypeCode(e.Type)),
        PathOffered e => Event("PATH_OFFERED", actor: e.Player.Value, amount: e.SavedEmbers),
        PathSelected e => Event("PATH_SELECTED", actor: e.Player.Value, detail: e.Auto ? Path(e.Path) + "_AUTO" : Path(e.Path)),
        PathChosen e => Event("PATH_CHOSEN", actor: e.Player.Value, detail: Path(e.Path)),
        EmberReborn e => Event("EMBER_REBORN", actor: e.Player.Value, position: e.Position, amount: e.Hp),
        RenegadeSpawned e => Event("RENEGADE_SPAWNED", actor: e.Player.Value, position: e.Position),
        RenegadeDefeated e => Event("RENEGADE_DEFEATED", actor: e.By?.Value, target: e.Renegade.Value),
        EchoGained e => Event("ECHO_GAINED", actor: e.Player.Value, amount: e.Amount, detail: EchoReasonCode(e.Reason)),
        EmberAbilityUsed e => Event(EmberAbilityCode(e.Ability), actor: e.Player.Value, target: Ref(e.Target), position: e.Tile),
        EchoFired e => Event("ECHO_FIRED", actor: e.Player.Value, position: e.Origin, detail: Direction(e.Direction)),
        AttackCorrected e => Event("ATTACK_CORRECTED", target: e.EnemyId is { } id ? EnemyRef(id) : null, tiles: e.Tiles),
        BetrayalRecorded e => Event("BETRAYAL_RECORDED", actor: e.Traitor.Value, target: e.Victim.Value, amount: e.Damage, detail: e.Kind switch
        {
            BetrayalKind.FromHost => "FROM_HOST",
            _ => "DIVERTED",
        }),
        BetrayalRevealed e => Event("BETRAYAL_REVEALED", actor: e.Traitor.Value, target: e.Victim.Value, amount: e.Damage),
        HostBoarded e => Event("HOST_BOARDED", actor: e.Player.Value, target: EnemyRef(e.EnemyId)),
        HostExited e => Event("HOST_EXITED", actor: e.Player.Value, position: e.Tile),
        HostDrained e => Event("HOST_DRAINED", actor: e.Player.Value, target: EnemyRef(e.EnemyId)),
        WardenTurned e => Event("WARDEN_TURNED", target: EnemyRef(e.EnemyId), detail: Direction(e.Facing)),
        Shouted e => Event("SHOUTED", actor: e.Player.Value),
        DebtChanged e => Event("DEBT_CHANGED", actor: e.Creditor.Value, target: e.Debtor.Value, amount: e.Delta, detail: DebtReasonCode(e.Reason)),
        EmbersDropped e => Event("EMBERS_DROPPED", position: e.Tile, amount: e.Amount),
        EmbersPickedUp e => Event("EMBERS_PICKED_UP", actor: e.Player.Value, position: e.Tile, amount: e.Amount),
        EnemyBribed e => Event("ENEMY_BRIBED", actor: e.Player.Value, target: EnemyRef(e.EnemyId), amount: e.Cost),
        RoomShifted e => Event("ROOM_SHIFTED", actor: e.Player.Value, amount: e.Line, detail: $"{(e.Axis == ShiftAxis.Row ? "ROW" : "COLUMN")}_{Direction(e.Direction)}"),
        BellRung e => Event("BELL_RUNG", actor: e.Player.Value, amount: e.ForRound),
        BellExpired e => Event("BELL_EXPIRED", actor: e.Ringer.Value),
        Pushed e => Event("PUSHED", actor: e.Pusher.Value, target: Ref(e.Target), position: e.To),
        RoomFlooded e => Event("ROOM_FLOODED", amount: e.Rows),
        Chilled e => Event("CHILLED", amount: e.Damage),
        _ => null,
    };

    /// <summary>Evento con la fase de la ronda en la que ocurrió, para mostrar la resolución paso a paso.</summary>
    private static Dictionary<string, object>? WithPhase(DomainEvent domainEvent)
    {
        var dto = VisibleEvent(domainEvent);
        if (dto is not null && domainEvent.Phase is { } phase)
        {
            dto["phase"] = PhaseCode(phase);
        }

        return dto;
    }

    public static string PhaseCode(RoundPhase phase) => phase switch
    {
        RoundPhase.Reveal => "REVEAL",
        RoundPhase.Guard => "GUARD",
        RoundPhase.Shift => "SHIFT",
        RoundPhase.Movement => "MOVEMENT",
        RoundPhase.Actions => "ACTIONS",
        RoundPhase.EnemyAttacks => "ENEMY_ATTACKS",
        RoundPhase.EnemyMoves => "ENEMY_MOVES",
        RoundPhase.Cleanup => "CLEANUP",
        _ => "END",
    };

    internal static Dictionary<string, object> Event(
        string type, string? actor = null, string? target = null, Position? position = null, int? amount = null, string? detail = null, IReadOnlyList<Position>? tiles = null)
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

        if (position is { } p && OnBoard(p))
        {
            dto["position"] = new Dictionary<string, object> { ["x"] = p.X, ["y"] = p.Y };
        }

        if (amount is { } a)
        {
            dto["amount"] = Math.Clamp(a, -99, 99);
        }

        if (detail is not null)
        {
            dto["detail"] = detail;
        }

        if (tiles is not null)
        {
            dto["tiles"] = tiles.Where(OnBoard).Select(t => new Dictionary<string, object> { ["x"] = t.X, ["y"] = t.Y }).ToList();
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

    internal static PositionDto Position(Position p) => new() { X = p.X, Y = p.Y };

    public static string Direction(Direction direction) => direction switch
    {
        Domain.Model.Direction.North => "N",
        Domain.Model.Direction.South => "S",
        Domain.Model.Direction.East => "E",
        _ => "W",
    };

    /// <summary>Dentro de la sala más grande (11 de lado): la vista recorta a la sala real.</summary>
    private static bool OnBoard(Position p) => p.IsInside(Room.LargeSize, Room.LargeSize);

    public static string OrderType(OrderType type) => type switch
    {
        Domain.Model.OrderType.Guard => "GUARD",
        Domain.Model.OrderType.Move => "MOVE",
        Domain.Model.OrderType.Attack => "ATTACK",
        Domain.Model.OrderType.Skill => "ABILITY",
        Domain.Model.OrderType.Push => "PUSH",
        _ => "INTERACT",
    };

    /// <summary>Mismo nombre que en el REST (ERRATA, ANTEAYER, HILVAN…): el del enum en mayúsculas.</summary>
    public static string Hero(HeroClass hero) => hero.ToString().ToUpperInvariant();

    public static string HeroState(HeroStatus status) => status switch
    {
        HeroStatus.Active => "ALIVE",
        HeroStatus.Down => "DOWN",
        HeroStatus.Extinct => "EXTINCT",
        HeroStatus.EmberSpirit => "EMBER_SPIRIT",
        HeroStatus.Renegade => "RENEGADE",
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

    public static string EnemyKind(EnemyKind kind) => kind switch
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

    public static string PactId(PactKind pact) => pact switch
    {
        PactKind.SlayWarden => "FINAL_BLOW_WARDEN",
        PactKind.NeverFall => "NEVER_DOWNED",
        PactKind.OpenThreeChests => "OPEN_3_CHESTS",
        PactKind.ReviveTwoAllies => "REVIVE_2_ALLIES",
        PactKind.MostWardenDamage => "MOST_WARDEN_DAMAGE",
        PactKind.BetrayTwiceUnconvicted => "BETRAY_TWICE_UNCONVICTED",
        PactKind.OutgoldTarget => "OUTGOLD_TARGET",
        PactKind.PushTwoIntoHazard => "PUSH_2_INTO_HAZARD",
        PactKind.CorrectConviction => "CORRECT_CONVICTION",
        _ => "BETRAY_TWICE_UNSEEN",
    };

    public static string PactDescription(PactKind pact) => pact switch
    {
        PactKind.SlayWarden => "Dar el golpe final al Celador.",
        PactKind.NeverFall => "Terminar sin que te derriben.",
        PactKind.OpenThreeChests => "Abrir 3 cofres.",
        PactKind.ReviveTwoAllies => "Levantar a 2 aliados.",
        PactKind.MostWardenDamage => "Hacer el mayor daño al Celador.",
        PactKind.BetrayTwiceUnconvicted => "Traicionar 2 veces sin que te condenen (ya no se ofrece).",
        PactKind.OutgoldTarget => "Que tu objetivo termine con menos oro que tú.",
        PactKind.PushTwoIntoHazard => "Empujar a 2 enemigos a trampas o grietas.",
        PactKind.CorrectConviction => "Acertar una condena en El Juicio (ya no se ofrece).",
        _ => "Golpear 2 veces a un aliado por la espalda o con una traición anónima.",
    };

    public static PactKind? ParsePact(string? value) =>
        PactRules.Offered.Select(p => (PactKind?)p).FirstOrDefault(p => PactId(p!.Value) == value);

    public static string RoomTypeCode(RoomType type) => type switch
    {
        RoomType.Treasure => "TREASURE",
        RoomType.Arena => "ARENA",
        RoomType.Sanctuary => "SANCTUARY",
        RoomType.Dark => "DARK",
        RoomType.Flooded => "FLOODED",
        _ => "NORMAL",
    };

    public static string CurseCode(Curse curse) => curse switch
    {
        Curse.Fierce => "FIERCE",
        Curse.WardenPlus => "WARDEN_PLUS",
        Curse.Dim => "DIM",
        _ => "HASTE",
    };

    public static Curse? ParseCurse(string? value) =>
        Enum.GetValues<Curse>().Select(c => (Curse?)c).FirstOrDefault(c => CurseCode(c!.Value) == value);

    public static string Outcome(OutcomeKind? kind) => kind switch
    {
        OutcomeKind.Victory => "WARDEN_DEFEATED",
        OutcomeKind.Draw => "DRAW",
        OutcomeKind.CollectiveDefeat => "EMBER_EXTINGUISHED",
        OutcomeKind.ThresholdClaimed => "THRESHOLD_CLAIMED",
        _ => "ABANDONED",
    };

    public static string Path(PathKind path) => path == PathKind.Vengeance ? "VENGEANCE" : "FRIENDSHIP";

    private static string EchoReasonCode(EchoReason reason) => reason switch
    {
        EchoReason.ShieldAbsorbed => "SHIELD_ABSORBED",
        EchoReason.Rekindled => "REKINDLED",
        EchoReason.AttackHindered => "ATTACK_HINDERED",
        EchoReason.BeaconKill => "BEACON_KILL",
        _ => "WARMTH",
    };

    private static string EmberLossCode(EmberReason reason) => reason switch
    {
        EmberReason.Bribe => "BRIBE",
        EmberReason.DebtSettled => "DEBT_SETTLED",
        _ => "HIT",
    };

    private static string DebtReasonCode(DebtReason reason) => reason switch
    {
        DebtReason.Loan => "LOAN",
        DebtReason.Interest => "INTEREST",
        DebtReason.Paid => "PAID",
        DebtReason.Extinction => "EXTINCTION",
        DebtReason.MatchEnd => "MATCH_END",
        _ => "CANCELLED",
    };

    private static string EmberAbilityCode(EmberAbility ability) => ability switch
    {
        EmberAbility.Kindle => "SHIELD_KINDLED",
        EmberAbility.Hinder => "ATTACK_HINDERED",
        EmberAbility.Beacon => "BEACON_LIT",
        EmberAbility.Warmth => "WARMTH_HEALED",
        _ => "REVIVED",
    };

    /// <summary>Del texto del contrato al dominio. <c>null</c> si no es un valor válido.</summary>
    public static OrderType? ParseOrderType(string? value) => value switch
    {
        "GUARD" => Domain.Model.OrderType.Guard,
        "MOVE" => Domain.Model.OrderType.Move,
        "ATTACK" => Domain.Model.OrderType.Attack,
        "ABILITY" => Domain.Model.OrderType.Skill,
        "INTERACT" => Domain.Model.OrderType.Interact,
        "PUSH" => Domain.Model.OrderType.Push,
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

    public static PathKind? ParsePath(string? value) => value switch
    {
        "FRIENDSHIP" => PathKind.Friendship,
        "VENGEANCE" => PathKind.Vengeance,
        _ => null,
    };

    public static Blessing? ParseBlessing(string? value) =>
        Enum.GetValues<Blessing>().Select(b => (Blessing?)b).FirstOrDefault(b => BlessingId(b!.Value) == value);

    public static HeroClass? ParseHero(string? value) =>
        Enum.GetValues<HeroClass>().Select(h => (HeroClass?)h).FirstOrDefault(h => Hero(h!.Value) == value);
}
