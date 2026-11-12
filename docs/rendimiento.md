# Rendimiento

Prueba de carga reproducible del servidor de tiempo real, punto de quiebre de una réplica y dos
optimizaciones medidas antes/después. Corresponde a la sección 9 de
[planteamiento/02-arquitectura.md](../planteamiento/02-arquitectura.md). La herramienta y cómo correrla
(también contra Azure) están en [loadtests/README.md](../loadtests/README.md).

## 1. Metas

| Meta | Cómo se verifica |
|---|---|
| p95 < 200 ms entre que llega la jugada que cierra la ronda (la última orden, o el vencimiento del plazo) y que **todos** los jugadores reciben `RoundResolved` | Latencia por ronda medida en el cliente (§2.2) |
| Ninguna jugada perdida ni duplicada | Todas las órdenes reciben ACK, `seq` continuo por jugador, ninguna orden aceptada reemplazada por GUARDAR (§2.3) |
| Tasa de error < 1 % | Errores / operaciones (§2.3) |
| CPU y memoria por partida activa; punto de quiebre de una réplica | `docker stats` por etapa de una rampa (§2.4) |

## 2. Método

### 2.1 Escenario

`loadtests/Ascua.LoadTests` (NBomber 6.7.1). Cada partida tiene 4 bots y cada bot usa su propia conexión
SignalR (WebSockets, sin negotiate, **MessagePack**, ticket de un solo uso), igual que el cliente real:

1. Antes de la carga se registran e inician sesión 4 bots por partida (Argon2id en el servidor; esto no se mide).
2. Cada partida se crea con el endpoint de desarrollo `POST /api/v1/dev/matches` (sin salas); los 4 bots
   piden ticket, se conectan y llaman a `JoinMatch`. En el draft cada bot elige al azar apenas recibe la oferta.
3. En cada `RoundOpened` cada bot "piensa" de 0 a 2 s (uniforme) y envía una orden válida al azar
   (MOVER o ATACAR en una dirección, o GUARDAR). La ronda dura **3 s** (en producción, 20 s) para generar carga.
4. Al terminar la partida (`MatchFinished`), ese mismo hilo de NBomber arranca otra con los mismos 4 usuarios:
   cada etapa mantiene N partidas en curso todo el tiempo (`KeepConstant`).

La rampa son etapas de 60 s con 16, 32, 48, 64, 80, 96, 112 y 128 partidas simultáneas, precedidas por 20 s sin partidas
(línea base de CPU y memoria). De cada etapa se descartan los primeros 10 s (la transición mientras arrancan
las partidas nuevas).

### 2.2 Cómo se mide la latencia de la meta

El mensaje `RoundResolved` no trae el instante de cierre, pero el servidor ya publica los dos instantes que
lo definen, así que **no hizo falta cambiar el contrato**:

- `SubmitOrder` responde `receivedAt` (hora del servidor, en ms) al aceptar cada orden;
- `RoundOpened` trae el `deadline` de la ronda y cuántos jugadores se esperan (`expectedPlayers`).

Los 4 bots de una partida viven en el mismo proceso, así que por ronda se calcula:

```
disparo  = máx(receivedAt de las órdenes aceptadas)   si llegaron las de todos los esperados
         = deadline                                    si no (la ronda cerró por plazo)
latencia = máx(instante en que cada uno de los 4 bots recibió RoundResolved) − disparo
```

La muestra se toma cuando llegaron los 4 `RoundResolved` **y** todas las respuestas de `SubmitOrder` de
esa ronda (un ACK puede procesarse después del push). Un jugador caído no está entre los esperados: la
ronda cierra sin él.

**Relojes.** En local el generador y la API corren en contenedores sobre la misma VM de Docker, con el mismo
reloj (`--clock sync`). Como control, el generador estima igual el desfase servidor−cliente con cada ACK
(estilo NTP: `receivedAt − (envío + recepción)/2`, quedándose con la muestra de menor RTT); en todas las
corridas locales la mediana quedó en torno a 0 ms (±1 ms, la resolución de los timestamps). Contra una URL
remota se usa `--clock estimate`, que aplica ese desfase por conexión; el error queda acotado por la mitad
del RTT mínimo.

