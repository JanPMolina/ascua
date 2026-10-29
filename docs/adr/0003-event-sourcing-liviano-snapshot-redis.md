# ADR-0003: Event sourcing liviano con instantánea en Redis

- **Estado:** aceptada
- **Fecha:** 2026-10-29
- **Autores:** Equipo Ascua

## Contexto

El enunciado exige que el resultado sea inmutable, que la partida se pueda **reproducir ronda a
ronda**, que ninguna jugada confirmada se pierda si una réplica muere y que haya auditoría. Al mismo
tiempo, cada ronda debe resolverse rápido (meta de p95 < 200 ms) y cualquier réplica debe poder
cerrar cualquier ronda (ADR-0001), así que el estado no puede vivir en la memoria de un proceso.

## Decisión

- **Fuente de verdad en Postgres, *append-only*:** `match_events(match_id, seq, round, type,
  payload_jsonb, created_at)` con `UNIQUE(match_id, seq)`. Guarda la verdad completa, incluida la
  información oculta (órdenes, pactos, drafts, contenido de cofres y resultados de cada tirada), y
  **solo se lee en el servidor**. La semilla del generador no se guarda: los eventos registran los
  resultados del azar, así que reproducir no exige volver a tirar.
- **Estado caliente en Redis:** la instantánea de `GameState` de cada partida con su versión (`seq`),
  más las órdenes de la ronda, plazos y tickets. `CloseRound` carga la instantánea, resuelve con el
  dominio puro (`Resolver.Resolve(state, orders, IRandom)`) y guarda la nueva.
- Si la instantánea falta o su versión no coincide con la última de Postgres, **se reconstruye**
  aplicando los eventos desde el inicio (o desde la última instantánea válida).
- Es "liviano" porque no hay proyecciones asíncronas ni bus de eventos: los eventos se escriben en la
  misma transacción que confirma la ronda y las vistas se calculan al vuelo con `PlayerViewProjector`.
- La reproducción (`GET /api/v1/matches/{id}/replay`) proyecta los eventos con la vista de quien la pide.

## Alternativas consideradas

| Alternativa | Por qué no |
|---|---|
| Solo estado actual en Postgres (CRUD) | No permite reproducir ni auditar sin una tabla de historial aparte, que termina siendo un log de eventos mal hecho |
| Solo estado en Redis | Rápido, pero Redis Basic no es durable: una caída perdería partidas enteras |
| Event sourcing "completo" (EventStoreDB, Marten con proyecciones) | Más infraestructura y conceptos de los necesarios; Postgres ya está en la topología |
| Reconstruir desde eventos en cada ronda (sin instantánea) | Correcto pero lento al avanzar la partida; es justo la optimización candidata "reconstruir → instantánea" que se medirá |

## Consecuencias

- **Positivas:** reproducción, auditoría y resultado inmutable salen del mismo log. Recuperación tras
  la caída de una réplica sin pérdida de rondas confirmadas. Resolución en memoria y rápida.
- **Negativas:** dos representaciones del estado que deben coincidir; se mitiga versionando la
  instantánea con `seq` y con una prueba de propiedad "reconstruir desde eventos == instantánea".
  Los eventos son un contrato interno: cambiar su forma exige *upcasting* o migración.
- **Seguridad:** como el log contiene secretos, nunca se expone tal cual; toda salida pasa por el
  proyector de vistas y los logs usan `LogRedactor`.
