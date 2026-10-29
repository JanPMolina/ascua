# ADR-0001: Turnos simultáneos con un único cerrador por ronda

- **Estado:** aceptada
- **Fecha:** 2026-10-29
- **Autores:** Equipo Ascua

## Contexto

En *Ascua* todos los jugadores envían en secreto una orden para la ronda `N` y la ronda se resuelve
cuando decidieron todos los vivos o cuando vence el plazo de 20 s (a quien no envió se le aplica
`GUARD`). El servidor corre en **2 o más réplicas** de Container Apps sin afinidad, así que:

- las órdenes de una misma partida pueden llegar a réplicas distintas al mismo tiempo;
- el temporizador de plazos corre en **cada** réplica (un `BackgroundService` que consulta Redis);
- el enunciado exige orden consistente por partida, idempotencia, que ninguna jugada confirmada
  se pierda si una réplica muere y que una partida con fallas no afecte a las demás.

Si dos réplicas resolvieran la misma ronda, la partida divergiría (dos tiradas del generador, dos
`RoundResolved` distintos). Se necesita exactamente **una** resolución por ronda.

## Decisión

1. **Turnos simultáneos con plazo del servidor.** Las órdenes se guardan en Redis con
   `HSETNX match:{id}:r:{N}:orders {playerId}` (la primera orden vale) e idempotencia por
   `clientActionId` con `SET NX match:{id}:act:{clientActionId}`. Una orden con `round != N` se
   rechaza con `STALE_ROUND`.
2. **Único cerrador por ronda en dos capas:**
   - *Capa rápida:* lock en Redis `SET match:{id}:close:{N} {replica} NX PX 5000`. Solo quien lo
     obtiene ejecuta `CloseRound`.
   - *Garantía final:* los eventos de la ronda se insertan en Postgres en una transacción con
     `UNIQUE(match_id, seq)`. Si el lock expiró y otra réplica ya cerró, el `INSERT` choca y la
     segunda réplica aborta **sin efectos** (no difunde nada).
3. `CloseRound` dispara por dos vías, ambas idempotentes: la última orden recibida o el vencimiento
   del plazo (`ZRANGEBYSCORE round-deadlines -inf now`).
4. La ronda se considera confirmada **solo** cuando sus eventos están en Postgres; después se
   actualiza la instantánea en Redis, se programa el plazo de `N+1` y se difunde.
5. Cada `CloseRound` corre aislado (`try/catch` y *circuit breaker* por partida): una excepción deja
   la partida en `FAULTED` y avisa a sus jugadores sin tocar las demás.

## Alternativas consideradas

| Alternativa | Por qué no |
|---|---|
| Turnos alternados (uno a la vez) | Elimina la concurrencia que el enunciado pide demostrar y alarga la partida (4 × 20 s por vuelta) |
| *Actor* por partida fijado a una réplica (Orleans, Akka.NET) | Resuelve el orden de forma elegante, pero agrega un *cluster* con membresía y *sticky routing*; demasiado para el plazo y el presupuesto |
| Solo el lock de Redis | Un lock con TTL no es seguro ante pausas largas del proceso (GC, red): dos réplicas podrían creer que lo tienen. Falta una garantía en la fuente de verdad |
| Solo la restricción única de Postgres | Correcto, pero todas las réplicas resolverían la ronda para que solo una gane: trabajo desperdiciado y más carga en Postgres |
| Cola de mensajes por partida (Service Bus con sesiones) | Ordena bien, pero suma un servicio pago y más latencia que el lock en Redis |

## Consecuencias

- **Positivas:** resolución exactamente una vez por ronda aunque haya varias réplicas o una muera a
  mitad (otra reconstruye desde Postgres y el temporizador vuelve a disparar). Orden total por
  partida mediante `seq`. Se puede probar con dos réplicas compitiendo en `Ascua.Concurrency.Tests`.
- **Negativas:** dos mecanismos que mantener coherentes; el plazo real puede extenderse hasta un
  ciclo del temporizador más el tiempo de resolución (el periodo del temporizador se ajusta en las
  pruebas de carga).
- **Riesgos:** si Redis se pierde, se pierden las órdenes de la ronda abierta (no las confirmadas);
  la ronda se resuelve con `GUARD` para los afectados. Se acepta porque Redis Basic no tiene réplica.
