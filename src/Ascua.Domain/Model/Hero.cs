using System.Collections.Immutable;

namespace Ascua.Domain.Model;

/// <summary>
/// Los nueve héroes (planteamiento/05-heroes.md): cada uno rompe una regla del juego. En el cable viajan con
/// el nombre del enum en mayúsculas (ERRATA, ANTEAYER, HILVAN…).
/// </summary>
public enum HeroClass
{
    /// <summary>Errata: ve todos los anuncios (Fe de erratas) y los desvía (Corregir).</summary>
    Errata,

    /// <summary>Anteayer: sus ataques se repiten 2 rondas después (Eco) y puede saltar atrás (Volver).</summary>
    Anteayer,

    /// <summary>Hilván: dos cuerpos en espejo y una sola vida (Espejo); daña lo que haya entre ellos (Tensar).</summary>
    Hilvan,

    /// <summary>Polizón: entra en un monstruo y lo controla, invisible para todos (Sin casa, Abordar).</summary>
    Polizon,

    /// <summary>La Escucha: solo percibe lo cercano y lo que hizo ruido (Ciega); revela la sala a su bando (Grito).</summary>
    Escucha,

    /// <summary>La Usurera: cura y levanta creando deudas de ascuas (Préstamo, Interés).</summary>
    Usurera,

    /// <summary>El Avaro: su vida son sus ascuas (Todo lo que tengo); paga para frenar enemigos (Soborno).</summary>
    Avaro,

    /// <summary>El Mampostero: ignora pinchos y grietas (Conoce las piedras); desliza filas y columnas (Corrimiento).</summary>
    Mampostero,

    /// <summary>El Campanero: oye las órdenes si decide último (Último en hablar); acorta el reloj ajeno (Repique).</summary>
    Campanero,
}

public enum HeroStatus
{
    Active,

    /// <summary>Caído: vida 0, no actúa; puede ser levantado.</summary>
    Down,

    /// <summary>Extinto: fuera del tablero; elige camino (planteamiento/04-caminos-tras-morir.md).</summary>
    Extinct,

    /// <summary>Retirado por abandono (venció la gracia de desconexión).</summary>
    Retired,

    /// <summary>Brasa errante (Camino de la Amistad): espíritu intangible que ayuda al grupo y gana ecos.</summary>
    EmberSpirit,

    /// <summary>Renegado (Camino de la Venganza): vuelve con su héroe del lado de la mazmorra.</summary>
    Renegade,
}

public sealed record HeroStats(int MaxHp, int Range, int Damage, int Initiative, int SkillCooldown);

public static class HeroCatalog
{
    public const int VisionRadius = 3;
    public const int ReviveHp = 2;
    public const int RoundsUntilExtinct = 3;

    public static HeroStats For(HeroClass heroClass) => heroClass switch
    {
        HeroClass.Errata => new(MaxHp: 4, Range: 1, Damage: 1, Initiative: 6, SkillCooldown: 3),
        HeroClass.Anteayer => new(MaxHp: 4, Range: 1, Damage: 1, Initiative: 5, SkillCooldown: 5),
        HeroClass.Hilvan => new(MaxHp: 8, Range: 1, Damage: 1, Initiative: 3, SkillCooldown: 3),
        HeroClass.Polizon => new(MaxHp: 4, Range: 1, Damage: 1, Initiative: 7, SkillCooldown: 3),
        HeroClass.Escucha => new(MaxHp: 5, Range: 4, Damage: 1, Initiative: 2, SkillCooldown: 4),
        HeroClass.Usurera => new(MaxHp: 4, Range: 1, Damage: 1, Initiative: 1, SkillCooldown: 2),

        // El Avaro no tiene barra de vida: 1 mientras está en pie; lo que aguanta son sus ascuas.
        HeroClass.Avaro => new(MaxHp: 1, Range: 1, Damage: 1, Initiative: 2, SkillCooldown: 2),
        HeroClass.Mampostero => new(MaxHp: 6, Range: 1, Damage: 1, Initiative: 1, SkillCooldown: 3),
        HeroClass.Campanero => new(MaxHp: 5, Range: 2, Damage: 1, Initiative: 4, SkillCooldown: 4),
        _ => throw new ArgumentOutOfRangeException(nameof(heroClass), heroClass, null),
    };
}

/// <summary>Números de las pasivas y activas de los nueve héroes (valores finales en planteamiento/05-heroes.md).</summary>
public static class HeroRules
{
    /// <summary>Corregir alcanza a los enemigos con alguna casilla anunciada a esta distancia de Errata.</summary>
    public const int CorrectReach = 2;

