using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ascua.Api.Endpoints;

/// <summary>Ejemplos de respuesta (200 y 404) para el historial, el detalle y la reproducción en OpenAPI.</summary>
internal sealed class MatchHistoryExamplesTransformer : IOpenApiOperationTransformer
{
    private const string MatchId = "0192f3a1-7c2e-7b4d-9a10-3f5e8c6d2b71";
    private const string Me = "0192f39e-11aa-7c3b-8d55-6f0e2a9b4c10";
    private const string Ally = "0192f39e-22bb-7c3b-8d55-6f0e2a9b4c20";

    private static readonly Dictionary<string, string> Examples = new(StringComparer.Ordinal)
    {
        ["GetMatchHistory"] = $$$"""
            {"items":[{"matchId":"{{{MatchId}}}","createdAt":"2026-11-01T20:15:03.120+00:00","finishedAt":"2026-11-01T20:31:47.900+00:00",
            "status":"FINISHED","outcome":"WARDEN_DEFEATED","result":"VICTORY","hero":"CUERVO","placement":1,"embers":17,"rounds":38,"playerCount":3}],
            "page":1,"pageSize":20,"total":1}
            """,
        ["GetMatch"] = $$$"""
            {"matchId":"{{{MatchId}}}","status":"FINISHED","outcome":"WARDEN_DEFEATED","createdAt":"2026-11-01T20:15:03.120+00:00",
            "finishedAt":"2026-11-01T20:31:47.900+00:00","rounds":38,"winners":["{{{Me}}}"],
            "players":[{"playerId":"{{{Me}}}","displayName":"ana","hero":"CUERVO","finalState":"ALIVE","embers":17,"placement":1,
            "pact":{"pactId":"BETRAY_TWICE_UNSEEN","description":"Golpear 2 veces a un aliado por la espalda sin que te descubra.","fulfilled":true}},
            {"playerId":"{{{Ally}}}","displayName":"beto","hero":"YUNQUE","finalState":"DOWN","embers":9,"placement":2,
            "pact":{"pactId":"NEVER_DOWNED","description":"Terminar sin haber caído.","fulfilled":false}}],
            "myPact":{"pactId":"BETRAY_TWICE_UNSEEN","description":"Golpear 2 veces a un aliado por la espalda sin que te descubra.","fulfilled":true},
            "replayUrl":"https://ascua.example/api/v1/matches/{{{MatchId}}}/replay"}
            """,
        ["GetMatchReplay"] = $$$"""
            {"matchId":"{{{MatchId}}}","viewMode":"PLAYER","viewerId":"{{{Me}}}","status":"FINISHED","fromRound":1,"toRound":1,
            "rounds":[{"round":1,"seq":42,"full":null,"view":{"status":"IN_PROGRESS","round":2,"floor":1,"roundDeadline":null,
            "me":{"playerId":"{{{Me}}}","displayName":"ana","hero":"CUERVO","state":"ALIVE","hp":4,"maxHp":4,"shield":0,"position":{"x":4,"y":7},
            "facing":"N","embers":1,"charges":0,"abilityCooldown":0,"stealthRoundsLeft":0,"downRoundsLeft":null,"blessings":["GANZUA"],
            "pact":{"pactId":"BETRAY_TWICE_UNSEEN","description":"Golpear 2 veces a un aliado por la espalda sin que te descubra."},"decided":false,"pendingOrder":null},
            "allies":[{"playerId":"{{{Ally}}}","displayName":"beto","hero":"YUNQUE","state":"ALIVE","hp":6,"maxHp":6,"position":{"x":5,"y":7},
            "facing":"N","embers":0,"blessingCount":1,"connected":false,"decided":false}],
            "enemies":[{"enemyId":3,"kind":"CARRONERO","hp":2,"maxHp":2,"position":{"x":4,"y":4},"facing":"S","phase":null,"telegraph":[{"x":4,"y":5}]}],
            "tiles":[{"x":4,"y":7,"kind":"FLOOR"}]},
            "events":[{"type":"MOVED","actorId":"{{{Me}}}","position":{"x":4,"y":7}},{"type":"GUARDED","actorId":"{{{Ally}}}"}]}]}
            """,
    };

    private const string NotFound = """
        {"type":"urn:ascua:problem:not-found","title":"El recurso no existe.","status":404,"code":"not-found"}
        """;

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (operation.OperationId is not { } id || !Examples.TryGetValue(id, out var example) || operation.Responses is null)
        {
            return Task.CompletedTask;
        }

        SetExample(operation, "200", "application/json", example);
        SetExample(operation, "404", "application/problem+json", NotFound);
        return Task.CompletedTask;
    }

    private static void SetExample(OpenApiOperation operation, string status, string contentType, string json)
    {
        if (operation.Responses!.TryGetValue(status, out var response)
            && response.Content is { } content
            && content.TryGetValue(contentType, out var media))
        {
            media.Example = JsonNode.Parse(json);
        }
    }
}
