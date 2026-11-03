# Registro de decisiones de arquitectura (ADR)

Cada decisión relevante se documenta con la [plantilla](0000-plantilla.md): contexto, decisión,
alternativas y consecuencias. Una ADR aceptada no se edita en lo sustancial: si la decisión cambia,
se escribe una nueva que la **reemplaza** y se actualiza el estado de la anterior.

| # | Decisión | Estado | Fecha |
|---|---|---|---|
| [0001](0001-turnos-simultaneos-cerrador-unico.md) | Turnos simultáneos con un único cerrador por ronda (lock en Redis + `UNIQUE(match_id, seq)` en Postgres) | aceptada | 2026-10-29 |
| [0002](0002-signalr-websockets-messagepack.md) | SignalR solo sobre WebSockets con MessagePack y sin *sticky sessions* | aceptada | 2026-10-29 |
| [0003](0003-event-sourcing-liviano-snapshot-redis.md) | Event sourcing liviano con instantánea en Redis | aceptada | 2026-10-29 |
| [0004](0004-jwt-propio-vs-oidc.md) | JWT propio frente a un proveedor OIDC externo | aceptada | 2026-10-29 |
| [0005](0005-dotnet-10-openapi-31.md) | .NET 10 en lugar de .NET 8 (OpenAPI 3.1 nativo) | aceptada | 2026-10-29 |
| [0006](0006-ticket-handshake-websocket.md) | Ticket de un solo uso para el *handshake* WebSocket | aceptada | 2026-10-29 |
| [0007](0007-salas-y-emparejamiento.md) | Salas y emparejamiento en Redis con escrituras atómicas (Lua + versión optimista) | aceptada | 2026-10-29 |
| [0008](0008-nombres-de-heroes.md) | Un solo juego de nombres de héroe (YUNQUE, CHISPA, CUERVO, RAIZ) y lectura compatible de partidas guardadas | reemplazada por ADR-0009 | 2026-10-30 |
| [0009](0009-nueve-heroes-contrato-2.md) | Nueve héroes nuevos, contrato 2.0.0 sin periodo de transición y borrado de partidas viejas | aceptada | 2026-11-02 |

Contratos relacionados: [`contracts/asyncapi.yaml`](../../contracts/asyncapi.yaml) (tiempo real) y
[`contracts/openapi.json`](../../contracts/openapi.json) (REST, OpenAPI 3.1 generado por la API). Visión general en
[`planteamiento/02-arquitectura.md`](../../planteamiento/02-arquitectura.md).
