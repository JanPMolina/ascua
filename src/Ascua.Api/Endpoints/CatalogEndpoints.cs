using System.ComponentModel;
using Ascua.Api.Realtime;
using Ascua.Api.Security;
using Ascua.Domain.Model;

namespace Ascua.Api.Endpoints;

/// <summary>Una mejora tal como se le muestra al jugador: qué hace y cuánto cuesta.</summary>
public sealed record BlessingCatalogItem(
    [property: Description("Id estable, el mismo de DraftOffer.options.")] string Id,
    string Name,
    string Effect,
    string Cost);

public sealed record SynergyCatalogItem(string Id, string Name, string First, string Second, string Effect);

public sealed record CatalogEntryItem(string Id, string Name, string Effect);

/// <summary>Textos del juego que el cliente puede mostrar tal cual.</summary>
public sealed record CatalogResponse(
    IReadOnlyList<BlessingCatalogItem> Blessings,
    IReadOnlyList<SynergyCatalogItem> Synergies,
    IReadOnlyList<CatalogEntryItem> RoomTypes,
    IReadOnlyList<CatalogEntryItem> Curses,
    IReadOnlyList<CatalogEntryItem> Pacts);

/// <summary>Catálogo de mejoras, sinergias, salas, maldiciones y misiones (06-version-3.md §8).</summary>
public static class CatalogEndpoints
{
    private static readonly CatalogResponse Catalog = new(
        [.. BlessingCatalog.All.Select(b => new BlessingCatalogItem(RealtimeMapper.BlessingId(b.Blessing), b.Name, b.Effect, b.Cost))],
        [.. BlessingCatalog.Synergies.Select(s => new SynergyCatalogItem(
            s.Synergy.ToString().ToUpperInvariant(), s.Name, RealtimeMapper.BlessingId(s.First), RealtimeMapper.BlessingId(s.Second), s.Effect))],
        [.. RoomTypes.Random.Select(t =>
        {
            var entry = CurseRules.Describe(t);
            return new CatalogEntryItem(RealtimeMapper.RoomTypeCode(t), entry.Name, entry.Effect);
        })],
        [.. Enum.GetValues<Curse>().Select(c =>
        {
            var entry = CurseRules.Describe(c);
            return new CatalogEntryItem(RealtimeMapper.CurseCode(c), entry.Name, entry.Effect);
        })],
        [.. PactRules.Offered.Select(p => new CatalogEntryItem(RealtimeMapper.PactId(p), RealtimeMapper.PactId(p), RealtimeMapper.PactDescription(p)))]);

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/catalog", () => TypedResults.Ok(Catalog))
            .WithTags("Catalog")
            .WithName("GetCatalog")
            .WithSummary("Textos de mejoras, sinergias, salas, maldiciones y misiones.")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimiting.UserPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
        return app;
    }
}
