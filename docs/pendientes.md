# Pendientes hacia `v1.0.0`

Actualizado el **30-oct-2026** con `main` en `b26abda`. La entrega es el **12-nov-2026**: ese día se pone el tag `v1.0.0` y el profesor evalúa esa versión.

Esta lista se contrastó con el enunciado ([Proyecto3.pdf](../Proyecto3.pdf)) y su rúbrica. Cada pendiente indica qué criterio afecta.
- **Al cerrar un pendiente:** se marca `[x]` en el mismo PR que lo resuelve.
- **Al descubrir uno nuevo:** se agrega aquí.

## Lo que ya está hecho

En `main`, con CI, CodeQL, gitleaks y Trivy en verde, y verificado con `docker compose`:
- **Dominio puro:** reglas de Ascua y vistas por jugador.
- **Usuarios:** Argon2id, JWT ES256 y *refresh* rotativo.
- **Salas y emparejamiento** (ADR-0007).
- **Tiempo real:** hub de SignalR con ticket de un solo uso, idempotencia, cerrador único, temporizadores, reconexión y abandono.
- **Historial** con reproducción.
- **Administración** con auditoría.
- **Cliente web** de prueba.
- **Bot** y **prueba de carga** con NBomber: punto de quiebre de ~112 partidas por réplica de 0,5 vCPU y dos optimizaciones medidas ([rendimiento.md](rendimiento.md)).

## Jan: código, pruebas y documentación

### Seguridad (20%)
- [ ] **CORS HTTP:** hoy solo se restringen los orígenes del WebSocket (`TicketAuthentication`). Falta `AddCors`/`UseCors` con `Ascua:Cors:AllowedOrigins` para la API REST.
- [ ] **Encabezados de seguridad HTTP desde el servidor:**
  - `Content-Security-Policy` (hoy solo existe como `<meta>` en `index.html`);
  - `X-Content-Type-Options: nosniff`;
  - `Referrer-Policy`;
  - `frame-ancestors 'none'`;
  - HSTS fuera de Development.

  Debe incluir una prueba de integración que los verifique.
- [x] **Modelo de amenazas** ([modelo-de-amenazas.md](modelo-de-amenazas.md)): `docs/modelo-de-amenazas.md` con STRIDE por componente (REST, hub, Redis, Postgres, Key Vault). Para cada amenaza, la mitigación implementada y la prueba que la cubre. La rúbrica lo pide de forma explícita.

### Sesiones y concurrencia (20%) y pruebas (10%)
- [ ] **Prueba de resiliencia por reinicio:** detener la instancia que lleva una partida a mitad de ronda y levantar otra (o una segunda `AscuaApiFactory`). Debe comprobar que la partida continúa desde Postgres/Redis sin perder jugadas confirmadas. El enunciado dice «sobrevive a reinicios» y hoy no hay prueba que lo demuestre.
- [ ] **Prueba de resiliencia de dependencias:**
  - si Redis o Postgres se caen, `/health/ready` pasa a no saludable y el proceso no se cae;
  - al volver la dependencia, la partida sigue.
- [ ] **Prueba de aislamiento:** una excepción en una partida la deja `FAULTED` sin afectar a otra partida en curso. La métrica `ascua.matches.faulted` ya existe, pero ninguna prueba lo cubre.

### Contratos (10%)
- [ ] **Nombres de héroes unificados:** REST usa `ANVIL`/`SPARK`/`RAVEN`/`ROOT` y el tiempo real usa `YUNQUE`/`CHISPA`/`CUERVO`/`RAIZ`; el cliente web traduce entre ambos.
  - Hay que elegir un solo juego de nombres.
  - Debe actualizarse a la vez en OpenAPI, AsyncAPI, el bot, la prueba de carga y el cliente.
- [ ] **Sincronía OpenAPI ↔ código:** agregar una prueba o un paso de CI que compare el `openapi.json` generado con una copia versionada, para que la rúbrica («sincronizados con el código») quede demostrada.

### Documentación (5%)
- [ ] **README:**
  - URL pública (cuando Juan Pablo la tenga);
  - carpeta `loadtests/` en la estructura;
  - enlaces a `docs/api/`, `docs/rendimiento.md`, las ADR, el modelo de amenazas y esta lista;
  - cómo correr la prueba de carga.
