using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Resolución pura y determinista de una ronda: (estado, órdenes, azar) → (estado nuevo, eventos).
/// Orden fijo (planteamiento/05-heroes.md, §3): 0) se revelan los caminos elegidos; 1) GUARDAR y Volver;
/// 2) Corrimiento; 3) movimientos con rebote (Espejo de Hilván, Polizón mueve a su huésped); 4) ataques,
/// habilidades e interacciones por iniciativa, con el Eco de Anteayer justo después de su dueño; 5) los enemigos
/// ejecutan lo anunciado; 6) los enemigos se mueven y anuncian (salvo el huésped); 7) disparadores y limpieza.
/// </summary>
public static class Resolver
{
    public static (GameState Next, IReadOnlyList<DomainEvent> Events) Resolve(
        GameState state,
        IReadOnlyDictionary<PlayerId, Order> orders,
        IRandom rng)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(rng);
        if (state.Phase != MatchPhase.InProgress)
        {
            throw new InvalidOperationException("Solo se resuelven rondas de partidas en curso.");
        }

        var ctx = new RoundContext(state, rng);
        Paths.RevealChoices(ctx);
        HeroSkills.ClearShouts(ctx);
        HeroSkills.RecordHistory(ctx);
        var downAtStart = state.Heroes.Where(h => h.Status == HeroStatus.Down).Select(h => h.Player).ToHashSet();
        var plan = PlanOrders(ctx, orders);

        ActivateGuards(ctx, plan);
        HeroSkills.Rewind(ctx, plan);
        RoomShift.Resolve(ctx, plan);
        HeroMovement.Resolve(ctx, plan);
        HeroActions.Resolve(ctx, plan);
        EnemyTurn.ExecuteTelegraphs(ctx);
        EnemyTurn.MoveAndAnnounce(ctx);
        Cleanup.Run(ctx, downAtStart);

        return Finish(ctx);
    }

    /// <summary>
    /// Orden efectiva de cada héroe que podía actuar al abrir la ronda (activo, Brasa o Renegado presente).
    /// Faltante o inválida → GUARDAR (Faro, para la Brasa). Congelado → pierde la acción.
    /// </summary>
    private static Dictionary<PlayerId, Order> PlanOrders(RoundContext ctx, IReadOnlyDictionary<PlayerId, Order> orders)
    {
        var plan = new Dictionary<PlayerId, Order>();
        foreach (var hero in ctx.Heroes.Where(h => h.CanAct && ctx.Start.FindHero(h.Player)!.CanAct).OrderBy(h => h.Seat).ToList())
        {
            if (hero.Frozen)
            {
                ctx.Update(hero with { Frozen = false });
                ctx.Emit(new ActionLost(EntityRef.ForHero(hero.Player)) { Audience = RoundContext.AudienceOf(hero) });
                continue;
            }

            var order = orders.TryGetValue(hero.Player, out var submitted) ? submitted : Order.DefaultGuard;
            if (OrderValidator.Validate(ctx.Start, hero.Player, ctx.Round, order) is not null)
            {
                order = Order.DefaultGuard;
            }

            plan[hero.Player] = order;
        }

        return plan;
    }

    private static void ActivateGuards(RoundContext ctx, Dictionary<PlayerId, Order> plan)
    {
        foreach (var (player, order) in plan)
        {
            var hero = ctx.Hero(player);
            if (order.Type != OrderType.Guard)
            {
                continue;
            }

            if (hero.IsEmber)
            {
                Paths.Beacon(ctx, hero);
            }
            else if (!hero.IsInHost)
            {
                // Dentro de un huésped, GUARDAR no hace nada.
                ctx.Guarding.Add(player);
                ctx.Emit(new HeroGuarded(player) { Audience = RoundContext.AudienceOf(hero) });
            }
        }
    }

    private static (GameState, IReadOnlyList<DomainEvent>) Finish(RoundContext ctx)
    {
        var outcome = Outcomes.Evaluate(ctx);
        if (outcome is not null)
        {
            foreach (var score in outcome.Scores)
            {
                ctx.Emit(new PactRevealed(score.Player, score.Pact, score.PactFulfilled));
            }

            foreach (var betrayal in outcome.Betrayals)
            {
                ctx.Emit(new BetrayalRevealed(betrayal.Traitor, betrayal.Victim, betrayal.Damage));
            }

            ctx.Emit(new MatchEnded(outcome));
        }

        // Quien hizo ruido se oye donde terminó la ronda (además de donde hizo el ruido).
        foreach (var hero in ctx.Heroes.Where(h => ctx.NoisyHeroes.Contains(h.Player)).ToList())
        {
            ctx.MakeNoise(hero);
        }

        foreach (var enemy in ctx.Enemies.Where(e => ctx.NoisyEnemies.Contains(e.Id)).ToList())
        {
            ctx.MakeNoise(enemy);
        }

        ctx.Emit(new RoundResolved(ctx.Round));
        var next = ctx.Start with
        {
            Round = outcome is null ? ctx.Round + 1 : ctx.Round,
            Phase = outcome is null ? MatchPhase.InProgress : MatchPhase.Finished,
            Heroes = [.. ctx.Heroes],
            Enemies = [.. ctx.Enemies],
            Rooms = [.. ctx.Rooms],
            RoomIndex = ctx.RoomIndex,
            NextEnemyId = ctx.NextEnemyId,
            LastSeq = ctx.LastSeq,
            Outcome = outcome,
            Betrayals = [.. ctx.Betrayals],
            NoisyHeroes = [.. ctx.NoisyHeroes],
            NoisyEnemies = [.. ctx.NoisyEnemies],
            NoiseTiles = [.. ctx.NoiseTiles],
            BellRound = ctx.BellRound,
            BellRinger = ctx.BellRinger,
            BellClosed = ctx.BellClosed,
        };
        return (next, ctx.Events);
    }
}
