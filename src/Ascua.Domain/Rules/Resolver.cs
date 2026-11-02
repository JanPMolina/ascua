using Ascua.Domain.Abstractions;
using Ascua.Domain.Events;
using Ascua.Domain.Model;

namespace Ascua.Domain.Rules;

/// <summary>
/// Resolución pura y determinista de una ronda: (estado, órdenes, azar) → (estado nuevo, eventos).
/// Orden fijo: 1) GUARDAR y escudos; 2) movimientos con rebote; 3) ataques y habilidades por
/// iniciativa; 4) los enemigos ejecutan lo anunciado; 5) los enemigos se mueven y anuncian;
/// 6) disparadores y limpieza. Antes de todo se revelan los caminos elegidos en la ronda
/// (planteamiento/04-caminos-tras-morir.md); las Brasas y los Renegados dan órdenes como los demás.
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
        var downAtStart = state.Heroes.Where(h => h.Status == HeroStatus.Down).Select(h => h.Player).ToHashSet();
        var plan = PlanOrders(ctx, orders);

        ActivateGuards(ctx, plan);
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
            if (hero.IsEmber)
            {
                if (order.Type == OrderType.Guard)
                {
                    Paths.Beacon(ctx, hero);
                }

                continue;
            }

            if (order.Type == OrderType.Guard)
            {
                ctx.Guarding.Add(player);
                ctx.Emit(new HeroGuarded(player) { Audience = RoundContext.AudienceOf(hero) });
            }
            else if (order is { Type: OrderType.Skill, Direction: { } direction } && hero.Class == HeroClass.Yunque)
            {
                ctx.Ramparts[player] = direction;
                ctx.Update(hero with { Facing = direction, SkillCooldown = hero.SkillCooldownOnUse });
                ctx.Emit(new SkillUsed(player, hero.Class, direction) { Audience = RoundContext.AudienceOf(hero) });
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

            ctx.Emit(new MatchEnded(outcome));
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
        };
        return (next, ctx.Events);
    }
}
