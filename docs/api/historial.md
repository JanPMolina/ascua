# Historial y reproducción de partidas

Endpoints REST con `Authorization: Bearer <JWT>`. Los errores son Problem Details (RFC 9457) con
`type = urn:ascua:problem:<code>` y la extensión `code`; no hay códigos nuevos (se reutilizan
`validation`, `unauthorized`, `forbidden` y `not-found`). Esquemas y ejemplos en `/openapi/v1.json` (`/docs`).

| Endpoint | Qué devuelve |
|---|---|
| `GET /api/v1/matches/history?page=1&pageSize=20` | Partidas del usuario (en curso y terminadas), de la más nueva a la más vieja, con `total`. `pageSize` 1-100. |
| `GET /api/v1/matches/{id}` | Detalle y resultado: estado, `outcome`, ganadores, jugadores por puesto y tu pacto. |
| `GET /api/v1/matches/{id}/replay?fromRound=0&toRound=60` | Reproducción ronda a ronda con **tu** vista. Admin: `?as=full`. |

## Privacidad y autorización

- **Partida ajena = partida inexistente:** `404 not-found` en ambos casos (detalle y replay), igual que
  `POST /realtime/tickets`. Así un 403 no confirma que el id exista.
- `?as=full` sin rol `admin` → `403 forbidden`, **antes** de buscar la partida (tampoco revela existencia).
  Un admin sin `as=full` en una partida ajena recibe 404: no tiene vista de jugador en ella.
- Cada acceso `as=full` pasa por el puerto `IReplayAccessAudit` y queda en `audit_log` como
  `match.full_replay_viewed`, con `fromRound`/`toRound` (ver [administracion.md](administracion.md)).
- **Pactos:** el tuyo siempre (`myPact`). Los ajenos solo si la partida terminó con resultado del juego
  (`FINISHED`/`ABANDONED`), como en `MatchFinished`; en `EXPIRED`/`FAULTED` no se revelan. La reproducción
  nunca los muestra en las rondas: es lo que viste en vivo.

## Resultado por usuario (`result` en el historial)

`VICTORY` (único ganador), `DRAW` (empataste en el máximo), `DEFEAT` (el Celador cayó pero ganó otro),
`COLLECTIVE_DEFEAT` (el Umbral se apagó), `ABANDONED`; `null` si no hay resultado del juego.
`placement` y `embers` salen de la puntuación final (ascuas + pacto cumplido).

## Cómo se reconstruye la reproducción (decisión)

No se vuelve a ejecutar el motor: el azar sale de un CSPRNG sin semilla (guardarla expondría el futuro de
la partida) y las órdenes no se persisten. En su lugar, cada confirmación guarda en la **misma transacción**
que sus eventos una instantánea en `match_states` (`match_id, version, round, from_seq, last_seq, state`).
El replay lee esas instantáneas y los eventos `(from_seq, last_seq]` de `match_events` y proyecta cada
confirmación con su estado posterior mediante `PlayerViewProjector`, igual que `RoundResolved` en vivo.
Leer lo confirmado es determinista (dos llamadas devuelven lo mismo) y nunca expone el payload crudo:
todo pasa por los DTOs del contrato de tiempo real.

- Ronda `0` = draft inicial; `1..60` = rondas. Cada ronda trae la vista al final de la ronda (`view`, o
  `full` para admin) y sus eventos visibles en orden. `seq` es el último aplicado.
- Partidas creadas antes de la migración `MatchHistoryReplay`: tienen una sola instantánea (la última)
  con todos sus eventos.
- Las partidas con héroes anteriores a los nueve actuales se borraron ([ADR-0009](../adr/0009-nueve-heroes-contrato-2.md)).
- Las señales rápidas no se guardan, así que no aparecen. Las traiciones anónimas siguen el mismo filtro que en
  vivo: solo las ve quien las cometió hasta que el final las revela.

## Base de datos

- `match_players.created_at` (copia de la fecha de la partida) + índice
  `ix_match_players_user_id_created_at (user_id, created_at DESC, match_id DESC)` para el historial paginado.
- `match_states`: PK `(match_id, version)`, índice `(match_id, round)`, append-only por trigger.
- **Inmutabilidad endurecida:** además de `match_events` append-only y `matches.result` fijo, ahora una
  partida en estado terminal no admite `UPDATE` ni `DELETE` (`trg_matches_final_immutable`) y los asientos
  no cambian (`trg_match_players_immutable`).