### 2.3 Jugadas perdidas, duplicadas y errores

Cada bot verifica:

- **ACK de todas las órdenes**: cada `SubmitOrder` termina en `ACCEPTED`, o en un rechazo previsto
  (`PLAYER_DOWN`, héroe caído). Una excepción o un código inesperado cuenta como error.
- **Ninguna duplicada**: cada orden lleva un `clientActionId` nuevo, así que un `DUPLICATE_ACTION` es una duplicación.
- **`seq` sin huecos por jugador**: el `previousSeq` de cada `RoundResolved` debe ser el último `seq` que ese
  jugador vio (del ACK de `JoinMatch`, de un `Snapshot` o del `RoundResolved` anterior).
- **Ninguna perdida**: si a un jugador cuya orden fue aceptada le llega `DEFAULT_GUARD_APPLIED`, el servidor
  la perdió y la reemplazó por GUARDAR.
- **Difusión completa**: los 4 jugadores reciben el `RoundResolved` de cada ronda cerrada.
- **Órdenes tardías** (`STALE_ROUND` de un jugador activo): con 0-2 s de espera y rondas de 3 s solo ocurren
  si el servidor se atrasa, así que cuentan como error.

Tasa de error = (errores de órdenes + duplicadas + huecos + perdidas + difusiones faltantes + tardías +
fallos al armar partidas) / (órdenes enviadas + 4 × rondas + partidas iniciadas).

### 2.4 CPU y memoria

`loadtests/run.sh` toma una muestra de `docker stats` del contenedor de la API por segundo (CPU en % de un
núcleo: con `cpus: 0.5` el máximo es 50 %). Por etapa se promedian las muestras y se calcula
`(CPU de la etapa − CPU en reposo) / partidas activas` (igual para la memoria).

### 2.5 Entorno

| | |
|---|---|
| Máquina | Laptop Intel Core i5-13420H (8 núcleos / 12 hilos), 16 GB de RAM, Windows 11 Pro |
| Docker | Docker Desktop 29.7.2 sobre WSL2 (kernel 6.6.87), 12 CPU y 7,6 GiB para la VM |
| API | Imagen del repo (`Dockerfile`, .NET 10, chiseled), `cpus: 0.5`, `mem_limit: 1g` (como una réplica mínima de Container Apps), `ASPNETCORE_ENVIRONMENT=Development` (igual que el compose base), logs en `Information` |
| Postgres / Redis | `postgres:17-alpine` (17.11) y `redis:7-alpine` (7.4.12) **sin límites**: en Azure son servicios administrados aparte |
| Generador | Contenedor `loadtests/Dockerfile` en la misma red de compose, sin límites (nunca pasó de ~1 núcleo) |
| Fecha | 30-oct-2026, corridas de 15:58 a 17:18 UTC |

Limitaciones: es una laptop con otras cargas (en paralelo había contenedores de otro proyecto y sesiones
compilando), así que las corridas tienen ruido. Por eso las comparaciones se hacen con el **mismo escenario
y carga fija** (§4), y no solo con la rampa. Postgres y Redis comparten la máquina con la API. Cada corrida
parte de una base vacía (`docker compose -p ascua-carga down -v`).

## 3. Resultados

Las tablas completas de cada corrida (con conteos de órdenes, RTT y rondas por plazo) están en
`loadtests/reports/*.md`. En **todas** las corridas: 0 jugadas perdidas, 0 duplicadas, 0 huecos de `seq`
y 0 difusiones faltantes. Las únicas "tardías" aparecieron con la réplica saturada.

### 3.1 Rampa y punto de quiebre (versión final)

Rampa de 60 s por etapa con la versión final (optimización 1 activa). Latencia de la meta en ms:

