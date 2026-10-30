# Runbook: partida `FAULTED`

Cuando el motor recibe una excepción inesperada al procesar una partida, la aísla (`MatchEngine`):
- marca la partida `FAULTED` y la detiene;
- libera la marca de partida activa de sus jugadores;
- avisa a los jugadores con un `Error` `MATCH_NOT_ACTIVE`.

**Las demás partidas y el proceso siguen funcionando.** Las jugadas confirmadas antes del fallo siguen en `match_events`.

## Síntoma

- El contador `ascua.matches.faulted` sube. Conviene que una alerta salte con cualquier valor mayor que 0 en 15 min.
- Hay un log de error con el texto *"La partida {MatchId} falló en la ronda {Round} y queda aislada como FAULTED"*, que incluye la excepción.
- Un jugador reporta que la partida se cortó con «La partida quedó en estado FAULTED y se detuvo».

## Diagnóstico

1. Busca el log por `MatchId` y lee la excepción y la ronda.
   - Si la excepción viene de `Ascua.Domain`, es un bug de reglas.
   - Si viene de Npgsql o StackExchange.Redis, es un problema de infraestructura; sigue [Redis o Postgres caídos](dependencias-caidas.md).
2. Reproduce la partida jugada por jugada con `GET /api/v1/matches/{id}/replay?as=full`, como administrador. Esta acción se audita como `match.full_replay_viewed`. La reproducción permite ver el estado exacto antes de la ronda que falló.
3. Revisa si el problema se repite: la misma excepción en varias partidas apunta a un bug sistemático, no a un caso raro.

## Acción

- **Caso aislado:** no hace falta intervenir. Los jugadores ya quedaron libres para volver a salas y a la cola.
- **Bug de reglas:**
  1. Escribe una prueba en `tests/Ascua.Domain.Tests` que reproduzca el estado de la reproducción.
  2. Corrige el bug y despliega.
- **Varias partidas a la vez:**
  1. Revisa si coincide con un despliegue reciente.
  2. Si es así, haz rollback (runbook de Juan Pablo).

## Verificación

- `ascua.matches.faulted` deja de subir.
- `GET /api/v1/admin/matches` ya no muestra la partida.
- Los jugadores pueden crear o unirse a otra sala: no les responde `already-in-match`.
