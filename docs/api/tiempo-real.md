# Tiempo real (hub `/realtime/match`)

El contrato normativo es [`contracts/asyncapi.yaml`](../../contracts/asyncapi.yaml). Este documento
explica cómo lo implementa el servidor y cómo probarlo.

## Conexión

1. `POST /api/v1/realtime/tickets` con `Authorization: Bearer <JWT>` y cuerpo `{ "matchId": "<uuid>" }`
   → `201 { ticket, expiresAt }`. Partida inexistente o ajena: `404 not-found` (no se distingue).
   Partida terminada: `409 match-not-active`.
2. WebSocket a `/realtime/match?protocolVersion=1&access_token=<ticket>` con `skipNegotiation` y
   transporte WebSockets. El ticket vale 30 s, sirve una sola vez (`GETDEL`) y solo para esa partida.
3. `JoinMatch { matchId }` → ACK con asiento y `Snapshot`. Si hay draft pendiente llega también `DraftOffer`.

| Rechazo del upgrade | HTTP | `code` (Problem Details) |
|---|---|---|
| Sin `protocolVersion=1` (se revisa antes de gastar el ticket) | 400 | `unsupported-protocol-version` |
| Ticket ausente, vencido, ya usado | 401 | `ticket-invalid` |
| Más de 3 conexiones del usuario | 429 | `connection-limit` |

## Flujo de una ronda

```
SubmitOrder ─► validación de esquema/rangos ─► NOT_JOINED? ─► idempotencia (clientActionId)
           ─► participante / partida en curso / héroe activo / round == N / legalidad (dominio)
           ─► Redis (Lua atómico): act:{clientActionId} + HSETNX r:{N}:orders + "ronda sellada"
           ─► PlayerDecided al grupo ─► si decidieron todos los conectados → CloseRound
CloseRound ─► lock por partida (SET NX PX) ─► sella la ronda y lee órdenes ─► faltantes = GUARD
           ─► Resolver.Resolve ─► Postgres: INSERT match_events (PK = UNIQUE(match_id, seq))
              + UPDATE matches ... WHERE version = v  (en una transacción)
           ─► Redis: instantánea nueva + plazo de N+1 en el sorted set ascua:deadlines
           ─► RoundResolved a cada jugador con SU delta (Clients.User) ─► RoundOpened al grupo
```

- Los desconectados no frenan la ronda: reciben `GUARD` (`DEFAULT_GUARD_APPLIED` en su delta).
- Un reenvío con el mismo `clientActionId` devuelve el ACK original (`DUPLICATE_ACTION`), aunque la
  ronda ya haya cerrado. Otra orden en la misma ronda: `ORDER_ALREADY_SUBMITTED`.
- Una orden que llega después de que el cerrador selló la ronda: `STALE_ROUND` (nunca se pierde en silencio).
- Una excepción del dominio marca la partida `FAULTED`, avisa `Error` + `MatchFinished { outcome: FAULTED }`
  y no afecta a las demás partidas ni al proceso.

## Estado y claves

| Dónde | Qué |
|---|---|
| Postgres `matches` | Estado, versión, `last_seq`, plazo, instantánea completa (`state` jsonb, solo servidor) y `result` inmutable |
| Postgres `match_players` | Asientos (autorización por partida y nombre visible) |
| Postgres `match_events` | Log append-only con la verdad completa; PK `(match_id, seq)` |
| `ascua:{m:<id>}:snap` | Instantánea caliente (hash `v`/`d`; solo se sobrescribe con una versión mayor) |
| `ascua:{m:<id>}:lock` | Lock de cierre/mutación (`PX` = `LockLifetime`) |
| `ascua:{m:<id>}:r:<N>:orders` / `:sealed` | Órdenes de la ronda y marca de ronda sellada |
| `ascua:{m:<id>}:act:<clientActionId>` | ACK guardado para la idempotencia |
| `ascua:{m:<id>}:presence` | Conexión vigente por usuario (`SESSION_REPLACED` al abrir otra) |
| `ascua:deadlines`, `ascua:graces` | Sorted sets de plazos y gracias que leen los temporizadores de todas las réplicas |
| `ascua:ticket:<sha256>` | Tickets (se guarda el hash, no el ticket) |
| `ascua:user:<id>:conns` | Arriendos de conexión por usuario (expiran si la réplica muere) |

## Temporizadores y ciclo de vida

`RoundTimerService` corre en cada réplica cada 250 ms: plazos vencidos (draft → elige el servidor;
ronda → cierra con GUARD) y gracias vencidas (abandono con `MatchRules.Retire`). Cada 30 s expira
partidas sin cambios por 10 min (`EXPIRED`) y recupera plazos que Redis haya perdido. La duración de
ronda se fija al crear la partida (`matches.round_duration_ms`), así que réplicas con otra
configuración no cambian el ritmo de una partida en curso.

Al recibir SIGTERM, `RealtimeDrainService` envía `ServerDraining` a las conexiones de la réplica y
espera `DrainDelay` antes de dejar cerrar el servidor. El backplane de Redis de SignalR reparte los
mensajes entre réplicas.

## Configuración (`Ascua:Realtime`)

| Clave | Por defecto |
|---|---|
| `RoundDuration` / `DraftDuration` | 20 s / 30 s |
| `ReconnectGrace` / `JoinTimeout` | 60 s / 120 s |
| `InactivityTimeout` | 10 min |
| `TicketLifetime` / `MaxConnectionsPerUser` | 30 s / 3 |
| `HubTokenLimit` / `HubTokensPerSecond` / `HubAbuseThreshold` | 20 / 5 / 50 rechazos en 60 s |
| `PublicBaseUrl` | `http://localhost:8080` (para `replayUrl`) |
| `EnableDevEndpoints` | `false` (en `docker-compose.yml` es `true`) |

## Observabilidad

Medidor `Ascua.Realtime`: `ascua.matches.active`, `ascua.connections.active`, `ascua.orders.rate`,
`ascua.round.resolve.duration` (ms), `ascua.broadcast.latency` (ms) y `ascua.matches.faulted`.
Los logs llevan `MatchId`, `PlayerId`, `Round` y `Seq` en el *scope* y nunca serializan `GameState`.

## Probar en local

```bash
docker compose up --build -d
docker compose --profile bot run --rm --build bot http://api:8080 2 300
```

El bot registra 2 jugadores, arranca una partida con `POST /api/v1/dev/matches` (solo con
`EnableDevEndpoints`), elige bendiciones y juega órdenes al azar hasta `MatchFinished`.
