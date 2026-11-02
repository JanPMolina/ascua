# Tiempo real (hub `/realtime/match`)

El contrato normativo es [`contracts/asyncapi.yaml`](../../contracts/asyncapi.yaml). Este documento
explica cómo lo implementa el servidor y cómo probarlo.

## Conexión

1. `POST /api/v1/realtime/tickets` con `Authorization: Bearer <JWT>` y cuerpo `{ "matchId": "<uuid>" }`
   → `201 { ticket, expiresAt }`. Partida inexistente o ajena: `404 not-found` (no se distingue).
   Partida terminada: `409 match-not-active`.
2. WebSocket a `/realtime/match?protocolVersion=1&access_token=<ticket>` con `skipNegotiation` y
   transporte WebSockets. El ticket vale 30 s, sirve una sola vez (`GETDEL`) y solo para esa partida.
3. `JoinMatch { matchId }` → ACK con asiento y `Snapshot`. Si hay draft pendiente llega también `DraftOffer`,
   y si hay una elección de camino abierta, `PathOffer` (también tras `GetSnapshot`).

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
- Una elección de camino abierta (`PathOffer` sin responder) de un jugador conectado también frena el
  cierre anticipado, hasta su plazo de 15 s.
- Una excepción del dominio marca la partida `FAULTED`, avisa `Error` + `MatchFinished { outcome: FAULTED }`
  y no afecta a las demás partidas ni al proceso.

## Caminos tras la extinción (contrato 1.3.0)

Reglas: [`planteamiento/04-caminos-tras-morir.md`](../../planteamiento/04-caminos-tras-morir.md).

```
Extinción (al resolver la ronda N) ─► PathOffered (privado) ─► PathOffer al jugador (+ me.path.offerId)
ChoosePath ─► esquema (path FRIENDSHIP|VENGEANCE) ─► NOT_JOINED? ─► ACK guardado (path:{user}:{offer})
          ─► participante / partida en curso / oferta vigente (offerId de esa extinción, sin elegir)
          ─► MatchRules.ChoosePath: héroe.PathChoice + PathSelected (privado) ─► si decidieron todos → CloseRound
Plazo (15 s desde que abrió la ronda) ─► MatchRules.AutoChoosePaths → FRIENDSHIP (Snapshot al jugador)
CloseRound N+1 ─► Resolver revela: PathChosen (todos) ─► la Brasa actúa desde N+2; el Renegado espera la sala siguiente
```

- **Plazo durable sin columnas nuevas.** La oferta nace al resolverse la ronda anterior, así que su plazo
  es `deadline − roundDuration + PathChoiceDuration` (`MatchSnapshot.PathDeadline`), derivado de lo que ya
  guarda Postgres. El sorted set `ascua:deadlines` agenda `MatchSnapshot.WakeAt` = mín(plazo de camino,
  plazo de la ronda). Si Redis pierde el plazo, a más tardar el cierre de la ronda resuelve la oferta con
  Amistad (lo hace el propio `Resolver`), así que nunca queda colgada.
- **Idempotencia.** `ChoosePath` repetido con el mismo camino devuelve el ACK original
  (`result: DUPLICATE_ACTION`, mismo `acceptedAt`), incluso después de la resolución; con otro camino,
  `PATH_CLOSED`. El `offerId` es un hash de partida + jugador + número de extinción.
- **Audiencias.** `PathOffered`, `PathSelected` y `EchoGained` son privados (`Audience.Only`); `PathChosen`,
  `EmberReborn`, `RenegadeDefeated` y `RenegadeSpawned` son para todos (a quien no ve al Renegado se le quita
  la posición); lo que hace una Brasa usa la audiencia nueva `Group` (el grupo siempre, un Renegado solo si
  ve la casilla).