| Partidas | p50 | p95 | p99 | Tasa de error | CPU (de 50 % máx.) | RAM | Meta |
|---:|---:|---:|---:|---:|---:|---:|:---:|
| 16 | 18,7 | 199,0 | 269,3 | 0,00 % | 35,9 % | 301 MiB | ✅ |
| 32 | 22,4 | 195,9 | 280,1 | 0,00 % | 42,2 % | 233 MiB | ✅ |
| 48 | 12,4 | 131,3 | 227,6 | 0,00 % | 36,5 % | 182 MiB | ✅ |
| 64 | 9,3 | 31,5 | 66,7 | 0,00 % | 40,4 % | 187 MiB | ✅ |
| 80 | 11,8 | 99,3 | 271,6 | 0,00 % | 45,4 % | 191 MiB | ✅ |
| 96 | 13,2 | 101,7 | 185,0 | 0,00 % | 47,2 % | 198 MiB | ✅ |
| 112 | 17,1 | 109,9 | 224,4 | 0,00 % | 49,3 % | 208 MiB | ✅ |
| 128 | 25,3 | **204,9** | 307,4 | 0,00 % | 49,7 % | 224 MiB | ❌ |

**Punto de quiebre: una réplica de 0,5 vCPU / 1 GiB soporta ~112 partidas simultáneas (448 jugadores
conectados) y se degrada con 128**, donde el p95 pasa de 200 ms con la CPU clavada en la cuota (50 %). Lo
que se degrada es la latencia, no la corrección: incluso saturada no se pierde ni se duplica ninguna jugada.

Las dos primeras etapas salen peor que las siguientes, aunque tienen menos carga: es el **calentamiento**
de la réplica (compilación JIT por niveles, conexiones nuevas a Postgres y Redis, y 16 partidas que arrancan
a la vez). Con carga fija pasa lo mismo: el p95 baja durante los primeros 2-3 minutos y después se estabiliza
(§4.1). En Azure conviene una etapa de calentamiento antes de medir.

**Repetida con los nueve héroes** (3-nov-2026, contrato 2.0.0, misma rampa y mismos límites;
`loadtests/reports/rampa-heroes.md`): el motor nuevo no empeora la capacidad.

| Partidas | p95 | p99 | Tasa de error | CPU (de 50 % máx.) | RAM | Meta |
|---:|---:|---:|---:|---:|---:|:---:|
| 16 | 69,3 | 101,7 | 0,00 % | 19,5 % | 284 MiB | ✅ |
| 64 | 21,3 | 60,8 | 0,00 % | 38,9 % | 225 MiB | ✅ |
| 112 | 113,5 | 216,8 | 0,00 % | 48,9 % | 245 MiB | ✅ |
| 128 | 170,0 | 210,3 | 0,00 % | 49,9 % | 248 MiB | ✅ |

Tampoco hubo jugadas perdidas ni duplicadas, huecos de `seq` ni difusiones faltantes. Esta vez 128 partidas
quedó dentro de la meta, pero con la CPU en la cuota y por una sola corrida, así que **se sigue declarando
~112 partidas por réplica**, sin buscar un punto de quiebre más alto. Los bots de carga juegan órdenes al azar:
esta corrida no mide el costo de cada habilidad por separado. Las cifras de §3.2 y §4 son de la versión anterior.

### 3.2 CPU y memoria por partida activa

Con carga fija de 64 partidas, en estado estable (minutos 3 y 4, `loadtests/reports/fijo64-despues.md`):

| | Versión final |
|---|---|
| CPU de la réplica | 35,4 % de un núcleo (reposo: 0,7 %) |
| **CPU por partida activa** | (35,4 − 0,7) / 64 = **0,54 % de un núcleo ≈ 5,4 mvCPU** (≈ 1,4 mvCPU por jugador) |
| Memoria del proceso | 179-180 MiB (en reposo, tras registrar los bots: 310 MiB) |
| **Memoria por partida activa** | ≤ 2,8 MiB (cota superior: todo el proceso / 64) |

