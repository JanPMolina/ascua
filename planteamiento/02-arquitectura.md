# Arquitectura del servidor (C# / .NET + SignalR en Azure)

## 1. Decisiones de stack

| Tema | Decisión | Motivo |
|---|---|---|
| Runtime | **.NET 10 (LTS)** con `Nullable=enable` y `TreatWarningsAsErrors=true` | Cumple ".NET 8+". `Microsoft.AspNetCore.OpenApi` de .NET 10 genera **OpenAPI 3.1** de forma nativa, que es lo que exige el enunciado. Con .NET 8 habría que forzarlo |
| Tiempo real | **SignalR** solo sobre WebSockets (`SkipNegotiation`) con el protocolo **MessagePack** | Evita *sticky sessions* en Container Apps, trae reconexión y grupos, y mensajes compactos |
| Difusión entre réplicas | **Backplane de Redis** para SignalR | Topología de referencia del enunciado |
| Estado caliente | Redis: instantánea de la partida, órdenes de la ronda, temporizadores, tickets, límites | Rápido y compartido entre réplicas |
| Persistencia | **PostgreSQL** (Flexible Server) con EF Core y Npgsql | Eventos confirmados en modo *append-only*, usuarios, historial y auditoría |
| REST | Minimal APIs, `AddProblemDetails()` (RFC 9457) y UI **Scalar** en `/docs` | Estándar y liviano |
| Contrato en tiempo real | **AsyncAPI 3.0** escrito a mano en `contracts/asyncapi.yaml` y validado en CI | El enunciado lo exige |
| Autenticación | JWT propio: *access* de 10 min y *refresh* rotativo con detección de reutilización. Contraseñas con **Argon2id** | Control total; no depende de un proveedor externo |
| IaC / CI | **Bicep** y **GitHub Actions** (OIDC federado contra Azure) | Nativo de Azure, sin secretos en el pipeline |
| Observabilidad | OpenTelemetry → Azure Monitor (Application Insights) | Exigido |
| Pruebas de carga | Proyecto **`Ascua.Bot`** (cliente SignalR .NET) con **NBomber** | Mide la latencia real desde la orden hasta que todos reciben el estado |

## 2. Solución (capas)

```
src/
  Ascua.Domain/          ← PURO: sin red, BD ni framework (requisito)
    Model/               GameState, Hero, Enemy, Room, Blessing, Pact...
    Rules/               Resolver.Resolve(state, orders, IRandom) → (newState, events)
    Views/               PlayerViewProjector.For(state, playerId)  ← filtra la información oculta
    Abstractions/        IRandom (implementado con un CSPRNG en Infrastructure)
  Ascua.Application/     Casos de uso: CreateRoom, Join, Ready, SubmitOrder, CloseRound, Reconnect...
                         Puertos: IMatchStore, IEventStore, IRoundScheduler, IBroadcaster, IClock
  Ascua.Infrastructure/  Redis (StackExchange.Redis), EF Core/Npgsql, Key Vault, CSPRNG, Argon2id
  Ascua.Api/             Minimal APIs REST, MatchHub (SignalR), auth, rate limiting, health, OTel
  Ascua.Bot/             Bot y generador de carga (NBomber)
  Ascua.Web/             Cliente de prueba web (HTML + canvas + @microsoft/signalr), servido estático
tests/
  Ascua.Domain.Tests/        Unitarias de reglas (xUnit + FsCheck para propiedades)
  Ascua.Integration.Tests/   Testcontainers (Postgres + Redis) + WebApplicationFactory
  Ascua.Concurrency.Tests/   Órdenes duplicadas, carreras de unión, cierre de ronda en 2 réplicas
  Ascua.Security.Tests/      Autorización por partida, no filtración, límites, tickets
  Ascua.Resilience.Tests/    Matar una réplica a mitad de partida y reanudar
infra/       main.bicep + módulos
contracts/   asyncapi.yaml (+ openapi.json exportado y comparado en CI)
loadtests/   escenarios y reportes
docs/        ADRs, modelo de amenazas, runbooks (rollback), costos
```

**Regla de dependencias:** `Domain` no referencia nada. `Application` solo referencia `Domain`. `Api` e `Infrastructure` dependen hacia adentro. Una prueba de arquitectura con NetArchTest lo verifica en CI.

## 3. Flujo de una ronda (el corazón del sistema)