    /// <summary>Rondas entre un ataque de Anteayer y su Eco.</summary>
    public const int EchoDelay = 2;

    /// <summary>Volver salta a donde estaba Anteayer hace estas rondas.</summary>
    public const int RewindDelay = 2;

    /// <summary>Separación con la que aparecen los cuerpos de Hilván.</summary>
    public const int BodySpacing = 2;

    public const int TugDamage = 2;

    /// <summary>Cada cuántas rondas dentro Polizón drena a su huésped.</summary>
    public const int DrainEvery = 2;

    public const int DrainAmount = 1;

    /// <summary>Daño a Polizón si otro mata a su huésped.</summary>
    public const int HostDeathDamage = 1;

    /// <summary>Enfriamiento de Abordar al girar al Celador (el normal cuenta desde que sale del huésped).</summary>
    public const int WardenTurnCooldown = 6;

    public const int BlindVisionRadius = 1;

    /// <summary>A tientas: daño extra si el objetivo no hizo ruido.</summary>
    public const int QuietTargetBonus = 1;

    public const int LoanReach = 2;
    public const int LoanHeal = 3;
    public const int LoanReviveHp = 3;
    public const int LoanDebt = 1;
    public const int InterestEvery = 5;
    public const int DebtCap = 4;

    public const int MiserStartEmbers = 4;
    public const int MiserReviveEmbers = 2;
    public const int MiserRekindleEmbers = 1;

    /// <summary>Con la fórmula del camino, el Avaro vuelve con al menos estas ascuas.</summary>
    public const int MiserMinReturnEmbers = 2;

    public const int BribeReach = 3;
    public const int BribeCost = 1;
    public const int WardenBribeCost = 3;

    /// <summary>Daño de Corrimiento a la criatura que sale por un borde y entra por el otro.</summary>
    public const int WrapDamage = 2;

    /// <summary>Alcance del sabotaje, en línea.</summary>
    public const int SabotageReach = 2;

    public const int SabotageDamage = 1;

    /// <summary>Espera del empujón (usado en la ronda N, disponible en N+3).</summary>
    public const int PushCooldown = 3;
}

/// <summary>Ataque de Anteayer que se repetirá en <see cref="FireRound"/> desde <see cref="Origin"/>.</summary>
public sealed record PendingEcho(int FireRound, Position Origin, Direction Direction);

/// <summary>Dónde estaba Anteayer y con cuánta vida al empezar a resolverse esa ronda (para Volver).</summary>
public sealed record HeroTrace(int Round, Position Position, int Hp);

public sealed record Hero
{
    public required PlayerId Player { get; init; }
    public required int Seat { get; init; }
    public required HeroClass Class { get; init; }
    public Position Position { get; init; }
    public Direction Facing { get; init; } = Direction.North;
    public int Hp { get; init; }
    public int Shield { get; init; }
    public int Embers { get; init; }
    public HeroStatus Status { get; init; }
    public int DownRounds { get; init; }
    public int SkillCooldown { get; init; }
    public bool Frozen { get; init; }
    public int BurnRounds { get; init; }
    public int BurnDamage { get; init; }
    public int SilverKeys { get; init; }
    public bool LastBreathUsed { get; init; }
    public ImmutableList<Blessing> Blessings { get; init; } = [];
    public ImmutableList<Blessing> DraftOptions { get; init; } = [];
    public PactKind Pact { get; init; }

    /// <summary>Jugador objetivo de la misión <see cref="PactKind.OutgoldTarget"/>. Solo lo ve su dueño.</summary>
    public PlayerId? PactTarget { get; init; }

    /// <summary>Las 2 misiones entre las que elige al empezar; vacío cuando ya eligió.</summary>
    public ImmutableList<PactOffer> PactOptions { get; init; } = [];

    public PactProgress Progress { get; init; } = new();

    /// <summary>Sala en la que ya saboteó (-1 = ninguna). Es una vez por sala.</summary>
    public int SabotagedRoom { get; init; } = -1;

    /// <summary>Rondas que faltan para poder volver a empujar.</summary>
    public int PushCooldown { get; init; }

    /// <summary>Hilván: su segundo cuerpo. <c>null</c> para los demás o mientras no tenga cuerpo en el tablero.</summary>
    public Position? SecondBody { get; init; }

    /// <summary>Anteayer: ecos programados, el más próximo primero.</summary>
    public ImmutableList<PendingEcho> EchoQueue { get; init; } = [];