La memoria no crece con la carga: entre 48 y 112 partidas el proceso se mantiene en 180-210 MiB, y el ruido
del GC (que va liberando lo que dejó el registro de bots, con Argon2id de 19 MiB por hash) es mayor que lo
que agrega cada partida. Por eso se informa la cota superior y no el delta contra el reposo, que sale
negativo. El estado de cada partida vive en Redis (~10 KB de instantánea JSON) y Postgres, no en la API.
Con 1 GiB, la memoria nunca es el límite: lo es la CPU.

Extrapolación (supuesto lineal, a validar en Azure): la cuota de 0,5 vCPU da para ~90 partidas a 0,54 %
cada una, y se llega a 112 porque la latencia aguanta un rato con la CPU al tope. Para 1 vCPU se esperarían
~180 partidas por réplica.

## 4. Optimizaciones medidas

### 4.1 Optimización 1: E/S de Redis en el thread pool de .NET

**Diagnóstico.** La rampa inicial ya se degradaba con 96 partidas y con una CPU que, en promedio, no
llegaba al tope. Una traza de CPU de la API bajo carga (`dotnet-trace collect-linux --profile cpu-sampling`
desde un contenedor que comparte el espacio de PID de la API) mostró que **~38 % de las muestras de CPU**
estaban en `Pipelines.Sockets.Unofficial.DedicatedThreadPoolPipeScheduler.RunWorkLoop`: los hilos de E/S
dedicados de StackExchange.Redis. De eso, ~14 % era `sched_yield`, es decir, hilos girando en vacío. Con una
cuota de CPU fraccionaria (CFS, 0,5 vCPU), cada ciclo de ese giro sale de la misma cuota que usa la lógica
del juego, y cuando la cuota se agota el kernel congela el contenedor hasta el próximo período: eso explica
los picos de latencia. La app tiene dos multiplexores, el del estado caliente y el del backplane de SignalR,
y los dos usan ese planificador por defecto.

Ninguna de las candidatas de §9 aplicaba: MessagePack con deltas y la instantánea en Redis ya estaban
implementadas, y la difusión ya es una tarea por jugador con `Task.WhenAll`.

**Cambio** (commit aislado, `src/Ascua.Infrastructure/Redis/RedisConnection.cs`): los dos multiplexores
usan `SocketManager.ThreadPool`, o sea, el thread pool normal de .NET. `Ascua:Redis:DedicatedSocketThreads=true`
vuelve al comportamiento anterior. Así las dos versiones se midieron **con la misma imagen**, cambiando solo
esa variable (`ASCUA_REDIS_DEDICATED_THREADS` en la compose de carga).

**Antes/después con el mismo escenario y carga fija de 64 partidas**, en estado estable (minutos 3 y 4):

| 64 partidas fijas | Antes (hilos dedicados) | Después (thread pool) | Cambio |
|---|---:|---:|---:|
| CPU de la réplica | 46,4 % | 35,4 % | **−24 %** |
| CPU por partida (absoluta) | 0,72 % | 0,55 % | −24 % |
| Latencia p50 | 10,4 ms | 8,2-8,3 ms | −20 % |
| Latencia p95 | 51-58 ms | 15-16 ms | **−70 %** |
| Latencia p99 | 85-99 ms | 39-46 ms | −55 % |
| RTT de `SubmitOrder` p95 | 44 ms | 4-5 ms | −90 % |
| CPU en reposo | 7,5 % | 0,7 % | |

**Rampa completa** (mismo escenario; `rampa-antes.md` y `rampa-despues.md`):

| Partidas | p95 antes | p95 después | CPU antes | CPU después |
|---:|---:|---:|---:|---:|
| 48 | 32,5 | 131,3¹ | 39,8 % | 36,5 % |
| 64 | 73,8 | 31,5 | 47,4 % | 40,4 % |
| 80 | 175,5 | 99,3 | 50,1 % (tope) | 45,4 % |
| 96 | **917,5** ❌ | 101,7 | 50,7 % (tope) | 47,2 % |
| 112 | — | 109,9 | — | 49,3 % |
| 128 | — | **204,9** ❌ | — | 49,7 % (tope) |

¹ Todavía en calentamiento (§3.1); las etapas de 16 y 32 de la corrida "después" también.