- **Reproducción.** Todo pasa por el log de eventos y `match_states` como cualquier mutación; el `Resolver`
  sigue siendo puro y determinista.

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
| `ascua:{m:<id>}:path:<user>:<offerId>` | ACK guardado de `ChoosePath` |
| `ascua:{m:<id>}:presence` | Conexión vigente por usuario (`SESSION_REPLACED` al abrir otra) |
| `ascua:deadlines`, `ascua:graces` | Sorted sets de plazos y gracias que leen los temporizadores de todas las réplicas |
| `ascua:ticket:<sha256>` | Tickets (se guarda el hash, no el ticket) |
| `ascua:user:<id>:conns` | Arriendos de conexión por usuario (expiran si la réplica muere) |

## Temporizadores y ciclo de vida

`RoundTimerService` corre en cada réplica cada 250 ms: plazos vencidos (draft → elige el servidor;
elección de camino → Amistad; ronda → cierra con GUARD) y gracias vencidas (abandono con `MatchRules.Retire`). Cada 30 s expira
partidas sin cambios por 10 min (`EXPIRED`) y recupera plazos que Redis haya perdido. La duración de
ronda se fija al crear la partida (`matches.round_duration_ms`), así que réplicas con otra
configuración no cambian el ritmo de una partida en curso.

Al recibir SIGTERM, `RealtimeDrainService` envía `ServerDraining` a las conexiones de la réplica y
espera `DrainDelay` antes de dejar cerrar el servidor. El backplane de Redis de SignalR reparte los
mensajes entre réplicas.

## Resiliencia (qué garantizan las pruebas)

`tests/Ascua.Integration.Tests/Realtime/ResilienceTests.cs` corre con su propio Postgres y Redis
(colección `resiliencia`) porque pausa los contenedores. Cada garantía tiene su prueba:

| Garantía | Qué se comprueba |
|---|---|
| **Reinicio de instancia** | Se desecha la instancia a mitad de ronda y se levanta otra sobre los mismos datos, dos veces. Sin clientes conectados, el plazo guardado en Redis cierra la ronda en la instancia nueva. Los clientes vuelven con un JWT y un ticket nuevos (`JoinMatch`) y retoman en el `seq` de Postgres. Una orden con ACK antes del reinicio se aplica, el log conserva intacto lo confirmado, el `seq` es contiguo y cada ronda se resuelve una sola vez. |
| **Caída de Redis o Postgres** | Con la dependencia pausada, `/health/ready` responde 503, `/health/live` sigue en 200 y el proceso no se cae: los errores del temporizador quedan en el log y el ciclo sigue. Al volver la dependencia, ready vuelve a 200. El plazo que venció durante la caída cierra la ronda con GUARD, la partida sigue y se pueden crear partidas nuevas. |
| **Aislamiento (`FAULTED`)** | Una partida con estado incoherente hace lanzar al dominio al cerrar la ronda. Esa partida queda `FAULTED` en Postgres y sus jugadores reciben `Error` (`MATCH_NOT_ACTIVE`, `status: FAULTED`) y `MatchFinished` (`outcome: FAULTED`). También se libera su marca de partida activa y `ascua.matches.faulted` sube en 1. Mientras tanto, otra partida en la misma instancia resuelve sus rondas sin recibir ningún error. |
| **Limpieza** | Una partida sin cambios durante `InactivityTimeout` pasa a `EXPIRED`. Los conectados reciben el aviso y se libera la marca de partida activa de todos sus jugadores. |

La concurrencia entre réplicas (dos cerradores, una sola resolución) está en `RealtimeMatchTests`.

## Configuración (`Ascua:Realtime`)

| Clave | Por defecto |
|---|---|
| `RoundDuration` / `DraftDuration` | 20 s / 30 s |
| `PathChoiceDuration` | 15 s (elección de camino; al vencer, Amistad) |
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
`EnableDevEndpoints`), elige bendiciones, elige camino al azar si se extingue y juega órdenes al azar hasta `MatchFinished`.