```
Cliente ──SubmitOrder{matchId, round:N, clientActionId, order}──► cualquier réplica
  1. El hub valida esquema y tamaño ─► ticket/JWT ─► ¿es participante? ─► ¿está vivo? ─► ¿round == N?
  2. Redis: HSETNX match:{id}:r:{N}:orders {playerId} {order}
        └ idempotencia: SET NX match:{id}:act:{clientActionId}  (si ya existía → devuelve el ACK anterior)
  3. Si ya están todas las órdenes → intenta cerrar la ronda
                                        │
Temporizador (BackgroundService en CADA réplica):   │
  ZRANGEBYSCORE round-deadlines -inf now ───────────┤
                                                    ▼
CloseRound(matchId, N):
  a. Lock: SET ascua:{m:id}:lock {token} NX PX 5000      ← un solo cerrador, aunque haya varias réplicas
                                                          (por partida: también serializa drafts y abandonos)
  b. Carga la instantánea (Redis; si falta → la recupera de matches.state en Postgres)
  c. Completa las órdenes faltantes con GUARDAR
  d. Domain.Resolver.Resolve(...)  → eventos con seq = lastSeq+1...
  e. Postgres en una transacción: INSERT eventos (UNIQUE(match_id, seq)) + UPDATE match.version
        └ si choca la restricción única → otra réplica ya cerró la ronda → abortar sin efectos
  f. Redis: guardar la nueva instantánea con su versión y programar el plazo de N+1
  g. Para cada jugador: PlayerViewProjector.For(...) → Clients.User(playerId).SendAsync("RoundResolved", vista)
```

- **Orden por partida:** un único cerrador por ronda (lock en Redis más la restricción única en Postgres como garantía final). Las órdenes con `round != N` se rechazan con `STALE_ROUND`.
- **Idempotencia:** `clientActionId` (UUID v7 generado en el cliente). Un reenvío devuelve el mismo ACK y no aplica nada.
- **Durabilidad:** una ronda solo se confirma cuando sus eventos están en Postgres. Si una réplica muere entre e y f, otra reconstruye desde Postgres y el temporizador vuelve a disparar. Ninguna jugada confirmada se pierde.
- **Aislamiento:** cada `CloseRound` corre en su propio `try/catch` con un *circuit breaker* por partida. Una excepción marca la partida como `FAULTED`, la registra y avisa a sus jugadores sin afectar al resto.
- **Limpieza:** las partidas sin actividad durante 10 min o abandonadas expiran (TTL en Redis más un *job* que las marca en Postgres).
- **Reloj y azar:** los plazos se calculan en el servidor. `IRandom` usa `RandomNumberGenerator`. La semilla y el estado del generador nunca salen del servidor.

> El temporizador del servidor consulta Redis. El **cliente** nunca hace polling: recibe empujones por WebSocket.

## 4. Estado y vistas

- **Event sourcing liviano:** la tabla `match_events(match_id, seq, round, type, payload_jsonb, created_at)` guarda la verdad completa, incluida la información oculta, y **solo es accesible en el servidor**.
- **Reproducción:** `GET /matches/{id}/replay` proyecta los eventos **con la vista del usuario** que lo pide. Un administrador puede ver la vista completa, y esa acción queda auditada.
- **Mensajes compactos:** se envía un **delta** por ronda (entidades cambiadas, eventos visibles y `seq`) en MessagePack. La vista completa solo va al (re)conectar con `GetSnapshot`.
- **No filtración:** el proyector decide qué ve cada jugador. Los logs usan un `LogRedactor` y nunca serializan `GameState` completo. Hay pruebas que comparan la vista de A contra los secretos de B.

## 5. Ciclo de vida de una partida

```
Lobby ──(≥2 listos)──► Drafting ──► InProgress ──► Finished
  │                      │              │  └─► Faulted (aislada)
  └─(vacío/TTL)─► Expired               └─(todos fuera)─► Abandoned
```

- **Unión concurrente:** un script Lua en Redis (`if count < max then add`) o `UPDATE ... WHERE players < max` en Postgres, para que la capacidad se respete aunque varios se unan a la vez.
- **Emparejamiento:** cola en un *sorted set* de Redis por modo, con tiempo de espera máximo configurable. El *matcher* toma jugadores de forma atómica con Lua y marca `user:{id}:active_match` con `SET NX`, para que nadie quede asignado a dos partidas.
- **Heartbeat:** `KeepAliveInterval` de 10 s y `ClientTimeoutInterval` de 30 s. Al desconectarse se marca `disconnectedAt`; un temporizador de gracia de 60 s aplica la regla de abandono.

## 6. Seguridad