**Punto de quiebre: de 80 a 112 partidas por réplica (+40 %).**

Con la réplica saturada la ventaja se achica, porque las dos versiones llegan al tope de CPU. Con 96
partidas fijas (`fijo96-*.md`, tercer minuto) el p95 pasa de 105 a 89 ms y se resuelven 3 % más rondas por
minuto. La mejora real está en el margen: la misma latencia con un cuarto menos de CPU, o un 40 % más de
partidas por réplica antes de romper la meta.

### 4.2 Optimización 2: cierre puntual al vencer el plazo

**Problema.** La meta también cuenta las rondas que cierran por plazo (algún jugador no jugó). Antes, esas
rondas solo las cerraba el sondeo de `RoundTimerService` cada 250 ms (`TimerInterval`), así que el cierre
llegaba entre 0 y 250 ms tarde, más que toda la meta. En el escenario base esto casi no se ve, porque los
bots siempre juegan antes de los 3 s.

**Cambio** (commit aislado, `MatchEngine.ScheduleDeadline`): la réplica que abre un plazo programa además
un temporizador en proceso para el instante exacto. Si la ronda cerró antes, ese temporizador no hace
nada. El sondeo de Redis sigue siendo la garantía si la réplica cae, y el lock por partida más la versión
en Postgres impiden un doble cierre si los dos llegan a la vez. `Ascua:Realtime:PreciseDeadlines=false`
vuelve al comportamiento anterior (`ASCUA_PRECISE_DEADLINES` en la compose de carga).

**Escenario:** 48 partidas fijas con `--skip-rate 0.15` (cada bot falta a una ronda con probabilidad 15 %,
así ~37 % de las rondas cierran por plazo), 3 etapas de 60 s; se comparan los minutos 2 y 3.

| 48 partidas, 15 % de faltas | Antes (solo sondeo) | Después (temporizador puntual) |
|---|---:|---:|
| p95 de las rondas que cerraron por plazo | 697-699 ms | 321-403 ms (**−45 % a −54 %**) |
| p95 de todas las rondas | 411-532 ms | 310-417 ms |
| p99 de todas las rondas | 794-795 ms | 499-592 ms |
| CPU | 45,9-47,1 % | 43,8-47,1 % |

Ese par (`plazo48-*.md`) se corrió con la máquina más cargada que el resto: el host estaba al ~39 % sin la
prueba y las dos versiones llegaron cerca del tope de CPU con solo 48 partidas. Un par anterior, en un
momento más tranquilo (CPU de 23-32 %) y con una versión previa del temporizador, dio la misma dirección:
p95 por plazo de 266-268 a 236-246 ms, y p95 total de 260-262 a 153-174 ms. Con eso, el escenario pasó de
incumplir la meta a cumplirla.

**Por qué no baja a ~20 ms.** Instrumentando el motor en una corrida corta (8 partidas, 50 % de faltas), el
propio temporizador en proceso despertaba con p50 de 3 ms y p95 de 73 ms de atraso (máximo 80 ms). Con una
cuota CFS de 0,5 vCPU, cuando la réplica agota su cuota del período de 100 ms el kernel la congela hasta el
período siguiente, y ningún temporizador puede despertar antes. Pasa lo mismo con todo el trabajo de la
réplica: es la causa de fondo de la cola de latencia cerca del tope, y es la razón por la que la
optimización 1, que libera cuota, mueve tanto el p95.

## 5. Conclusiones

- **Se cumple la meta** con una réplica de 0,5 vCPU / 1 GiB hasta **~112 partidas simultáneas (448 jugadores)**:
  p95 < 200 ms, 0 % de errores. En ninguna corrida, ni con la réplica saturada, hubo jugadas perdidas,
  duplicadas, huecos de `seq` o difusiones faltantes: la corrección no depende de la carga; la latencia, sí.
- **El recurso que limita es la CPU**: ~0,54 % de un núcleo por partida activa (≈ 5,4 mvCPU). La memoria se
  queda en ~180-220 MiB (≤ 2-3 MiB por partida) con el límite en 1 GiB. Escalar es sumar réplicas (el estado
  está en Redis/Postgres y el backplane ya reparte la difusión) o pasar a 1 vCPU.