    /// <summary>Anteayer: dónde estuvo en las últimas rondas de esta sala (para Volver).</summary>
    public ImmutableList<HeroTrace> History { get; init; } = [];

    /// <summary>Polizón: id del enemigo en el que va. Mientras tanto, <see cref="Position"/> sigue a su huésped.</summary>
    public int? HostEnemyId { get; init; }

    /// <summary>Polizón: ronda en la que entró al huésped (el drenaje se cuenta desde ahí).</summary>
    public int BoardedRound { get; init; }

    /// <summary>La Escucha usó Grito: su bando ve la sala entera hasta la próxima resolución.</summary>
    public bool Shouting { get; init; }

    /// <summary>Ascuas que debe a la Usurera (solo las ven el deudor y ella).</summary>
    public int Debt { get; init; }

    /// <summary>A quién le debe.</summary>
    public PlayerId? DebtTo { get; init; }

    /// <summary>Ronda en la que empezó la deuda vigente (el interés se cuenta desde ahí).</summary>
    public int DebtSinceRound { get; init; }

    /// <summary>Avaro: máximo de ascuas que llegó a tener (decide sus ascuas guardadas al extinguirse).</summary>
    public int PeakEmbers { get; init; }

    /// <summary>Ascuas apartadas al extinguirse; cada camino decide cuántas recupera (privado).</summary>
    public int SavedEmbers { get; init; }

    /// <summary>Ecos ganados como Brasa errante (privado). Suben el porcentaje de ascuas al renacer.</summary>
    public int Echoes { get; init; }

    /// <summary>Veces que se extinguió; identifica cada oferta de camino.</summary>
    public int Extinctions { get; init; }

    /// <summary>Extinto con una elección de camino abierta (se revela al resolver la ronda siguiente).</summary>
    public bool PathOfferPending { get; init; }

    /// <summary>Camino elegido y aún no revelado (privado hasta que se resuelve la ronda).</summary>
    public PathKind? PathChoice { get; init; }

    /// <summary>Brasa: enfriamiento de Estorbo (el de Avivar es <see cref="SkillCooldown"/>).</summary>
    public int HinderCooldown { get; init; }

    /// <summary>Rondas que faltan para que la Calidez de una Brasa pueda volver a curar a este héroe.</summary>
    public int WarmthCooldown { get; init; }

    /// <summary>Brasas que dieron cada punto de escudo de Avivar (uno por punto; se consumen primero).</summary>
    public ImmutableList<PlayerId> EmberShieldBy { get; init; } = [];

    /// <summary>Brasa: usó Faro esta ronda (ilumina un radio de 2 para el grupo hasta la próxima resolución).</summary>
    public bool BeaconActive { get; init; }

    /// <summary>Renegado que todavía no reapareció (espera la sala siguiente o su ronda en la del Celador).</summary>
    public bool AwaitingSpawn { get; init; }

    /// <summary>Renegado en la sala del Celador: ronda en la que reaparece (0 = al entrar a la sala siguiente).</summary>
    public int SpawnRound { get; init; }

    /// <summary>Héroes a los que este Renegado ya dio su primer golpe (lo absorbió el escudo de aviso).</summary>
    public ImmutableList<PlayerId> WarnedHeroes { get; init; } = [];

    public HeroStats Stats => HeroCatalog.For(Class);

    /// <summary>Vida máxima. El Avaro siempre tiene 1: sus bendiciones de vida actúan sobre sus ascuas.</summary>
    public int MaxHp => IsMiser ? Stats.MaxHp : Stats.MaxHp + (Has(Blessing.DoubleHeart) ? 2 : 0) - (Has(Blessing.StoneFist) ? 1 : 0);

    /// <summary>Daño básico. El Renegado hace como máximo 1 por golpe (sin Puño de piedra).</summary>
    public int Damage => IsRenegade ? PathRules.RenegadeMaxDamage : Stats.Damage;

    /// <summary>Su radio de visión, sin la sala ni las maldiciones (ver <see cref="GameState.VisionOf"/>).</summary>
    public int VisionRadius =>
        Math.Max(1, (IsEmber ? PathRules.EmberVisionRadius : IsBlind ? HeroRules.BlindVisionRadius : HeroCatalog.VisionRadius) - (Has(Blessing.OwlEye) ? 1 : 0));

    /// <summary>Corazón doble: nada lo cura. El Avaro tampoco se cura: su vida son sus ascuas.</summary>
    public bool CanHeal => !IsMiser && !Has(Blessing.DoubleHeart);