- [ ] **Runbooks** en `docs/runbooks/`:
  - partida `FAULTED`;
  - suspender un usuario y terminar una partida desde administración;
  - Redis o Postgres caídos;
  - rotación de la clave JWT.

  El runbook de rollback lo escribe Juan Pablo.
- [ ] **Limpieza:** quitar el `TODO(Jan)` obsoleto de `src/Ascua.Bot/Program.cs`; los escenarios de NBomber ya viven en `loadtests/`.

### Mantenimiento del repositorio
- [ ] **PRs de Dependabot:**
  - #5 (trivy-action 0.36.0): **cerrar**, porque `main` ya usa `v0.36.0`.
  - #4 (gitleaks-action v3): revisar que el job `secrets-scan` siga pasando antes de fusionar.
  - #6, #7 y #9 (Azure Monitor, Test SDK, xUnit runner): fusionar si el CI pasa.
- [ ] **Limpieza local:** borrar los *worktrees* viejos en `.worktrees/` y archivar las sesiones de Claude cuyos PR ya se fusionaron.

## Juan Pablo: Azure

El detalle está en [infra/README.md](../infra/README.md); la interfaz con la app, en [03-plan-de-trabajo.md](../planteamiento/03-plan-de-trabajo.md#interfaz-entre-los-dos-para-no-bloquearse).

### IaC y CI/CD (10%)
- [ ] **Bicep** con todos los recursos:
  - Container Apps;
  - Redis;
  - PostgreSQL Flexible Server;
  - Key Vault;
  - ACR;
  - Log Analytics y Application Insights;
  - identidad administrada con sus permisos.
- [ ] **Federación OIDC** GitHub → Azure y *environments* `dev` y `production` (con aprobación manual).
- [ ] **Workflows:**
  - `infra.yml`: ambiente desde cero en una ejecución;
  - `deploy.yml`: imagen a ACR, dev automático y prod con aprobación;
  - `rollback.yml`: menos de 5 minutos.
- [ ] **Publicaciones sin cortar partidas:** revisiones con división de tráfico y drenado de conexiones; **rollback probado** y documentado.
- [ ] **Evidencia** de un ambiente reconstruido desde cero con el pipeline.

### Seguridad e identidad (parte del 20%)
- [ ] **`KeyVaultSigningKeyProvider`** con `DefaultAzureCredential` (`TODO(Juan Pablo)` en `src/Ascua.Infrastructure/DependencyInjection.cs`). Hasta entonces la app no arranca si se define `Ascua__KeyVaultUri`.
- [ ] **Autenticación Entra** con identidad administrada en Postgres y Redis (`TODO(Juan Pablo)` en `src/Ascua.Infrastructure/Redis/RedisConnection.cs`): ninguna cadena con contraseña en la configuración.
- [ ] **Configuración de Azure:**
  - `Ascua__Realtime__EnableDevEndpoints=false`;
  - `Ascua__TrustForwardedHeaders=true`;
  - `Ascua__Cors__AllowedOrigins` y `Ascua__Realtime__PublicBaseUrl` con el dominio público;
  - Redis **sin modo clúster**.

### Observabilidad y costo (5% + parte del 10%)
- [ ] **Application Insights** recibiendo trazas y las métricas `ascua.*`.
- [ ] **Al menos 2 alertas:** p95 > 200 ms, errores > 1% y reinicios de réplica.
- [ ] **Dashboard o *workbook*.**
- [ ] **Límite diario de ingesta** en Log Analytics.
- [ ] **`docs/costos.md`:** estimación mensual y apagado o escala a 0 de dev.

### Rendimiento en la nube (parte del 15%)
- [ ] **Prueba de carga contra la URL de Azure:** correr `loadtests/run.sh` contra dev y reportar CPU y memoria por réplica desde Azure Monitor. Se agrega como sección de `docs/rendimiento.md`.

## Los dos: entrega

- [ ] **URL pública** disponible y estable durante el periodo de revisión.
- [ ] **Ensayo de la defensa:** cada uno explica las ADR y las decisiones de su parte.
- [ ] **12-nov:**
  1. todo en verde en CI;
  2. tag `v1.0.0`;
  3. acceso de lectura al profesor;
  4. verificar la URL pública.
