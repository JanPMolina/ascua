# Runbook: terminar una partida o suspender una cuenta

Las dos acciones son exclusivas del rol `admin`. Cada una se confirma en la **misma transacción** que su fila de `audit_log`. El detalle está en [docs/api/administracion.md](../api/administracion.md).

## Requisito: tener una cuenta admin

No hay un admin por defecto. Si todavía no existe ninguno:
1. Registra la cuenta normalmente.
2. Despliega con `Ascua__Admin__BootstrapUsername=<usuario>`.
3. Inicia sesión de nuevo, para que el JWT traiga `role=admin`.

## Terminar una partida trabada o con trampa

1. Ubica la partida con `GET /api/v1/admin/matches`. Ahí aparecen la ronda, los jugadores y `lastActivityAt`.
2. Termínala con `POST /api/v1/admin/matches/{id}/terminate` y el cuerpo `{ "reason": "…" }`.
   - El motivo debe tener entre 3 y 500 caracteres y no llevar datos sensibles, porque queda en la auditoría.
   - Si responde **409 `match-busy`**, se estaba cerrando una ronda: reintenta en unos segundos.
   - Si responde **409 `match-not-active`**, la partida ya había terminado.
3. Resultado:
   - la partida queda `TERMINATED`;
   - los jugadores reciben `MatchFinished` (`TERMINATED`) y `KICKED_BY_ADMIN`, y unos 2 s después se cierran sus conexiones (código 4003).

## Suspender una cuenta

1. Suspéndela con `POST /api/v1/admin/users/{id}/suspend` y el cuerpo `{ "reason": "…" }`. Es idempotente.
2. Efectos inmediatos en todas las réplicas:
   - se revocan sus *refresh tokens*;
   - su JWT vigente recibe **403 `account-suspended`** desde la siguiente petición;
   - se cierran sus WebSockets;
   - si tenía una partida activa, se le aplica la regla de abandono y la partida sigue para los demás.
3. Para rehabilitarla: `POST /api/v1/admin/users/{id}/unsuspend`. La persona debe iniciar sesión de nuevo.

## Verificación

- `GET /api/v1/admin/audit` muestra `match.terminated` o `user.suspended` con tu `actorId`, tu IP y el `correlationId`.
- `GET /api/v1/admin/connections` ya no lista sus conexiones.
