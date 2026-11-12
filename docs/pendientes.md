# Pendientes hacia `v1.0.0`

Actualizado el **12-nov-2026**. La entrega prevista es el **12-nov-2026**; revisión indicada por Juan Pablo el **19-nov**, con disponibilidad solicitada hasta el **22-nov inclusive**. El tag `v1.0.0` sigue pendiente de los criterios de cierre.

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
- [x] **CORS HTTP:** `AddCors`/`UseCors` con la misma lista `Ascua:Cors:AllowedOrigins` que el WebSocket, sin credenciales ([seguridad-transporte.md](seguridad-transporte.md)).
- [x] **Encabezados de seguridad HTTP desde el servidor** (`Security/SecurityHeaders.cs`, probados en `SecurityHeadersTests`):
  - `Content-Security-Policy` (antes solo como `<meta>` en `index.html`);
  - `X-Content-Type-Options: nosniff`;
  - `Referrer-Policy`;
  - `frame-ancestors 'none'`;
  - HSTS fuera de Development.

  Debe incluir una prueba de integración que los verifique.
- [x] **Modelo de amenazas** ([modelo-de-amenazas.md](modelo-de-amenazas.md)): `docs/modelo-de-amenazas.md` con STRIDE por componente (REST, hub, Redis, Postgres, Key Vault). Para cada amenaza, la mitigación implementada y la prueba que la cubre. La rúbrica lo pide de forma explícita.

### Sesiones y concurrencia (20%) y pruebas (10%)
- [x] **Prueba de resiliencia por reinicio:** detener la instancia que lleva una partida a mitad de ronda y levantar otra (o una segunda `AscuaApiFactory`). Debe comprobar que la partida continúa desde Postgres/Redis sin perder jugadas confirmadas. El enunciado dice «sobrevive a reinicios». Cubierta en `ResilienceTests`; ver `docs/api/tiempo-real.md`, sección «Resiliencia».
- [x] **Prueba de resiliencia de dependencias:**
  - si Redis o Postgres se caen, `/health/ready` pasa a no saludable y el proceso no se cae;
  - al volver la dependencia, la partida sigue.
- [x] **Prueba de aislamiento:** una excepción en una partida la deja `FAULTED` sin afectar a otra partida en curso. La prueba también cubre la métrica `ascua.matches.faulted`.
- [x] **Prueba de limpieza:** una partida inactiva expira (`EXPIRED`) y libera la marca de partida activa.

### Contratos (10%)
- [x] **Nombres de héroes unificados:** REST usa `ANVIL`/`SPARK`/`RAVEN`/`ROOT` y el tiempo real usa `YUNQUE`/`CHISPA`/`CUERVO`/`RAIZ`; el cliente web traduce entre ambos.
  - Hay que elegir un solo juego de nombres.
  - Debe actualizarse a la vez en OpenAPI, AsyncAPI, el bot, la prueba de carga y el cliente.
- [x] **Sincronía OpenAPI ↔ código:** agregar una prueba o un paso de CI que compare el `openapi.json` generado con una copia versionada, para que la rúbrica («sincronizados con el código») quede demostrada.

### Documentación (5%)
- [x] **README:** URL pública de dev incorporada. Ya tiene la carpeta `loadtests/` en la estructura, los enlaces a `docs/api/`, `docs/rendimiento.md`, las ADR, el modelo de amenazas, los runbooks y esta lista, y cómo correr la prueba de carga.
- [x] **Runbooks** en `docs/runbooks/`:
  - partida `FAULTED`;
  - suspender un usuario y terminar una partida desde administración;
  - Redis o Postgres caídos;
  - rotación de la clave JWT.

  El runbook de rollback lo escribe Juan Pablo.
- [x] **Limpieza:** quitar el `TODO(Jan)` obsoleto de `src/Ascua.Bot/Program.cs`; los escenarios de NBomber ya viven en `loadtests/`.

### Los nueve héroes ([05-heroes.md](../planteamiento/05-heroes.md))
- [x] Motor, vistas, servidor, contrato 2.0.0 ([ADR-0009](adr/0009-nueve-heroes-contrato-2.md)), simulador y balance ([balance.md](balance.md)).
- [x] **Cliente web:** protocolo 2, arte, interfaz por héroe, señales, sonidos, demo, tutorial y guía.
- [x] **Prueba de carga repetida con los nueve héroes** ([rendimiento.md](rendimiento.md), §3.1).
- [ ] **Escuchar los sonidos:** nadie los ha oído; solo se comprobó que no fallan.
- [ ] **Traiciones con datos reales:** la pantalla final con traiciones solo se vio en la demo.
- [ ] **Confirmar el balance jugando:** los números se calibraron contra bots. Lo primero que se debe mirar es si el Celador (5 de vida por jugador) cae demasiado fácil entre personas.
- [ ] **Metas sin cumplir:** rondas por partida (55,9 contra 55), Hilván y Polizón bajos, Celador con 2 jugadores (27,8 %). Detalle en [balance.md](balance.md).

### Mantenimiento del repositorio
- [x] **PRs de Dependabot:** #5 cerrado (`main` ya tenía trivy-action `v0.36.0`); #4 (gitleaks-action v3), #6, #7 y #9 fusionados con el CI en verde. Los próximos los abre Dependabot solo.
- [x] **Limpieza local:** *worktrees* y ramas de PR ya fusionados borrados.

