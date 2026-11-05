using System.Collections.Immutable;

namespace Ascua.Domain.Model;

/// <summary>Las 12 mejoras: cada una cambia una regla y tiene un costo visible (06-version-3.md §8).</summary>
public enum Blessing
{
    /// <summary>Brasa viva: tus golpes siempre queman; recibes 1 al abrir cofres.</summary>
    LivingEmber,

    /// <summary>Paso ígneo: cada paso deja fuego; el fuego te hace 1 más a ti.</summary>
    FireStep,

    /// <summary>Puño de piedra: +1 de daño si atacas sin dar un paso; −1 de vida máxima (−1 ascua para el Avaro).</summary>
    StoneFist,

    /// <summary>Corazón doble: +2 de vida máxima (+1 ascua para el Avaro); nada te cura.</summary>
    DoubleHeart,

    /// <summary>Hoja hambrienta: cada golpe final cura 1; no puedes defender.</summary>
    HungryBlade,

    /// <summary>Espejo roto: todo golpe con autor que recibes le devuelve 1; no puedes tener escudo.</summary>
    BrokenMirror,

    /// <summary>Lámpara: ves toda la sala; los monstruos te eligen siempre.</summary>
    Lamp,

    /// <summary>Ganzúa: tus cofres dan +2 ascuas; no puedes levantar a aliados con INTERACT.</summary>
    Lockpick,

    /// <summary>Último aliento: una vez, al caer quedas con 1 de vida (1 ascua para el Avaro); si vuelves a caer, nadie te levanta.</summary>
    LastBreath,

    /// <summary>Codicia: +1 ascua por golpe final; cada golpe que recibes hace +1.</summary>
    Greed,

    /// <summary>Escarcha: tus golpes congelan y no hacen daño (al Celador y a un Renegado, que no se congelan, sí).</summary>
    Frost,

    /// <summary>Ojo de búho: ves todos los ataques anunciados de la sala; visión −1, mínimo 1.</summary>
    OwlEye,
}

/// <summary>Sinergias (recetas de dos mejoras).</summary>
public enum Synergy
{
    /// <summary>Infierno = Brasa viva + Paso ígneo: el fuego no te daña y tu fuego dura 1 ronda más.</summary>
    Inferno,

    /// <summary>Coraza viva = Espejo roto + Corazón doble: tu reflejo hace 2.</summary>
    LivingArmor,

    /// <summary>Saqueador = Codicia + Ganzúa: ves todos los cofres de la sala y si traen llave.</summary>
    Plunderer,
}

/// <summary>Texto de una mejora tal como se muestra al jugador. Lo sirve <c>GET /api/v1/catalog</c>.</summary>
public sealed record BlessingInfo(Blessing Blessing, string Name, string Effect, string Cost);

/// <summary>Texto de una sinergia.</summary>
public sealed record SynergyInfo(Synergy Synergy, string Name, Blessing First, Blessing Second, string Effect);

/// <summary>Catálogo con los nombres que se ven en pantalla (docs/glosario.md).</summary>
public static class BlessingCatalog
{
    public static IReadOnlyList<BlessingInfo> All { get; } =
    [
        new(Blessing.LivingEmber, "Brasa viva", "Tus golpes siempre queman: 1 de daño más al terminar la ronda.", "Recibes 1 de daño al abrir cofres."),
        new(Blessing.FireStep, "Paso ígneo", "Cada paso deja fuego en la casilla que dejas.", "El fuego te hace 1 más a ti."),
        new(Blessing.StoneFist, "Puño de piedra", "Si atacas sin dar un paso, haces +1 de daño.", "−1 de vida máxima (el Avaro, −1 de oro)."),
        new(Blessing.DoubleHeart, "Corazón doble", "+2 de vida máxima (el Avaro, +1 de oro).", "Nada te cura."),
        new(Blessing.HungryBlade, "Hoja hambrienta", "Cada golpe final te cura 1 (el Avaro, +1 de oro).", "No puedes defender."),
        new(Blessing.BrokenMirror, "Espejo roto", "Todo golpe con autor que recibes le devuelve 1 de daño.", "No puedes tener escudo."),
        new(Blessing.Lamp, "Lámpara", "Ves toda la sala.", "Los monstruos te eligen siempre."),
        new(Blessing.Lockpick, "Ganzúa", "Tus cofres dan +2 de oro.", "No puedes levantar a aliados con Usar."),
        new(Blessing.LastBreath, "Último aliento", "Una vez: al caer quedas con 1 de vida.", "Si vuelves a caer, nadie puede levantarte."),
        new(Blessing.Greed, "Codicia", "+1 de oro por cada golpe final.", "Cada golpe que recibes hace +1."),
        new(Blessing.Frost, "Escarcha", "Tus golpes congelan: pierde su próxima acción.", "Tus golpes no hacen daño (al Celador y a un Traidor, que no se congelan, sí)."),
        new(Blessing.OwlEye, "Ojo de búho", "Ves todos los ataques marcados de la sala.", "Visión −1, mínimo 1."),
    ];

    public static IReadOnlyList<SynergyInfo> Synergies { get; } =
    [
        new(Synergy.Inferno, "Infierno", Blessing.LivingEmber, Blessing.FireStep, "El fuego no te daña y tu fuego dura 1 ronda más."),
        new(Synergy.LivingArmor, "Coraza viva", Blessing.BrokenMirror, Blessing.DoubleHeart, "Tu reflejo hace 2 de daño."),
        new(Synergy.Plunderer, "Saqueador", Blessing.Greed, Blessing.Lockpick, "Ves todos los cofres de la sala y si traen llave."),
    ];

    public static BlessingInfo Of(Blessing blessing) => All.First(b => b.Blessing == blessing);
}

public static class BlessingRules
{
    public static IReadOnlyList<Blessing> All { get; } = Enum.GetValues<Blessing>();

    public static ImmutableList<Synergy> SynergiesOf(IReadOnlyCollection<Blessing> blessings)
    {
        var result = ImmutableList.CreateBuilder<Synergy>();
        if (blessings.Contains(Blessing.LivingEmber) && blessings.Contains(Blessing.FireStep))
        {
            result.Add(Synergy.Inferno);
        }

        if (blessings.Contains(Blessing.BrokenMirror) && blessings.Contains(Blessing.DoubleHeart))
        {
            result.Add(Synergy.LivingArmor);
        }

        if (blessings.Contains(Blessing.Greed) && blessings.Contains(Blessing.Lockpick))
        {
            result.Add(Synergy.Plunderer);
        }

        return result.ToImmutable();
    }

    /// <summary>Añade la bendición y aplica sus efectos inmediatos sobre la vida.</summary>
    public static Hero Grant(Hero hero, Blessing blessing)
    {
        if (hero.Has(blessing))
        {
            return hero;
        }

        var updated = hero with { Blessings = hero.Blessings.Add(blessing) };
        if (blessing == Blessing.BrokenMirror)
        {
            updated = updated with { Shield = 0, EmberShieldBy = [] };
        }

        if (hero.IsMiser)
        {
            var embers = blessing switch
            {
                Blessing.DoubleHeart => hero.Embers + 1,
                Blessing.StoneFist => Math.Max(0, hero.Embers - 1),
                _ => hero.Embers,
            };
            return updated with { Embers = embers, PeakEmbers = Math.Max(hero.PeakEmbers, embers) };
        }

        return blessing switch
        {
            Blessing.DoubleHeart => updated with { Hp = updated.Hp + 2 },
            Blessing.StoneFist => updated with { Hp = Math.Max(1, Math.Min(updated.Hp, updated.MaxHp)) },
            _ => updated,
        };
    }
}
