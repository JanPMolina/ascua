# ADR-0007: Salas y emparejamiento en Redis con escrituras atómicas

- **Estado:** aceptada
- **Fecha:** 2026-10-29
- **Autores:** Equipo Ascua

## Contexto

Antes de una partida los jugadores se juntan de dos maneras: en **salas** (públicas o privadas con
código de invitación) o en la **cola de emparejamiento**. El enunciado y la arquitectura
(`planteamiento/02-arquitectura.md`, sección 5) piden:

- capacidad de 2 a 4 respetada **aunque varios se unan a la vez**;
- un usuario en **una sola** sala, cola o partida activa a la vez;
- emparejamiento en el servidor, con tiempo máximo de espera configurable y sin que un jugador quede
  en dos partidas;
- todo con varias réplicas de la API sin afinidad.

El estado del lobby es corto (minutos) y muy concurrente; no hace falta historial de salas. La partida
en sí (eventos, resultado) sí es durable y la persiste el tiempo real en Postgres (ADR-0003).

## Decisión

1. **Todo el lobby vive en Redis** (salas, índice de salas públicas, códigos de invitación, cola y
   marcas por usuario). Así las tres exclusiones (sala, cola, partida) se comprueban **en el mismo
   almacén y en la misma operación atómica**.
2. **Sala = agregado inmutable en `Ascua.Application.Rooms.Room`** con las reglas (capacidad, héroe sin
   repetir, "listo" exige héroe, solo el anfitrión inicia, todos listos y ≥2). Se guarda como un hash
   `room:{id}` con `v` (versión) y `data` (JSON). Cada cambio es *leer → aplicar regla → guardar si la
   versión no cambió* con un **script Lua** (`SaveRoom`), que en la misma ejecución atómica:
   - rechaza si la versión cambió (`CONFLICT`; el caso de uso relee y reintenta, hasta 10 veces con una
     espera aleatoria corta);
   - verifica que cada miembro nuevo esté libre (`user:{id}:lobby` vacío o vencido y sin
     `user:{id}:active_match`);
   - escribe la sala, su TTL, el índice `rooms:public` y las marcas de lobby de los miembros.

   Con N uniones simultáneas a una sala de 4, en cada "ronda" de lecturas gana una sola escritura; los
   perdedores releen y ven la sala llena (`room-full`). Hay una prueba de integración con 12 uniones
   simultáneas.
3. **Inicio de sala:** el anfitrión inicia cuando hay ≥2 jugadores y **todos** los presentes están
   listos (decisión: nadie entra a una partida sin haber confirmado). La sala pasa a `STARTING` (queda
   congelada), se llama a `IMatchLauncher.LaunchAsync` (idempotente por `RoomId`) y, con el id de la
   partida, la misma escritura atómica pasa la sala a `STARTED`, saca a los jugadores del lobby y les
   pone `SET user:{id}:active_match {matchId} NX PX`. Si el lanzamiento falla, la sala vuelve a `OPEN`.
   Repetir el inicio devuelve la misma partida.
4. **Cola de emparejamiento:** sorted set `mm:queue` (score = llegada en ms) más `mm:tickets` (nombre y
   héroe preferido). Un `BackgroundService` (`MatchmakingWorker`) en **cada réplica** llama cada
   `PollInterval` al agrupador Lua `FormGroup`, que de forma atómica toma **4** jugadores en cuanto los
   hay, o **2-3** si el más antiguo esperó `MaxWait`. Los deja en `pending:{grupo}` hasta lanzar la
   partida (id de grupo = `RoomId` del lanzador) y `CompleteGroup` marca la partida activa con `SET NX`.
   Un grupo huérfano (réplica caída a mitad) se reintenta a los 15 s con el mismo id; el lanzador
   idempotente devuelve la misma partida. Quien espera más que `QueueTimeout` sin rivales sale de la cola
   (estado `EXPIRED`). Los héroes se reparten sin repetir: por orden de llegada, el preferido si está
   libre; si no, el primero libre.
5. **Marca de partida activa** (`IActiveMatchRegistry`): la ponen salas y emparejamiento; la quita el
   tiempo real al terminar la partida (`ReleaseAsync`, compara y borra). Tiene un TTL de seguridad
   (`ActiveMatchTtl`, 4 h) por si nunca se libera.
6. Sin `IMatchLauncher` real registrado, se usa `UnavailableMatchLauncher` (con `TryAdd`) que responde
   **503 `matches-unavailable`**; el tiempo real lo reemplaza registrando el suyo.

## Alternativas consideradas

| Alternativa | Por qué no |
|---|---|
| Salas en Postgres con `UPDATE rooms SET players = players + 1 WHERE players < capacity` | Atómico para la capacidad, pero la exclusión con la cola (en Redis) quedaría repartida entre dos almacenes y sin una transacción común; además el lobby no necesita durabilidad |
| Reglas de la sala escritas directamente en Lua (`if count < max then add`) | Más corto, pero duplica las reglas fuera de C# y no se pueden probar como código puro; con el control de versión, Lua solo garantiza la atomicidad y las reglas quedan en el agregado |
| Transacción `MULTI/EXEC` desde C# | StackExchange.Redis comparte una conexión y no expone `WATCH`; sus condiciones (`AddCondition`) no alcanzan para "si el jugador está libre o su marca está vencida". El script Lua lo resuelve en una sola ida y vuelta |
| Lock distribuido por sala | Más latencia y riesgo de locks huérfanos; la versión optimista no bloquea a nadie |
| Agrupador en una sola réplica (líder) | Exige elección de líder; con Lua atómico varias réplicas pueden agrupar a la vez sin doble asignación |
| Que el cliente consulte la cola para saber su turno | El agrupador corre en el servidor; el cliente solo consulta el estado por REST y la partida llega por el tiempo real |

## Consecuencias

- **Positivas:** una sola fuente de verdad para "¿dónde está este jugador?"; capacidad y exclusión
  garantizadas con cualquier número de réplicas; reglas puras con pruebas unitarias; salas que se
  limpian solas por TTL (`RoomTtl`, 1 h sin cambios).
- **Negativas:** si Redis pierde datos se pierden las salas y la cola en curso (aceptable: es estado de
  minutos y los jugadores vuelven a entrar). Los scripts arman claves a partir de ids, así que **Redis
  no puede estar en modo clúster** (sirve Azure Cache for Redis Basic/Standard, o cualquier instancia de
  un solo *shard*; si algún día hace falta clúster, habría que agrupar las claves con *hash tags*). El total del listado de salas públicas es
  aproximado: las salas vencidas se quitan del índice de forma perezosa al listar.
- **Pruebas:** `tests/Ascua.Application.Tests` (reglas, reintentos, lanzamiento y huérfanos) e
  integración con Testcontainers Redis + Postgres: 12 uniones simultáneas a una sala de 4 (entran
  exactamente 4) y 4 agrupadores concurrentes más el `BackgroundService` sobre la misma cola sin que
  ningún jugador quede en dos partidas.