    /// <summary>Último aliento ya gastado: si vuelve a caer, nadie puede levantarlo.</summary>
    public bool CanBeRevived => !(Has(Blessing.LastBreath) && LastBreathUsed);

    public bool CanSabotage(int roomIndex) => IsActive && !IsInHost && SabotagedRoom != roomIndex;

    /// <summary>Empujan los héroes activos y los Renegados presentes, salvo dentro de un huésped.</summary>
    public bool CanPush => (IsActive || IsPresentRenegade) && !IsInHost;

    /// <summary>Enfriamiento que deja la habilidad al usarse (×2 para el Renegado).</summary>
    public int SkillCooldownOnUse => Stats.SkillCooldown * (IsRenegade ? PathRules.RenegadeCooldownFactor : 1);

    public bool IsActive => Status == HeroStatus.Active;
    public bool IsEmber => Status == HeroStatus.EmberSpirit;
    public bool IsRenegade => Status == HeroStatus.Renegade;

    /// <summary>Renegado ya presente en la sala: tiene cuerpo, bloquea, ataca y puede ser derrotado.</summary>
    public bool IsPresentRenegade => IsRenegade && !AwaitingSpawn;

    /// <summary>Sigue en juego dentro de la sala: héroes activos o caídos y Renegados presentes (la Brasa es intangible).</summary>
    public bool IsOnBoard => Status is HeroStatus.Active or HeroStatus.Down || IsPresentRenegade;

    /// <summary>Da órdenes en la ronda: héroe activo, Brasa errante o Renegado presente.</summary>
    public bool CanAct => IsActive || IsEmber || IsPresentRenegade;

    /// <summary>Puede recibir daño y curación: héroe activo o Renegado presente.</summary>
    public bool IsVulnerable => IsActive || IsPresentRenegade;

    /// <summary>Extinto con la elección de camino todavía abierta (sin elegir).</summary>
    public bool IsChoosingPath => PathOfferPending && PathChoice is null;

    /// <summary>Polizón dentro de un monstruo: nadie lo ve y no ocupa casilla.</summary>
    public bool IsInHost => HostEnemyId is not null;

    /// <summary>Ocupa casilla: se le puede golpear, bloquea el paso y otros lo encuentran en el tablero.</summary>
    public bool HasBody => IsOnBoard && !IsInHost;

    /// <summary>El Avaro: su vida son sus ascuas.</summary>
    public bool IsMiser => Class == HeroClass.Avaro;

    /// <summary>La Escucha con su héroe (como Brasa ve como cualquier Brasa).</summary>
    public bool IsBlind => Class == HeroClass.Escucha && !IsEmber;

    /// <summary>Errata con su héroe: ve todos los anuncios de la sala.</summary>
    public bool SeesAllTelegraphs => Class == HeroClass.Errata && !IsEmber;

    /// <summary>Casillas de sus cuerpos: una, o dos si es Hilván en el tablero.</summary>
    public IEnumerable<Position> Bodies
    {
        get
        {
            yield return Position;
            if (SecondBody is { } second)
            {
                yield return second;
            }
        }
    }

    public bool Occupies(Position position) => Position == position || SecondBody == position;

    /// <summary>Distancia desde su cuerpo más cercano.</summary>
    public int DistanceTo(Position position) =>
        SecondBody is { } second ? Math.Min(Position.DistanceTo(position), second.DistanceTo(position)) : Position.DistanceTo(position);

    /// <summary>Su cuerpo más cercano a una casilla (en empate, el principal).</summary>
    public Position NearestBody(Position position) =>
        SecondBody is { } second && second.DistanceTo(position) < Position.DistanceTo(position) ? second : Position;

    /// <summary>Mismo bando: el grupo (héroes, caídos, extintos y Brasas) o la mazmorra (Renegados).</summary>
    public bool IsSameFaction(Hero other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return IsRenegade == other.IsRenegade;
    }

    public bool Has(Blessing blessing) => Blessings.Contains(blessing);

    public bool HasSynergy(Synergy synergy) => BlessingRules.SynergiesOf(Blessings).Contains(synergy);

    public static Hero Create(PlayerId player, int seat, HeroClass heroClass, Position position)
    {
        var embers = heroClass == HeroClass.Avaro ? HeroRules.MiserStartEmbers : 0;
        return new Hero
        {
            Player = player,
            Seat = seat,
            Class = heroClass,
            Position = position,
            Hp = HeroCatalog.For(heroClass).MaxHp,
            Embers = embers,
            PeakEmbers = embers,
        };
    }
}
