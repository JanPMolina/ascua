# Pruebas de carga

Generador [NBomber](https://nbomber.com) 6.7.1 (`Ascua.LoadTests`) que juega N partidas simultáneas de 4 bots
por el hub SignalR con **MessagePack**, como un cliente real. Método, resultados y conclusiones:
[docs/rendimiento.md](../docs/rendimiento.md).

> NBomber es gratis solo para uso personal o académico (lo avisa en consola). Este repositorio es un
> proyecto universitario; para uso en una organización hace falta licencia.

## Correr

Solo hace falta Docker. Desde la raíz del repo:

```bash
loadtests/run.sh --stages 4,8,12,16,24,32 --stage-seconds 60 --label rampa
```

```powershell
./loadtests/run.ps1 -Stages 4,8,12,16,24,32 -StageSeconds 60 -Label rampa
```

Sin `--url`, el script levanta la API con `docker compose` limitada a **0,5 vCPU / 1 GiB**
([docker-compose.load.yml](docker-compose.load.yml)), con rondas de 3 s, toma una muestra de `docker stats`
por segundo y corre el generador dentro de la red de compose.

### Contra Azure (u otra URL)

```bash
loadtests/run.sh --url https://<app>.azurecontainerapps.io --stages 10,20,40 --stage-seconds 120 --label azure
```

Requisitos en el entorno destino (solo mientras dura la prueba):

- `Ascua__Realtime__EnableDevEndpoints=true`: los bots arrancan partidas con `POST /api/v1/dev/matches`.
- `Ascua__Realtime__RoundDuration=00:00:03` para generar carga (si no, cada ronda dura 20 s).
- `Ascua__RateLimiting__Auth__PermitLimit` alto (p. ej. 100000): todos los bots se registran desde una IP.
  El generador igual respeta `Retry-After` si recibe 429.
- Fijar la réplica (`minReplicas = maxReplicas = 1`) para medir el punto de quiebre de **una** réplica.

Contra una URL remota el reloj del generador no es el del servidor: se pasa `--clock estimate` (el script
ya lo hace) y la hora del servidor se estima con los ACK de `SubmitOrder` (muestra de menor RTT, estilo NTP).
CPU y memoria se leen en las métricas de Container Apps (`UsageNanoCores`, `WorkingSetBytes`) para la
misma ventana de tiempo que cada etapa (las horas UTC quedan en `resultados.json`).

## Parámetros

| Parámetro | Por defecto | Qué hace |
|---|---|---|
| `--url` | compose local | Base de la API. |
| `--stages` | `10` | Partidas simultáneas por etapa (coma). Una sola = carga constante. |
| `--stage-seconds` | `60` | Duración de cada etapa; los primeros 10 s de cada una no cuentan (transición). |
| `--idle-seconds` | `15` | Pausa sin partidas antes de la primera etapa (línea base de CPU/RAM). |
| `--think-ms` | `2000` | Cada bot espera al azar 0..think-ms antes de enviar su orden. |
| `--skip-rate` | `0` | Probabilidad de que un bot no juegue una ronda (fuerza cierres por plazo). |
| `--clock` | `sync` | `sync` = mismo reloj (mismo host); `estimate` = corrige el desfase con los ACK. |
| `--label` | `carga` | Nombre de la corrida. |

Variables de la compose de carga: `ASCUA_ROUND_DURATION` (`00:00:03`), `ASCUA_REDIS_DEDICATED_THREADS`
(`false`; `true` = comportamiento previo a la optimización 1) y `ASCUA_PRECISE_DEADLINES`
(`true`; `false` = sin la optimización 2).

## Resultados

Cada corrida deja `loadtests/reports/<fecha>-<label>/`:

- `resumen.md`: tabla por etapa (p50/p95/p99, errores, huecos de seq, perdidas, duplicadas, CPU/RAM y su valor por partida).
- `resultados.json`: lo mismo en JSON.
- `stats.csv`: muestras de `docker stats` (solo local).
- `nbomber/`: informe HTML/MD de NBomber.

Las carpetas de corrida no se versionan; los resúmenes de las corridas citadas en el informe se copian a
`loadtests/reports/<label>.md`.

El código de salida es 0 si la primera etapa cumple la meta (lo usa el workflow manual `Carga (humo)`).
