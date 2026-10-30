# Runbooks de operación

Pasos para diagnosticar y resolver incidentes del servidor de Ascua. Cada runbook dice:
- **Síntoma:** cómo se reconoce el incidente (alerta, métrica o log).
- **Diagnóstico:** qué revisar.
- **Acción:** qué hacer.
- **Verificación:** cómo confirmar que quedó resuelto.

| Runbook | Cuándo |
|---|---|
| [Partida `FAULTED`](partida-faulted.md) | Sube `ascua.matches.faulted`, o un jugador reporta que su partida se detuvo |
| [Moderación: terminar una partida o suspender una cuenta](moderacion.md) | Trampa, abuso o partida trabada |
| [Redis o Postgres caídos](dependencias-caidas.md) | `/health/ready` falla, errores 5xx o latencia alta |
| [Rotar la clave de firma JWT](rotar-clave-jwt.md) | Rotación programada o sospecha de que la clave se filtró |
| Rollback de una versión | Lo escribe Juan Pablo junto con `rollback.yml` (ver [infra/README.md](../../infra/README.md)) |

## Herramientas comunes

- **Logs y trazas:** cada línea del motor de partidas lleva `MatchId`, `PlayerId` y `Round` en su *scope*.
  - En Azure se consultan en Application Insights.
  - En local: `docker compose logs api`.
- **Métricas propias** (OpenTelemetry, *meter* `Ascua`):

  | Métrica | Qué mide |
  |---|---|
  | `ascua.matches.active` | Partidas activas |
  | `ascua.connections.active` | Conexiones abiertas en la réplica |
  | `ascua.orders.rate` | Jugadas aceptadas |
  | `ascua.round.resolve.duration` | Duración del cierre de ronda |
  | `ascua.broadcast.latency` | Latencia desde que se cierra la ronda hasta que llega la nueva vista |
  | `ascua.matches.faulted` | Partidas aisladas como `FAULTED` |

- **Sondas:**
  - `/health/live`: el proceso responde.
  - `/health/ready`: Postgres y Redis responden.
- **Administración** (rol `admin`, todo queda auditado, ver [docs/api/administracion.md](../api/administracion.md)):
  - `GET /api/v1/admin/matches`: partidas activas.
  - `GET /api/v1/admin/connections`: conexiones abiertas.
  - `GET /api/v1/admin/audit`: registro de auditoría.
