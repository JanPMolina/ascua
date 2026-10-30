# Plan de trabajo: Jan y Juan Pablo, del 29-oct al 12-nov-2026

Hay **14 días**. La entrega es el **tag `v1.0.0`** el 12-nov. Recordatorios:
- Cada día de atraso resta 10%.
- **Un solo secreto en el repositorio limita la nota de seguridad al 50%.** gitleaks corre en CI desde el día 1.

## Roles

El reparto sigue el acceso a Azure: **Jan no tiene acceso a Azure**, así que Juan Pablo se encarga de todo lo que vive en la nube y Jan de todo lo demás.

| Persona | Responsabilidad | Rúbrica que cubre |
|---|---|---|
| **Juan Pablo: Azure** | Todo lo que se aprovisiona, despliega u opera en Azure | Azure/IaC/CI-CD (10%), parte de Seguridad (secretos e identidad) y de Observabilidad (alertas y dashboards) |
| **Jan: resto del proyecto** | Juego, servidor, seguridad en código, contratos, pruebas, carga, cliente, CI de build y documentación | Arquitectura (20%), Sesiones (20%), Seguridad (20%), Rendimiento (15%), Contratos y pruebas (10%), Documentación (5%) |

### Juan Pablo (Azure)
- **Bicep** (`infra/`): Container Apps Environment y app, Azure Cache for Redis, PostgreSQL Flexible Server, Key Vault, Container Registry, Log Analytics y Application Insights.
- **Identidad administrada** de la app y sus permisos: Key Vault Secrets User, AcrPull y autenticación Entra en Postgres y Redis.
- **Federación OIDC** GitHub → Azure (sin secretos en el pipeline) y los *environments* `dev` y `production` con aprobación manual.
- **Workflows de nube:**
  - `infra.yml`: levanta un ambiente desde cero en una sola ejecución.
  - `deploy.yml`: publica la imagen en ACR, despliega a dev automáticamente y a prod con aprobación.
  - `rollback.yml`: vuelve a la revisión anterior en menos de 5 minutos.
- **Despliegues sin cortar partidas:** revisiones múltiples con división de tráfico, drenado de conexiones y **rollback probado**.
- **Secretos en Key Vault:** la clave de firma JWT y cualquier otro secreto, leídos por la app con identidad administrada.
- **Monitoreo en Azure:** al menos 2 alertas, dashboard o *workbook*, límite diario de ingesta.
- **Costos:** estimación mensual, apagado o escala a 0 de dev, vigilancia del crédito de Azure for Students.
- **Pruebas de carga contra la URL de Azure:** ejecutar el bot de Jan y medir CPU y memoria por réplica en Azure Monitor.

### Jan (resto)
- **Dominio puro:** reglas de Ascua, resolución, vistas por jugador y pruebas unitarias y de propiedades.
- **Servidor:** REST (usuarios, salas, emparejamiento, historial, administración), hub de SignalR, rondas, temporizadores, reconexión, Redis y Postgres (esquema y migraciones).
- **Seguridad en código:** Argon2id, JWT con *refresh* rotativo, ticket WS, autorización por partida, validación, límites de tasa, CORS y cabeceras, y modelo de amenazas.
- **Contratos:** AsyncAPI 3.0, OpenAPI 3.1 en `/docs`, Problem Details.
- **CI de calidad** (`ci.yml`): build, formato, pruebas, validación de contratos, CodeQL, gitleaks, Dependabot y Trivy.
- **Pruebas:** integración, concurrencia, seguridad y resiliencia (Testcontainers), más el bot y la prueba de carga **local** con su optimización medida.
- **Instrumentación OpenTelemetry en el código:** métricas propias, logs estructurados y sondas de salud.
- **Cliente web**, Dockerfile, `docker compose`, README, ADRs y runbooks.

### Interfaz entre los dos (para no bloquearse)
Jan entrega y Juan Pablo consume:
- La **imagen** construida desde `Dockerfile`, que escucha en el puerto `8080` y corre sin root.
- Las **sondas** `/health/live` y `/health/ready`.
- La **configuración por variables de entorno** (`appsettings` → `Ascua__*`):

| Clave | Local (compose) | Azure (lo define Juan Pablo) |
|---|---|---|
| `ConnectionStrings__Postgres` | cadena con contraseña de desarrollo | host y base sin contraseña (token Entra con identidad administrada) |
| `ConnectionStrings__Redis` | `redis:6379` | host:6380 con SSL y token Entra. **Sin modo clúster** (los scripts Lua del lobby tocan varias claves, ADR-0007) |
| `Ascua__KeyVaultUri` | vacío (usa una clave de desarrollo generada al arrancar) | URI del Key Vault. Falta implementar `ISigningKeyProvider` para Key Vault (ver `src/Ascua.Infrastructure/Auth/SigningKeys.cs`); hasta entonces la app no arranca si se define |
| `Ascua__Database__MigrateOnStartup` | `true` (aplica migraciones al arrancar) | `false` por defecto; `true` es seguro con varias réplicas (EF Core toma un *lock* al migrar) o se migra desde el pipeline |
| `Ascua__Jwt__AllowEphemeralKey` | no hace falta (Development siempre usa clave efímera) | `false`; `true` solo en un ambiente de prueba de una réplica mientras no esté Key Vault |
| `Ascua__Jwt__Issuer` / `Ascua__Jwt__Audience` | `ascua` / `ascua-api` | igual (o el dominio público) |
| `Ascua__Jwt__AccessTokenLifetime` / `Ascua__Jwt__RefreshTokenLifetime` | `00:10:00` / `7.00:00:00` | igual |
| `Ascua__RateLimiting__Auth__PermitLimit` / `__Window` | `10` por `00:01:00` (por IP, endpoints de `/api/v1/auth`) | igual |
| `Ascua__RateLimiting__User__PermitLimit` / `__Window` | `60` por `00:01:00` (por usuario) | igual |
| `Ascua__Lobby__RoomTtl` | `01:00:00` (una sala sin cambios se borra sola) | igual |
| `Ascua__Lobby__ActiveMatchTtl` | `04:00:00` (tope de la marca "partida activa" si el tiempo real no la libera) | igual |
| `Ascua__Matchmaking__MaxWait` | `00:00:30` (pasado esto, la cola arma partidas de 2-3 en lugar de esperar a 4) | igual |
| `Ascua__Matchmaking__QueueTimeout` | `00:05:00` (quien espera más sin rivales sale de la cola) | igual |
| `Ascua__Matchmaking__PollInterval` | `00:00:01` (cada cuánto el agrupador procesa la cola) | igual |
| `Ascua__Matchmaking__Enabled` | `true` | `true` (el agrupador es seguro con varias réplicas; `false` lo apaga en una réplica) |
| `Ascua__TrustForwardedHeaders` | `false` | `true` (detrás del ingress de Container Apps, para que el *rate limiting* vea la IP real) |
| `Ascua__Cors__AllowedOrigins` | `http://localhost:8080` | dominio público |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | vacío (exporter apagado) | la del recurso (no es un secreto según Microsoft, pero puede ir en Key Vault) |

