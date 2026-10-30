# Runbook: Redis o Postgres caídos

Cada base cumple un papel distinto:
- **Postgres** es la fuente de verdad: guarda las jugadas confirmadas (`match_events` con `UNIQUE(match_id, seq)`), los usuarios y la auditoría.
- **Redis** guarda el estado caliente de las partidas, el lock del cerrador de ronda, los plazos de ronda y de gracia, el lobby y la cola. También sirve como *backplane* de SignalR entre réplicas.

## Síntoma

- `/health/ready` responde 503 y Container Apps deja de enviarle tráfico a esa réplica. `/health/live` sigue en 200, así que el contenedor no se reinicia.
- Suben los errores 5xx, `ascua.round.resolve.duration` o `ascua.broadcast.latency`.
- En los logs aparecen `RedisConnectionException` o `RedisTimeoutException`, o errores de Npgsql.

## Diagnóstico

1. El cuerpo de `/health/ready` dice qué *check* falló: `postgres` o `redis`.
2. **En Azure:** revisa en el portal el estado del recurso (Azure Cache for Redis o PostgreSQL Flexible Server) y si hay un mantenimiento programado.
3. **En local:** `docker compose ps` y `docker compose logs postgres redis`.

## Qué pasa mientras dura la caída

**Si se cae Postgres:**
- No se confirman rondas nuevas.
- Lo ya confirmado no se pierde.
- Login y registro fallan con 5xx.
- Al volver, el motor recarga desde Postgres cualquier estado caliente que haya quedado viejo.

**Si se cae Redis:**
- No se pueden cerrar rondas: sin lock no hay cerrador.
- El lobby y la cola no responden, y la difusión entre réplicas se detiene.
- Al volver, los plazos vencidos siguen en el *sorted set* y `RoundTimerService` los procesa en la siguiente pasada.
- Los jugadores reconectan con un ticket nuevo y recuperan el estado con `JoinMatch`.
- Si Redis perdió los datos (reinicio sin persistencia), el estado de una partida se reconstruye desde Postgres (`RepairFromStoreAsync`) cuando un jugador vuelve a actuar sobre ella.
- Las partidas que nadie retoma pasan a `EXPIRED` por inactividad. Esa limpieza se basa en Postgres, así que funciona aunque Redis haya perdido los datos.

En ambos casos el proceso no se cae, y las demás réplicas siguen igual si la falla es de red y no del servicio.

## Acción

1. **No reinicies réplicas para «arreglar» la caída**: no ayuda, porque el problema está en la dependencia, no en el proceso.
2. **En Azure**, si la caída dura más de unos minutos (lo hace Juan Pablo):
   - revisa la conectividad de red y la identidad administrada (permisos o token de Entra vencido);
   - si es un incidente del servicio, espera a que se recupere o escala el plan del recurso.
3. Cuando la sonda vuelva a 200, revisa que `ascua.matches.active` recupere su valor normal.
4. Revisa si alguna partida quedó `FAULTED` durante la caída (runbook [Partida `FAULTED`](partida-faulted.md)).
5. Las partidas que nadie retomó expiran solas a los `Ascua__Realtime__InactivityTimeout` (10 min por defecto).

## Verificación

- `/health/ready` responde 200 en todas las réplicas.
- Desaparecen los errores de conexión en los logs.
- Una partida nueva se puede crear y jugar: alcanza con el bot en dev.
