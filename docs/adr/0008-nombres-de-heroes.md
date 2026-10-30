# ADR-0008: Un solo juego de nombres de héroe (YUNQUE, CHISPA, CUERVO, RAIZ)

- **Estado:** aceptada
- **Fecha:** 2026-10-30
- **Autores:** Equipo Ascua

## Contexto

El enum de dominio `HeroClass` se llamaba `Anvil`/`Spark`/`Raven`/`Root`. Como el REST serializa los
enums con `JsonNamingPolicy.SnakeCaseUpper`, salas y cola exponían `ANVIL`/`SPARK`/`RAVEN`/`ROOT`,
mientras que el tiempo real (AsyncAPI), el historial, el bot, la prueba de carga y las reglas del juego
usaban `YUNQUE`/`CHISPA`/`CUERVO`/`RAIZ`. `RealtimeMapper` traducía en un sentido y el cliente web en
el otro. Dos nombres para lo mismo en contratos que deben estar «completos y sincronizados» es un
defecto de contrato.

Además, el nombre del enum está persistido:

| Dónde | Forma | ¿Se puede reescribir? |
|---|---|---|
| `match_players.hero` (Postgres) | texto `Anvil`… (`ToString()`) | Sí, con un `UPDATE` (suspendiendo el *trigger* que hace inmutables los asientos) |
| `matches.state`, `match_states.state`, `match_events.payload` (jsonb) | `"Class": "Anvil"`, `"Hero": "Anvil"` | No conviene: `match_events` y `match_states` son *append-only* y una partida terminada es inmutable (*triggers*; [ADR-0003](0003-event-sourcing-liviano-snapshot-redis.md)) |
| Instantánea caliente, salas y tickets de cola (Redis) | JSON con `"Anvil"`… | No hay migraciones para Redis |

## Decisión

1. **Nombres del juego en todas partes.** Los miembros del enum pasan a `Yunque`, `Chispa`,
   `Cuervo` y `Raiz`; con la política del REST viajan como `YUNQUE`/`CHISPA`/`CUERVO`/`RAIZ`, igual que
   en AsyncAPI. Se eliminan las traducciones de `RealtimeMapper` y del cliente web. **Es un cambio de
   contrato REST**: `ANVIL`/`SPARK`/`RAVEN`/`ROOT` dejan de aceptarse (400 `validation`).
2. **Columna relacional migrada.** La migración `HeroNamesInSpanish` reescribe `match_players.hero` a
   la forma del contrato (`YUNQUE`…), que es la que se escribe desde ahora. Así el panel de
   administración, que la expone tal cual, devuelve el mismo nombre que el resto de la API. Como
   `trg_match_players_immutable` impide cambiar asientos, la migración lo desactiva solo durante el
   `UPDATE` (dentro de la transacción de la migración); `HeroNamesMigrationTests` lo comprueba con una
   base que ya tiene partidas, en los dos sentidos.
3. **JSON leído con compatibilidad.** El jsonb y Redis no se reescriben: `HeroClassJsonConverter`
   (registrado en `MatchJson` y en el JSON de salas/cola) acepta los nombres nuevos y los viejos al
   leer y escribe solo los nuevos. Una partida guardada antes del cambio se reanuda, se reproduce y
   aparece en el historial sin intervención.

## Alternativas consideradas

| Alternativa | Por qué no |
|---|---|
| Dejar el enum en inglés y ponerle `[JsonStringEnumMemberName("YUNQUE")]` | Arregla el cable, pero el dominio seguiría con otro vocabulario que las reglas del juego y el resto de los contratos |
| Unificar en los nombres en inglés | Habría que cambiar AsyncAPI, historial, bot, prueba de carga y las reglas del juego; los nombres del juego son los del planteamiento |
| Reescribir también el jsonb con SQL | `match_events` es *append-only* (habría que desactivar el *trigger*) y Redis no se migra; el lector tolerante cubre los tres casos con una sola pieza |
| Recrear las bases de desarrollo | `docker compose` conserva el volumen `pgdata`, y una partida en curso durante un despliegue se rompería |

## Consecuencias

- **Positivas:** un solo vocabulario en dominio, REST, tiempo real, historial, administración, bot y
  cliente; el cliente ya no traduce nada.
- **Negativas:** clientes externos que enviaran `ANVIL`… reciben 400 (no hay otros clientes que el web,
  el bot y la prueba de carga, que se actualizan en el mismo cambio). El lector tolerante debe quedarse
  mientras existan partidas guardadas antes del cambio.
- **Reversión:** el `Down` de la migración devuelve la columna a `Anvil`…, pero el código anterior no
  sabe leer el JSON escrito por el nuevo (`"Yunque"`): revertir el código después de jugar partidas
  nuevas exige terminar esas partidas primero.
