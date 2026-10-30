# Salas y emparejamiento

Referencia viva en `/docs` (Scalar) y `/openapi/v1.json`. Decisiones de diseño en
[ADR-0007](../adr/0007-salas-y-emparejamiento.md). Todo exige `Authorization: Bearer <JWT>` y usa la
política de límite de tasa por usuario (`Ascua:RateLimiting:User`).

Los enums viajan como texto en MAYÚSCULAS: héroes `YUNQUE`, `CHISPA`, `CUERVO`, `RAIZ`
(los mismos del tiempo real, [ADR-0008](../adr/0008-nombres-de-heroes.md)); visibilidad
`PUBLIC`/`PRIVATE`; estado de sala `OPEN`/`STARTING`/`STARTED`; estado de cola
`IDLE`/`QUEUED`/`MATCHING`/`MATCHED`/`EXPIRED`.

## Salas

| Método y ruta | Respuesta correcta | Notas |
|---|---|---|
| `POST /api/v1/rooms` | `201` sala, `Location: /api/v1/rooms/{id}` | `{ name, visibility = PUBLIC, maxPlayers = 4 }`. Quien la crea es el anfitrión. Las privadas reciben `inviteCode` |
| `GET /api/v1/rooms?page=1&pageSize=20` | `200 { items, page, pageSize, total }` | Solo públicas, abiertas y con lugar. `pageSize` de 1 a 50 |
| `GET /api/v1/rooms/current` | `200` sala | La sala en la que está el usuario, o `404 room-not-found` |
| `GET /api/v1/rooms/{id}` | `200` sala | Una privada solo la ven sus participantes (`404` para el resto). `inviteCode` y `matchId` solo para participantes |
| `POST /api/v1/rooms/{id}/join` | `200` sala | Solo públicas. Idempotente si ya está dentro |
| `POST /api/v1/rooms/join` | `200` sala | `{ code }`: 8 caracteres, no distingue mayúsculas |
| `POST /api/v1/rooms/{id}/leave` | `204` | Si sale el anfitrión lo reemplaza el más antiguo; la sala vacía se borra |
| `PUT /api/v1/rooms/{id}/hero` | `200` sala | `{ hero }`. No se repiten héroes en la sala; cambiarlo quita el "listo" |
| `PUT /api/v1/rooms/{id}/ready` | `200` sala | `{ ready }`. Marcarse listo exige héroe |
| `POST /api/v1/rooms/{id}/start` | `200 { matchId }` | Solo el anfitrión, con ≥2 jugadores y **todos** listos. Idempotente |

Después de `start`, cada jugador tiene partida activa: se conecta al tiempo real pidiendo un ticket
(`POST /api/v1/realtime/tickets` con el `matchId`, ver ADR-0006). La sala queda en `STARTED` con el
`matchId` visible para sus participantes hasta que vence (`Ascua:Lobby:RoomTtl` sin cambios).

Ejemplo de sala:

```json
{
  "id": "1f0c…",
  "name": "Los del jueves",
  "visibility": "PRIVATE",
  "status": "OPEN",
  "capacity": 4,
  "hostId": "6a2e…",
  "members": [
    { "userId": "6a2e…", "username": "ana", "hero": "YUNQUE", "ready": true, "isHost": true },
    { "userId": "93bd…", "username": "beto", "hero": null, "ready": false, "isHost": false }
  ],
  "inviteCode": "K7M2QXRA",
  "matchId": null,
  "createdAt": "2026-11-01T12:00:00+00:00"
}
```

## Emparejamiento

| Método y ruta | Respuesta correcta | Notas |
|---|---|---|
| `POST /api/v1/matchmaking/queue` | `200` estado | Cuerpo opcional `{ preferredHero }`. Idempotente: si ya estaba, conserva su lugar |
| `DELETE /api/v1/matchmaking/queue` | `204` | Idempotente. `409 match-starting` si ya fue agrupado |
| `GET /api/v1/matchmaking/queue` | `200 { status, enqueuedAt, position, playersInQueue, preferredHero, matchId }` | `matchId` cuando el estado es `MATCHED` |

El agrupador corre en el servidor (un `BackgroundService` por réplica, atómico en Redis): forma una
partida de **4** en cuanto los hay, o de **2-3** cuando el más antiguo esperó `Ascua:Matchmaking:MaxWait`
(30 s). Quien espera más de `Ascua:Matchmaking:QueueTimeout` (5 min) sin rivales sale de la cola y ve
`EXPIRED`. Los héroes no se repiten: por orden de llegada, el preferido si está libre; si no, uno libre.

## Problem Details

Mismo formato que el resto de la API (`urn:ascua:problem:<código>` y extensión `code`; ver
[autenticacion.md](autenticacion.md)). Códigos de salas y emparejamiento:

| Código | HTTP | Cuándo |
|---|---|---|
| `room-not-found` | 404 | La sala no existe, venció, o es privada y no participa en ella; código de invitación desconocido |
| `not-room-member` | 403 | Acción sobre una sala pública en la que no está |
| `not-room-host` | 403 | Iniciar sin ser el anfitrión |
| `room-full` | 409 | La sala no tiene lugar |
| `already-in-room` | 409 | Ya está en otra sala (crear, unirse o entrar a la cola) |
| `already-in-queue` | 409 | Está en la cola de emparejamiento |
| `already-in-match` | 409 | Tiene una partida activa |
| `room-not-open` | 409 | La sala está iniciando o ya inició |
| `hero-taken` | 409 | Otro jugador de la sala tiene ese héroe |
| `hero-required` | 409 | Marcarse listo sin héroe |
| `not-enough-ready` | 409 | Iniciar con menos de 2 jugadores o con alguien sin marcar listo |
| `room-busy` | 409 | La sala cambió demasiadas veces seguidas mientras se intentaba modificarla; reintentar |
| `match-starting` | 409 | Salir de la cola cuando la partida ya se está creando |
| `matches-unavailable` | 503 | El servicio de partidas (tiempo real) aún no está disponible |

Los códigos siguen la convención *kebab-case* del catálogo REST existente (por ejemplo `room-full` es el
`ROOM_FULL` del planteamiento); los errores del hub en tiempo real usan MAYÚSCULAS según el contrato AsyncAPI.
