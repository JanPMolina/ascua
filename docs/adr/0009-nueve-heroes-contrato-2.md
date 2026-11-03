# ADR-0009: Nueve héroes nuevos, contrato 2.0.0 sin periodo de transición

- **Estado:** aceptada
- **Fecha:** 2026-11-02
- **Autores:** Equipo Ascua

## Contexto

Los nueve héroes de [05-heroes.md](../../planteamiento/05-heroes.md) reemplazan a Yunque, Chispa, Cuervo y Raíz. A diferencia del cambio de nombres de [ADR-0008](0008-nombres-de-heroes.md), aquí no hay equivalencia: desaparecen reglas (bloqueo frontal, Muralla, cargas, sigilo) y aparecen otras (dos cuerpos, huéspedes, deudas, ascuas como vida).

El cambio afecta a tres lugares:

| Dónde | Qué pasa con los héroes viejos |
|---|---|
| Contrato de tiempo real | `HeroKind` cambia, y `SelfHero` pierde `charges` y `stealthRoundsLeft`. Es incompatible |
| Postgres (`matches`, `match_states`, `match_events`, `match_players`) | El estado guardado nombra héroes y campos que ya no existen |
| Redis (instantánea caliente, salas, tickets de cola) | JSON con los nombres viejos; no hay migraciones para Redis |

La política del contrato pide aceptar la versión mayor anterior durante al menos 30 días. No hay producción ni clientes externos: solo el cliente web, el bot y la prueba de carga, que se actualizan con el proyecto.

## Decisión

1. **Contrato 2.0.0, sin periodo de transición.** `protocolVersion` pasa a 2 y la versión 1 se rechaza desde el primer día con `unsupported-protocol-version`. Es una excepción a la política, escrita en `contracts/asyncapi.yaml`.
2. **Las partidas viejas se borran.** La migración `HeroesRework` elimina las partidas con héroes antiguos, con sus estados, eventos y asientos. Partidas, estados y eventos son inmutables por *trigger*, así que los suspende solo durante el cambio, dentro de la transacción de la migración. `HeroesReworkMigrationTests` lo comprueba con una base que ya tiene partidas.
3. **Redis se trata como caché.** Un JSON que no se puede leer cuenta como ausente: `RedisMatchHotState` recarga desde Postgres y `RedisRoomStore` trata la sala como inexistente.
4. **Se elimina el lector tolerante de ADR-0008.** `HeroNames` y `HeroNamesMigrationTests` ya no tienen función: no quedan datos que traducir. La columna `match_players.hero` se sigue escribiendo con el nombre del contrato (`HeroColumn`).
5. **El plazo del Repique es durable.** `matches.bell_duration_ms` lo guarda al crear la partida, igual que `round_duration_ms`, para que un reinicio o una réplica con otra configuración no cambie una partida en curso.

## Alternativas consideradas

| Alternativa | Por qué no |
|---|---|
| Mantener la versión 1 con una capa de traducción | No hay nada a qué traducir: un cliente v1 no sabe dibujar dos cuerpos ni deudas, y sus héroes ya no existen |
| Conservar las partidas viejas con el motor anterior | Dos motores en el dominio solo para reproducir partidas de prueba |
| Convertir cada héroe viejo en uno nuevo | Falsearía el historial: esas partidas se jugaron con otras reglas, y la reproducción ya no coincidiría |
| Borrar a mano las bases de desarrollo | `docker compose` conserva el volumen `pgdata`, y cualquier ambiente con datos fallaría al leerlos |

## Consecuencias

- **Positivas:** un solo juego de reglas en el dominio y sin código de compatibilidad. El contrato describe solo lo que existe.
- **Negativas:**
  - Se pierde el historial de las partidas anteriores. Hoy son solo datos de prueba.
  - El cliente web deja de conectarse hasta que pase a `protocolVersion=2`, así que la API y el cliente deben publicarse juntos.
- **Reversión:** el `Down` de la migración solo quita `bell_duration_ms`; lo borrado no vuelve. Revertir el código exige restaurar un respaldo anterior a la migración, y el código viejo tampoco sabría leer las partidas jugadas con los héroes nuevos.