Si una clave cambia, se actualiza esta tabla en el mismo PR.

## Cronograma

### Fase 0: Cimientos (29-30 oct)
- [x] Jan: repositorio en GitHub, esqueleto de la solución, `docker compose`, CI base (build, test, format, CodeQL, gitleaks, Dependabot).
- [ ] Jan y Juan Pablo: congelar las reglas v1 ([01-juego.md](01-juego.md)) y escribir las ADR 1-3.
- [ ] Juan Pablo: grupo de recursos, federación OIDC y *environments* de GitHub (`dev`, `production`).

### Fase 1: Núcleo (31 oct-3 nov)
- [ ] Jan: `Resolver` completo y `PlayerViewProjector` con pruebas de no filtración.
- [ ] Jan: usuarios (Argon2id, JWT, *refresh* rotativo) y salas por REST con Problem Details; OpenAPI 3.1 en `/docs`.
- [ ] Juan Pablo: Bicep de todos los recursos y `infra.yml` levantando **dev** desde cero.
- [ ] Juan Pablo: `deploy.yml` desplegando la imagen del esqueleto a dev, con las sondas en verde.

### Fase 2: Tiempo real y nube (4-6 nov)
- [ ] Jan: `MatchHub` (ticket, `SubmitOrder` idempotente, `CloseRound` con lock, temporizadores, vistas por jugador), reconexión y backplane de Redis.
- [ ] Jan: `asyncapi.yaml` v1 completo y métricas OpenTelemetry propias.
- [ ] Juan Pablo: Key Vault con la clave de firma JWT, autenticación Entra en Postgres y Redis desde la app, Application Insights recibiendo trazas.
- 🎯 **Hito (6-nov):** una partida de 2 jugadores completa en la URL de dev.

### Fase 3: Completar requisitos (7-9 nov)
- [ ] Jan: emparejamiento, historial, reproducción y administración con auditoría.
- [ ] Jan: límites de tasa, CORS y cabeceras, pruebas de concurrencia, seguridad y resiliencia.
- [ ] Juan Pablo: ambiente **prod** con aprobación manual, revisiones con división de tráfico, drenado y `rollback.yml` **probado y documentado**.
- [ ] Juan Pablo: alertas (p95 > 200 ms, errores > 1%, reinicios) y dashboard.

### Fase 4: Rendimiento y cierre (10-11 nov)
- [ ] Jan: bot con NBomber, prueba de carga local, punto de quiebre y optimización con números de antes y después.
- [ ] Jan: cliente web mínimo, modelo de amenazas y README.
- [ ] Juan Pablo: correr la carga contra Azure, reportar CPU y memoria por partida y por réplica, estimación de costos, y evidencia de un ambiente reconstruido desde cero.
- [ ] Jan y Juan Pablo: ensayo de la defensa.

### 12-nov: Entrega
- [ ] Todo en verde en CI → tag `v1.0.0` → dar acceso de lectura al profesor → verificar la URL pública.

## Recortes si el tiempo no alcanza (en este orden)

1. Reducir bendiciones a 6 y eliminar las sinergias.
2. Quitar el sigilo de Cuervo.
3. Dejar 3 salas en lugar de 5.
4. Hacer el cliente web solo de texto o tabla.

**Nunca se recortan:** seguridad, durabilidad, idempotencia, pruebas en CI, contratos y despliegue.

## Riesgos

| Riesgo | Mitigación |
|---|---|
| **Desbalance de carga:** Jan carga ~80% del código | Juan Pablo puede apoyar con pruebas de carga, documentación de despliegue y revisión de PRs. Si Azure queda listo antes, toma tareas de Jan que no requieran nube, por ejemplo el cliente web |
| Que el crédito de Azure se agote | Dev apagado o a 0 réplicas fuera de horario, límite de ingesta en Log Analytics |
| Que Jan no pueda probar en Azure | Todo debe funcionar igual con `docker compose`. Juan Pablo comparte logs y trazas de Azure cuando haga falta |
| Lock y réplicas en casos raros | `UNIQUE(match_id, seq)` en Postgres como garantía final; pruebas con 2 instancias en Testcontainers |
| Autenticación Entra con Postgres y Redis | Juan Pablo reserva medio día; Jan deja la conexión preparada con `DefaultAzureCredential` |
| Que las reglas del juego crezcan | Congelar las reglas el 30-oct |