- **Optimización 1** (E/S de Redis en el thread pool): −24 % de CPU con la misma carga, p95 de 51-58 a 15-16 ms
  con 64 partidas, y punto de quiebre de 80 a 112 partidas (+40 %). Es un cambio de configuración de una línea
  que no se ve leyendo el código: solo apareció con una traza de CPU bajo carga.
- **Optimización 2** (cierre puntual por plazo): saca hasta 250 ms del peor caso de las rondas en que alguien no
  juega; con 15 % de faltas, el p95 por plazo bajó entre 45 % y 54 %.
- **Para Azure** (lo corre Juan Pablo con `loadtests/run.sh --url ...`, ver [loadtests/README.md](../loadtests/README.md)):
  fijar 1 réplica, calentar 2-3 minutos antes de medir, leer CPU/RAM en las métricas de Container Apps para las
  mismas ventanas, y comparar con estas cifras. Ahí hay latencia de red real entre la API, Redis y Postgres
  administrados, que acá no existe: se espera un p50 más alto y un punto de quiebre parecido o menor.
- **Siguiente optimización candidata**, según la misma traza: los envíos por socket a Redis (~14 % de la CPU en
  `send`) por los muchos comandos chicos de cada orden (`FindAck`, `GetAsync`, el script de envío y dos lecturas
  para `AllDecided`). Juntarlos en un solo script Lua o en un batch bajaría la CPU por orden.


## Medición en Azure (12-nov-2026 UTC)

[Workflow y artifact original](https://github.com/JanPMolina/ascua/actions/runs/43128625356). Inicio del generador: **00:45:43 UTC**. Destino: dev, una réplica de **0,5 vCPU / 1 GiB**, rondas de 3 s, etapas de 120 s, omitiendo los primeros 10 s de cada etapa. Reloj remoto estimado; desfase mediano servidor−cliente: −0,7 ms. Container Apps y Redis están en West US 2; PostgreSQL, en North Central US por restricción de esta suscripción.

Las muestras de Azure Monitor se consultaron con las dimensiones reales `revisionName` y `podName`. Todas las filas corresponden a la réplica `ca-ascua-dev--load-43128625356-1-85ddcd69fd-zhsk2`. CPU y RAM son promedios de muestras PT1M dentro de cada etapa: esa granularidad puede mezclar un borde entre etapas. El p95 viene del generador, no de promedios de Azure Monitor.

| Partidas | p95 ms | Tasa error | CPU núcleos | CPU % de la cuota de 0,5 vCPU | RAM MiB | Órdenes perdidas | Duplicadas | Huecos de secuencia |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 16 | 510,6 | 0,05% | 0,38 | 75,07 | 505,42 | 0 | 0 | 0 |
| 32 | 402,5 | 0,06% | 0,38 | 76,42 | 475,65 | 0 | 0 | 0 |
| 64 | 3809,1 | 4,13% | 0,48 | 96,16 | 418,25 | 0 | 0 | 0 |
| 96 | 9530,6 | 24,99% | 0,48 | 95,48 | 387,21 | 0 | 0 | 0 |
| 112 | 10946,6 | 15,17% | 0,48 | 95,60 | 418,81 | 0 | 0 | 1 |

**Resultado: la meta no se alcanzó ni en la primera etapa.** Desde 64 partidas la CPU promedia cerca de su cuota y la latencia se degrada. La separación regional puede añadir tiempo de ida y vuelta, pero atribuirle la causa exige perfilar; no se demuestra con esta tabla. El punto local de ~112 partidas por réplica no se puede trasladar a Azure con esta medición.

La prueba restauró `EnableDevEndpoints=false`, retiró las variables temporales de duración de ronda y límite de autenticación, y devolvió la escala a 0–2 réplicas. Se verificó de nuevo en Azure. No se cambió el generador ni se rebajó su umbral para obtener un resultado verde.