## Juan Pablo: Azure

Paso a paso en [Juan_Pablo_instrucciones.md](../Juan_Pablo_instrucciones.md).

El detalle está en [infra/README.md](../infra/README.md); la interfaz con la app, en [03-plan-de-trabajo.md](../planteamiento/03-plan-de-trabajo.md#interfaz-entre-los-dos-para-no-bloquearse).

### IaC y CI/CD (10%)
- [x] **Bicep** con todos los recursos:
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
- [x] **Publicaciones sin cortar partidas:** revisiones con división de tráfico y drenado de conexiones; **rollback probado** y documentado.
- [ ] **Evidencia** de un ambiente reconstruido desde cero con el pipeline.

### Seguridad e identidad (parte del 20%)
- [x] **Key Vault:** crear el secreto `jwt-signing-key` y dar `Key Vault Secrets User` a la identidad. El `KeyVaultSigningKeyProvider` ya está implementado y probado (Jan); solo falta configurarlo en Azure.
- [x] **Autenticación Entra** con identidad administrada en Postgres y Redis: crear los roles y permisos y poner `UseEntraAuth=true`. El código ya está (Jan); ninguna cadena lleva contraseña.
- [x] **Configuración de Azure:**
  - `Ascua__Realtime__EnableDevEndpoints=false`;
  - `Ascua__TrustForwardedHeaders=true`;
  - `Ascua__Cors__AllowedOrigins` y `Ascua__Realtime__PublicBaseUrl` con el dominio público;
  - ingress con `allowInsecure: false` (la app no redirige a HTTPS por su cuenta; ver [seguridad-transporte.md](seguridad-transporte.md));
  - Redis **sin modo clúster**.

### Observabilidad y costo (5% + parte del 10%)
- [x] **Application Insights** recibiendo trazas y las métricas `ascua.*`.
- [x] **Al menos 2 alertas:** errores HTTP >1%, partidas FAULTED y reinicios de réplica configurados.
- [ ] **Alerta de p95 >200 ms:** falta una fuente que preserve las muestras o buckets adecuados; no calcular un percentil de medias.
- [x] **Dashboard o *workbook*.**
- [x] **Límite diario de ingesta** en Log Analytics.
- [x] **`docs/costos.md`:** estimación mensual y apagado o escala a 0 de dev.

### Rendimiento en la nube (parte del 15%)
- [x] **Prueba de carga contra la URL de Azure:** correr `loadtests/run.sh` contra dev y reportar CPU y memoria por réplica desde Azure Monitor. Se agrega como sección de `docs/rendimiento.md`.

### Evidencia verificada de dev (12-nov-2026)

- [URL pública de dev](https://ca-ascua-dev.whitecliff-be2ee19c.westus2.azurecontainerapps.io/): portada y sondas HTTP 200. Disponibilidad solicitada hasta el 22-nov inclusive; producción continúa bloqueada hasta que Jan configure revisores nativos.
- [Despliegue de dev](https://github.com/JanPMolina/ascua/actions/runs/43300437654): corrección de métricas del [PR #54](https://github.com/JanPMolina/ascua/pull/54), con CI previo en verde. Application Insights recibe las métricas de partidas, conexiones, órdenes, duración de resolución y latencia de difusión.
- [Rollback probado](https://github.com/JanPMolina/ascua/actions/runs/43299999681): **53 segundos**, revisión sana y dominio público comprobados. Prueba HTTP del rollback; la continuidad de partida se comprobó por separado durante el despliegue siguiente.
- [Continuidad manual durante el despliegue automático](https://github.com/JanPMolina/ascua/actions/runs/43301113236): dos clientes reales conectados por MessagePack conservaron la misma partida, héroes y vida desde la ronda 1 hasta la 15; nueva orden con ACK ACCEPTED a las 20:36:38 UTC tras finalizar el despliegue. Es evidencia de esta sesión, no una prueba exhaustiva de ausencia de pérdidas.
- [Carga real en Azure](https://github.com/JanPMolina/ascua/actions/runs/43128625356): ejecutada con una réplica de 0,5 vCPU/1 GiB; CPU y memoria por réplica en [rendimiento.md](rendimiento.md). **No alcanzó p95 <200 ms**: 510,6 ms ya con 16 partidas. Haber ejecutado la prueba no significa que se haya cumplido la meta.
- Alertas configuradas: errores HTTP, partidas FAULTED y reinicios. El workbook muestra latencia media/máxima de los agregados; el p95 de muestras originales procede del generador de carga, no se presenta un percentil de promedios como p95 real.
- La reconstrucción íntegra desde un grupo vacío y producción con aprobación nativa siguen pendientes. El alta inicial se corrigió tras detectar restricciones regionales, concurrencia de PostgreSQL y modo Express; la app se publicó finalmente aplicando su módulo.
- [ ] Reducir la latencia de la carga de Azure hasta la meta y repetir la medición; no aumentar recursos sin recalcular el consumo dentro de USD 95.

## Los dos: entrega

- [ ] **URL pública** disponible y estable durante el periodo de revisión.
- [ ] **Ensayo de la defensa:** cada uno explica las ADR y las decisiones de su parte.
- [ ] **12-nov:**
  1. todo en verde en CI;
  2. tag `v1.0.0`;
  3. acceso de lectura al profesor;
  4. verificar la URL pública.