| Requisito | Implementación |
|---|---|
| Autenticación HTTP | JWT con firma ES256 cuya clave vive en Key Vault. *Refresh* rotativo guardado como hash en Postgres; si se reutiliza uno viejo, se revoca toda la familia |
| Autenticación WS | `POST /api/v1/realtime/tickets` → ticket opaco de un solo uso con 30 s de vida (`GETDEL` en Redis). Se envía en el *handshake*; si es inválido, se aborta la conexión |
| Autorización | Política por partida en cada método del hub: participante, vivo, ronda abierta. En REST, cada usuario solo accede a lo suyo; el rol `admin` va en un *claim* |
| Validación | DTOs con `[MaxLength]`/rangos y FluentValidation. `MaximumReceiveMessageSize` = 4 KB. Un mensaje inválido devuelve `Error{code}` sin cerrar la partida |
| Límites | `AddRateLimiter` por usuario (HTTP 429 con `Retry-After`), *token bucket* por conexión con un `IHubFilter` y máximo 3 conexiones por usuario (contador en Redis) |
| Transporte | Solo HTTPS/WSS (ingress de Container Apps), HSTS, CORS y orígenes WS en lista blanca, cabeceras CSP, `X-Content-Type-Options`, etc. |
| Secretos | Key Vault leído con **identidad administrada**. Postgres y Redis con **autenticación Entra** (sin contraseñas). Nada en el repositorio |
| Cadena de suministro | Dependabot, CodeQL (C#), gitleaks y Trivy sobre la imagen. Contenedor *chiseled* sin root (`USER app`) |
| Trampas | Servidor autoritativo: el cliente solo envía `{type, dir}`. Todo cálculo, azar y temporizador ocurre en el servidor |
| Modelo de amenazas | STRIDE en `docs/threat-model.md` |

## 7. Azure (topología y costo aproximado)

```
Internet ─HTTPS/WSS─► Container Apps Env ── ascua-api (2+ réplicas, revisiones múltiples)
                          │  managed identity
                          ├─► Azure Cache for Redis (Basic C0)     ~16 USD/mes
                          ├─► PostgreSQL Flexible (B1ms)           ~15 USD/mes
                          ├─► Key Vault                            <1 USD/mes
                          ├─► Container Registry (Basic)           ~5 USD/mes
                          └─► Log Analytics + App Insights          ~0-5 USD/mes (con límite)
Container Apps: plan de consumo (cuota gratuita); dev con minReplicas=0.
```

- **Total estimado: 35-45 USD/mes** con prod encendido; cabe en el crédito de Azure for Students. En dev se apaga Postgres con un script o pipeline y la app escala a 0.
- **Despliegue sin cortar partidas:** revisión nueva con un 10% del tráfico, luego 100%. Al recibir SIGTERM, la réplica deja de aceptar conexiones, envía `ServerDraining` a sus clientes y estos se reconectan a otra réplica. El estado vive en Redis y Postgres, así que la partida continúa.
- **Rollback (menos de 5 min):** `az containerapp ingress traffic set --revision-weight <anterior>=100`. Hay un *workflow* manual `rollback.yml`, documentado y probado.
- **Pipeline:** build → lint (`dotnet format --verify-no-changes`) → pruebas → validación de contratos (AsyncAPI CLI y diff de OpenAPI) → escaneos → imagen a ACR → despliegue a **dev** automático → despliegue a **prod** con aprobación del *environment* `production`.
- **Ambiente desde cero:** `infra.yml` ejecuta `az deployment sub create -f infra/main.bicep -p env=xyz`.

## 8. Observabilidad

- **Logs estructurados** con `matchId`, `playerId`, `round` y `seq` en el *scope*. Sin tokens ni información oculta.
- **Métricas propias** (`System.Diagnostics.Metrics`): `ascua.matches.active`, `ascua.connections.active`, `ascua.orders.rate`, `ascua.round.resolve.duration` y `ascua.broadcast.latency` (histograma).
- **Trazas** desde `SubmitOrder` hasta `CloseRound` y `Broadcast`, correlacionadas por partida.
- **Sondas:** `/health/live` (proceso vivo) y `/health/ready` (Redis y Postgres responden).
- **Alertas (≥2):** latencia p95 de difusión mayor a 200 ms durante 5 min; tasa de 5xx o errores del hub mayor a 1%; más de 3 reinicios de réplica en 10 min.

## 9. Rendimiento

- **Métrica objetivo:** tiempo desde que la última orden llega (o vence el plazo) hasta que **el último** jugador recibe `RoundResolved`. Meta: p95 < 200 ms.
- **Escenario:** N partidas de 4 bots. Cada bot espera un tiempo aleatorio (0-2 s) y envía una orden válida. El plazo se reduce a 3 s en la prueba para generar más carga.
- **Punto de quiebre:** subir N en una réplica (0,5 vCPU / 1 GiB) hasta que el p95 supere 200 ms o los errores pasen del 1%. Se registra CPU y memoria por partida activa.
- **Optimizaciones candidatas** (al menos una, con números de antes y después):
  1. JSON con vista completa → **MessagePack con deltas**.
  2. Reconstruir el estado desde eventos en cada ronda → **instantánea en Redis**.
  3. Un `SendAsync` por jugador → difusión en lote.

## 10. ADRs que hay que escribir

1. Turnos simultáneos con un único cerrador por ronda (lock en Redis más unicidad en Postgres).
2. SignalR solo WebSockets con MessagePack y sin *sticky sessions*.
3. Event sourcing liviano con instantánea en Redis.
4. JWT propio frente a un proveedor OIDC externo.
5. .NET 10 en lugar de .NET 8 (OpenAPI 3.1 nativo).
6. Ticket de un solo uso para el *handshake* WebSocket.
