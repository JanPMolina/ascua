# Administración y auditoría

Endpoints bajo `/api/v1/admin` (OpenAPI en `/openapi/v1.json`, etiqueta **Admin**). Cumplen el requisito del
enunciado: *ver partidas activas y conexiones, terminar una partida, suspender un usuario; solo el rol
administrador; cada acción queda auditada*.

## Acceso

- Política `admin`: JWT válido de una cuenta **no suspendida** con el claim `role=admin`.
  - Sin token o con token inválido → **401** `urn:ascua:problem:unauthorized`.
  - Con token de un `player` → **403** `urn:ascua:problem:forbidden`.
- El rol viaja en el JWT: tras promover a alguien, debe iniciar sesión de nuevo para obtenerlo.
- *Rate limiting* por usuario (política `user`), igual que el resto de la API autenticada.

### Primer administrador (bootstrap)

No hay credenciales en el repositorio ni un usuario admin por defecto. Para crear el primero:

1. Registrar la cuenta normalmente (`POST /api/v1/auth/register`) con su propia contraseña.
2. Arrancar la API con `Ascua__Admin__BootstrapUsername=<ese usuario>`.
3. Al arrancar, si el usuario existe y es `player`, se promueve a `admin` y se audita como
   `user.promoted` con `actorId: null` (acción del sistema). Es idempotente: si ya es admin no hace nada;
   si el usuario no existe, solo deja un aviso en el log.
4. La variable se puede quitar después; el rol queda guardado en `users.role`.

## Endpoints

| Método y ruta | Qué hace | Errores propios |
|---|---|---|
| `GET /admin/matches?status=active&page=1&pageSize=20` | Partidas `DRAFTING`/`IN_PROGRESS`, la de actividad más reciente primero: id, estado, ronda, jugadores (asiento, héroe, `connected`), `connectedPlayers`, `createdAt`, `lastActivityAt` (última confirmación en Postgres) | 400 `validation` (`status` distinto de `active`, `page < 1`, `pageSize` fuera de 1-100) |
| `GET /admin/connections` | Conexiones de tiempo real vivas agregadas por partida y usuario: `totalConnections`, `connectedUsers` y, por partida, los jugadores conectados con su número de conexiones abiertas (todas las réplicas). **Nunca** devuelve ids de conexión, tickets ni tokens | — |
| `POST /admin/matches/{id}/terminate` `{ "reason": "…" }` | Detiene la partida (ver abajo) | 404 `match-not-found`, 409 `match-not-active` (ya terminó), 409 `match-busy` (se estaba cerrando una ronda; reintentar) |
| `POST /admin/users/{id}/suspend` `{ "reason": "…" }` | Suspende la cuenta (ver abajo). Idempotente | 404 `user-not-found`, 409 `cannot-target-self` |
| `POST /admin/users/{id}/unsuspend` `{ "reason": "…" }` | Rehabilita la cuenta; tiene que volver a iniciar sesión | 404 `user-not-found` |
| `GET /admin/audit?page=1&pageSize=50` | Registro de auditoría, lo más reciente primero | 400 `validation` |

`reason` es obligatorio (3-500 caracteres) y queda en la auditoría: no debe llevar datos sensibles.

### Terminar una partida

Estado terminal nuevo **`TERMINATED`** (se suma a `FINISHED`, `ABANDONED`, `EXPIRED` y `FAULTED`; check
`ck_matches_status`). `MatchEngine.TerminateAsync`:

1. Toma el lock de la partida en Redis (el mismo de cada ronda): no puede cruzarse con un cierre de ronda.
2. En **una transacción** de Postgres marca `matches.status = TERMINATED` (solo si seguía viva) e inserta
   la fila de `audit_log`. Si otra réplica la terminó antes, no se audita ni se avisa dos veces.
3. Guarda la instantánea caliente, **libera la "partida activa"** de todos los jugadores (pueden volver a
   salas y cola) y difunde al grupo `MatchFinished` con `outcome: TERMINATED` y un `Error` con código
   `KICKED_BY_ADMIN` (`details.status: TERMINATED`), según `contracts/asyncapi.yaml` 1.2.0.
4. Unos 2 s después (para que lleguen esos mensajes) corta las conexiones de la partida en todas las réplicas
   (canal de control `ascua:realtime:control`, orden `kick-match`).

### Suspender una cuenta

1. En **una transacción**: `users.is_suspended = true` + fila de `audit_log` (mismo `SaveChanges`).
2. Revoca todas las familias de refresh del usuario (`refresh_tokens.revoked_at`); un refresh posterior
   responde 401.
3. **Access token vigente**: en cada petición, `OnTokenValidated` consulta el estado de acceso
   (`IUserAccessGate`: clave Redis `ascua:user:{id}:access`, TTL 30 s, con respaldo en Postgres). Al suspender
   se escribe de inmediato, así que el token deja de servir en la siguiente petición en cualquier réplica:
   **403** `urn:ascua:problem:account-suspended`. Si Redis no responde se consulta Postgres (nunca se deja
   pasar sin saber). El mismo chequeo se hace al consumir un ticket WS.
4. Corta sus conexiones WS en todas las réplicas con `Error` `KICKED_BY_ADMIN` (orden `kick-user`).
5. Si tenía partida activa, se le aplica el abandono (su héroe se retira; la partida sigue para los demás);
   lo saca de su sala y de la cola de emparejamiento.
6. El login responde **403** `account-suspended` mientras siga suspendido.

Los pasos 2-6 se ejecutan después de confirmar el paso 1 y son idempotentes: si alguno falla, la acción ya
está registrada y la suspensión rige igual (login, refresh y JWT consultan el estado guardado).

## Auditoría (`audit_log`)

| Columna | Contenido |
|---|---|
| `id` | UUID v7 |
| `actor_id` | Admin que actuó; `null` = sistema (bootstrap). Sin FK: la auditoría sobrevive al borrado de cuentas |
| `action` | `match.terminated`, `user.suspended`, `user.unsuspended`, `user.promoted` |
| `target_type` / `target_id` | `match` o `user` y su id |
| `reason` | Motivo escrito por el admin |
| `metadata` | `jsonb` sin datos sensibles (estado anterior, ronda, jugadores, nombre de usuario). Nunca contraseñas, hashes ni tokens |
| `ip` / `correlation_id` | IP de origen (real detrás del ingress con `Ascua__TrustForwardedHeaders`) y `traceId` de OpenTelemetry |
| `created_at` | Momento de la acción |

- **Append-only**: la app solo inserta (`IAuditLog.Append`, sin métodos de cambio) y un trigger de Postgres
  rechaza `UPDATE`, `DELETE` y `TRUNCATE`.
- **Garantía de escritura**: cada acción y su registro se confirman en la misma transacción; no hay acción
  sin auditoría ni auditoría de una acción que no ocurrió.
- Otras features auditan con el puerto `IAuditLog` (`Ascua.Application.Audit`): `Append` + el
  `IUnitOfWork.SaveChangesAsync` del mismo scope.
