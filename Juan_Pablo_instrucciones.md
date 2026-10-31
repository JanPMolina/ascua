# Instrucciones para Juan Pablo: despliegue en Azure

Hola, Juan Pablo. Este documento junta todo lo que necesitas para llevar Ascua a Azure. La app ya está lista del lado del código: solo hay que **aprovisionar, configurar y automatizar**. No hace falta tocar C#.

> **Entrega: 12-nov-2026.** Ese día se pone el tag `v1.0.0` y el profesor revisa esa versión **y la URL pública**. Cada día de atraso resta 10%.
> **Un solo secreto en el repositorio limita la nota de seguridad al 50%.** Nada de contraseñas, claves ni cadenas con `Password=` en archivos; gitleaks corre en cada PR.

**Fechas que conviene cumplir** (del [plan de trabajo](planteamiento/03-plan-de-trabajo.md#cronograma)):

| Fecha | Hito |
|---|---|
| 3-nov | dev levantado desde cero con `infra.yml` y la app desplegada con las sondas en verde |
| 6-nov | Key Vault, Entra en Postgres y Redis, y Application Insights funcionando; partida completa en la URL de dev |
| 9-nov | prod con aprobación manual, rollback probado, alertas y dashboard |
| 11-nov | prueba de carga contra Azure, costos y evidencia de reconstrucción desde cero |

## Índice

1. [Qué te toca y qué no](#1-qué-te-toca-y-qué-no)
2. [Cómo trabajamos los dos en paralelo](#2-cómo-trabajamos-los-dos-en-paralelo)
3. [Lo que la app espera de Azure](#3-lo-que-la-app-espera-de-azure)
4. [Paso a paso](#4-paso-a-paso)
5. [Checklist para la rúbrica](#5-checklist-para-la-rúbrica)
6. [Si algo falla](#6-si-algo-falla)

---

## 1. Qué te toca y qué no

**Tuyo** (rúbrica: *Despliegue en Azure, IaC y CI/CD* 10%, más partes de *Seguridad* y *Observabilidad*):

| Área | Entregable |
|---|---|
| IaC | `infra/main.bicep` + módulos + `infra/parameters/dev.bicepparam` y `prod.bicepparam` |
| CI/CD | `.github/workflows/infra.yml`, `deploy.yml`, `rollback.yml` |
| Identidad | Identidad administrada de la app con sus permisos; OIDC GitHub → Azure (sin secretos en GitHub) |
| Secretos | Key Vault con la clave de firma JWT |
| Despliegues | Revisiones con división de tráfico, drenado, rollback **probado** en menos de 5 min |
| Observabilidad | Application Insights, ≥2 alertas, dashboard o *workbook*, límite de ingesta |
| Costos | `docs/costos.md` con la estimación mensual; dev apagado o escalado a 0 cuando no se usa |
| Rendimiento en la nube | Correr la prueba de carga contra dev y reportar CPU y memoria por réplica |
| Documentación | `docs/runbooks/rollback.md`, la URL pública en el `README.md` |

**No es tuyo** (lo hace Jan): todo lo que está en `src/`, `tests/`, `contracts/`, `loadtests/` y el cliente web. Si para Azure necesitas un cambio en el código, abre un *issue* o avísale: no lo cambies tú.

## 2. Cómo trabajamos los dos en paralelo

Jan va a seguir puliendo el juego (animaciones, sonido, interfaz) mientras tú montas Azure. Para no pisarnos:

- **Ramas:** nunca se trabaja directo en `main`. Crea ramas con prefijo `infra/` (por ejemplo `infra/bicep-base`, `infra/deploy`) y abre un PR a `main`. Jan usa `feat/`, `fix/` y `docs/`.
- **Archivos de cada uno:** tú solo tocas `infra/`, `.github/workflows/infra.yml`, `deploy.yml`, `rollback.yml`, `docs/costos.md` y `docs/runbooks/rollback.md`. Si necesitas cambiar otro archivo (`README.md`, `ci.yml`, la tabla de variables), hazlo en un PR chico y avisa. El archivo `.github/CODEOWNERS` lo deja asignado automáticamente.
- **PRs:** el CI (build, pruebas, contratos, gitleaks, Trivy, CodeQL) tiene que estar en verde antes de fusionar. Squash merge. Si el PR solo toca tus archivos, puedes fusionarlo tú.
- **Después de que todo esté desplegado:** cada PR que Jan fusione a `main` se despliega **solo a dev** con tu `deploy.yml`. Prod se actualiza con aprobación manual (tú o Jan aprueban en GitHub). Así Jan puede seguir mejorando el juego sin depender de ti, y nada llega a prod sin que alguien lo apruebe.
- **Variables nuevas:** si Jan agrega una variable de entorno, la anota en la tabla de [03-plan-de-trabajo.md](planteamiento/03-plan-de-trabajo.md#interfaz-entre-los-dos-para-no-bloquearse) en el mismo PR. Revisa esa tabla antes de cada despliegue a prod.

## 3. Lo que la app espera de Azure

### Imagen
- Se construye desde el `Dockerfile` de la raíz: `docker build -t ascua-api .`.
- La imagen es *chiseled* y corre **sin root** (UID 1654). No tiene shell, así que `az containerapp exec` no sirve; usa los logs.
- Escucha en el puerto **8080** por HTTP. El TLS lo termina el ingress de Container Apps.
- Sondas:

  | Sonda | Ruta | Qué indica |
  |---|---|---|
  | **liveness** | `/health/live` | El proceso responde. No reinicies la réplica por fallas de Postgres o Redis |
  | **readiness** | `/health/ready` | Postgres y Redis responden. Si falla, el ingress deja de mandarle tráfico |
  | **startup** | `/health/live` | Aplica las migraciones al arrancar; dale ~60 s |

### Recursos (topología del enunciado)

| Recurso | Notas |
|---|---|
| **Container Apps Environment + Container App** | Ingress externo, `transport: auto` (WebSockets funciona sin configurar nada), `allowInsecure: false`. **0,5 vCPU / 1 GiB** por réplica (así se midió el punto de quiebre: ~112 partidas por réplica, ver [docs/rendimiento.md](docs/rendimiento.md)). Prod: mín. 2 réplicas para que un despliegue no corte partidas; dev: mín. 0. Modo de revisiones **múltiple**. `terminationGracePeriodSeconds: 30` |
| **Azure Cache for Redis** (Basic C0 en dev, Standard C0/C1 en prod) | **Sin modo clúster**: los scripts Lua del lobby tocan varias claves (ADR-0007). Activar *Microsoft Entra Authentication* y deshabilitar el *access key auth* cuando la app ya conecte con identidad. TLS 6380 |
| **PostgreSQL Flexible Server** (Burstable B1ms) | Versión 17. **Solo autenticación Entra** (o Entra + Postgres mientras pruebas). Base `ascua`. Permitir acceso desde el Environment (VNet o, más simple, «Allow Azure services») |
| **Key Vault** | Modelo de permisos **RBAC**. Soft delete activado |
| **Container Registry** (Basic) | Sin usuario admin: la app hace *pull* con la identidad administrada (`AcrPull`) |
| **Log Analytics + Application Insights** | Application Insights en modo *workspace-based*. **Límite diario de ingesta** en Log Analytics (por ejemplo 0,5 GB/día) |
| **Identidad administrada asignada por el usuario** (`id-ascua-<env>`) | La usa la app para todo: ACR, Key Vault, Postgres y Redis |

### Permisos de la identidad administrada

| Recurso | Rol |
|---|---|
| Container Registry | `AcrPull` |
| Key Vault | `Key Vault Secrets User` (solo lectura de secretos) |
| PostgreSQL | Rol de Postgres con el **nombre de la identidad** (ver paso 4.4) |
| Redis | *Data access policy* `Data Owner` (o `Data Contributor`) para la identidad |

### Variables de entorno de la Container App

La lista completa está en la tabla [«Interfaz entre los dos»](planteamiento/03-plan-de-trabajo.md#interfaz-entre-los-dos-para-no-bloquearse). Las que **debes** poner en Azure:

| Variable | Valor en Azure |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` (o `dev` en el ambiente dev: habilita JSON en el hub para depurar) |
| `Ascua__Azure__ManagedIdentityClientId` | *client id* de `id-ascua-<env>` |
| `ConnectionStrings__Postgres` | `Host=<servidor>.postgres.database.azure.com;Database=ascua;Username=id-ascua-<env>;SSL Mode=Require` (**sin `Password`**) |
| `Ascua__Database__UseEntraAuth` | `true` |
| `Ascua__Database__MigrateOnStartup` | `true` (EF Core toma un *lock*: es seguro con varias réplicas) |
| `ConnectionStrings__Redis` | `<nombre>.redis.cache.windows.net:6380,ssl=True,abortConnect=False` (**sin `password`**) |
| `Ascua__Redis__UseEntraAuth` | `true` |
| `Ascua__KeyVaultUri` | `https://<kv>.vault.azure.net/` |
| `Ascua__Jwt__KeyVaultSecretName` | `jwt-signing-key` (es el valor por defecto; ponlo solo si usas otro nombre) |
| `Ascua__TrustForwardedHeaders` | `true` (el rate limiting necesita la IP real detrás del ingress) |
| `Ascua__Cors__AllowedOrigins__0` | `https://<fqdn de la app>` (el mismo origen sirve el cliente web) |
| `Ascua__Realtime__PublicBaseUrl` | `https://<fqdn de la app>` |
| `Ascua__Realtime__EnableDevEndpoints` | `false` (**nunca** `true` en prod; solo mientras corres la prueba de carga en dev) |
| `Ascua__Admin__BootstrapUsername` | Tu usuario o el de Jan, **ya registrado** en la app. Se promueve a admin al arrancar y queda auditado. Se puede quitar después |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | La del recurso. No es un secreto según Microsoft, pero si prefieres, guárdala en Key Vault con una referencia de secreto de Container Apps |

**Qué hace la app con esas variables** (ya está implementado y probado en `tests/Ascua.Integration.Tests/AzureReadinessTests.cs`):

| Variable | Comportamiento |
|---|---|
| `Ascua__KeyVaultUri` | Lee la clave JWT del secreto con la identidad. Firma con la versión más reciente, valida también con las versiones viejas **habilitadas** y relee Key Vault cada 5 min. Si al arrancar no puede leer Key Vault, **no arranca**: es a propósito, para no emitir tokens que las otras réplicas no aceptan |
| `UseEntraAuth=true` | Pide y renueva sola los tokens de Entra para Postgres y Redis. Si la cadena trae contraseña, la app falla al arrancar con un mensaje claro: así no se cuela un secreto en la configuración |

## 4. Paso a paso

### 4.1 Preparar la suscripción (una vez, a mano)

```bash
az login
az account set --subscription "<Azure for Students>"
az provider register --namespace Microsoft.App
az provider register --namespace Microsoft.OperationalInsights
az provider register --namespace Microsoft.Cache
az provider register --namespace Microsoft.DBforPostgreSQL
```

Elige una región con todo disponible y barata, por ejemplo `eastus2`. Sigue la convención de nombres `rg-ascua-dev`, `ca-ascua-dev`, `kv-ascua-dev-<sufijo>` y así con el resto.

### 4.2 OIDC: GitHub Actions entra a Azure sin secretos

1. Crea una *App registration* (o una identidad administrada) para GitHub. Agrégale **credenciales federadas** para:
   - `repo:JanPMolina/ascua:environment:dev`;
   - `repo:JanPMolina/ascua:environment:production`;
   - `repo:JanPMolina/ascua:ref:refs/heads/main`, para `infra.yml`.
2. Dale `Contributor` en la suscripción (o en los grupos de recursos) y `User Access Administrator` acotado a los grupos de recursos, para que Bicep pueda asignar roles.
3. En GitHub → *Settings* → *Environments*, crea `dev` y `production`. En `production` activa **Required reviewers** (tú y Jan).
4. Guarda como **variables**, no secretos, de cada environment: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`. No son secretos y así gitleaks no se queja.
5. En los workflows: `permissions: id-token: write, contents: read` y `azure/login@v2` con esos tres valores.

### 4.3 Bicep (`infra/`)

Estructura sugerida:

```
infra/
  main.bicep                 # alcance de suscripción: crea el RG y llama a los módulos
  modules/
    identity.bicep           # identidad administrada asignada por el usuario
    monitoring.bicep         # Log Analytics (+ dailyQuotaGb) + Application Insights
    registry.bicep           # ACR + AcrPull para la identidad
    keyvault.bicep           # Key Vault RBAC + Key Vault Secrets User para la identidad
    redis.bicep              # Redis sin clúster + Entra + access policy para la identidad
    postgres.bicep           # Flexible Server + Entra admin + base ascua + firewall
    containerapp.bicep       # Environment + App (ingress, sondas, escalado, variables, identidad)
    alerts.bicep             # alertas + action group (tu correo)
  parameters/
    dev.bicepparam
    prod.bicepparam
```

Detalles que suelen fallar:
- **Imagen inicial:** la Container App necesita una imagen al crearla. Usa un parámetro `image` con valor por defecto `mcr.microsoft.com/k8se/quickstart:latest` para la primera vez; luego `deploy.yml` la reemplaza.
- **Ingress:**
  - `targetPort: 8080`, `transport: 'auto'`, `allowInsecure: false`;
  - `stickySessions` **no** hace falta: el cliente entra por WebSocket directo, sin *negotiate*, y el estado vive en Redis.
- **Escalado:** regla HTTP con unas 100 solicitudes concurrentes. Prod con `minReplicas: 2`, `maxReplicas: 4`; dev con `minReplicas: 0`, `maxReplicas: 2`.
- **`activeRevisionsMode: 'Multiple'`** para poder dividir el tráfico entre revisiones.
- **Validación local:** antes de abrir el PR corre `az bicep build --file infra/main.bicep` y `az deployment sub what-if ...`.

### 4.4 Postgres con Entra (una vez por ambiente)

Después de que Bicep cree el servidor con tu cuenta como administrador Entra:

```bash
# Conéctate como admin Entra (token como contraseña) y crea el rol de la identidad de la app.
export PGPASSWORD=$(az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv)
psql "host=<servidor>.postgres.database.azure.com dbname=postgres user=<tu-correo> sslmode=require" \
  -c "select * from pgaadauth_create_principal('id-ascua-dev', false, false);"
psql "host=<servidor>.postgres.database.azure.com dbname=ascua user=<tu-correo> sslmode=require" \
  -c "grant all on schema public to \"id-ascua-dev\"; alter database ascua owner to \"id-ascua-dev\";"
```

La app crea las tablas sola con `MigrateOnStartup=true`.

### 4.5 La clave JWT en Key Vault

La clave es ECDSA **P-256** (ES256) en PEM PKCS#8, guardada como **secreto** con el nombre `jwt-signing-key`. Se genera en tu máquina y se sube sin pasar por el repo ni por los logs del pipeline:

```bash
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out jwt.pem
az keyvault secret set --vault-name <kv> --name jwt-signing-key --file jwt.pem --content-type application/x-pem-file
rm jwt.pem
```

- Para subir el secreto necesitas el rol `Key Vault Secrets Officer` sobre el vault (la app solo tiene *User*).
- La rotación (programada y urgente) está en [docs/runbooks/rotar-clave-jwt.md](docs/runbooks/rotar-clave-jwt.md). En resumen: subir una versión nueva, esperar 10 min y deshabilitar la vieja. La app la toma sola en ≤5 min.
- Hay una prueba que lo cubre: `A_disabled_old_version_stops_validating_its_tokens`.

### 4.6 Workflows

| Workflow | Disparador | Qué hace |
|---|---|---|
| `infra.yml` | `workflow_dispatch` con input `environment` (dev/prod) | `azure/login` → `az deployment sub create -f infra/main.bicep -p infra/parameters/<env>.bicepparam`. **Un ambiente nuevo debe salir de una sola ejecución** (es criterio de la rúbrica) |
| `deploy.yml` | `push` a `main` (dev) y `workflow_dispatch`/tag `v*` (prod, environment `production` con aprobación) | 1) `docker build` + Trivy, igual que el CI; 2) `az acr login` + `docker push <acr>/ascua-api:<sha>`; 3) dev: `az containerapp update --image ...`; 4) prod: revisión nueva **sin tráfico**, probar `/health/ready` en su URL de revisión y pasarle el tráfico (10% → 100%), desactivando la vieja al final |
| `rollback.yml` | `workflow_dispatch` con input `revision` (opcional; por defecto la anterior) | `az containerapp ingress traffic set --revision-weight <anterior>=100`. Debe tardar **menos de 5 min**: mídelo y anótalo |

**Despliegues sin cortar partidas:**
- Al bajar una revisión, la app recibe SIGTERM y **avisa `ServerDraining`** a sus conexiones (`RealtimeDrainService`).
- Los clientes se reconectan a otra réplica y recuperan la partida desde Redis y Postgres.
- Por eso prod necesita **≥2 réplicas**, y conviene mover el tráfico de forma gradual.
- **Para la evidencia:** juega una partida mientras despliegas y verifica que siga sin perder jugadas. El bot sirve: `dotnet run --project src/Ascua.Bot -- https://<fqdn> 4 500`, con `EnableDevEndpoints=true` solo en dev.

### 4.7 Observabilidad

- La app ya exporta trazas, logs y métricas a Application Insights apenas hay `APPLICATIONINSIGHTS_CONNECTION_STRING`.
- Los logs del motor llevan `MatchId`, `PlayerId` y `Round`.
- Métricas propias (*meter* `Ascua`):

  | Métrica | Qué mide |
  |---|---|
  | `ascua.matches.active` | Partidas activas |
  | `ascua.connections.active` | Conexiones abiertas |
  | `ascua.orders.rate` | Jugadas aceptadas |
  | `ascua.round.resolve.duration` | Duración del cierre de ronda |
  | `ascua.broadcast.latency` | Latencia de difusión |
  | `ascua.matches.faulted` | Partidas aisladas como `FAULTED` |

- Alertas mínimas (la rúbrica pide ≥2):
  1. p95 de `ascua.broadcast.latency` > 200 ms durante 5 min;
  2. tasa de solicitudes fallidas > 1% durante 5 min;
  3. reinicios de réplica (métrica `RestartCount` de Container Apps) > 0;
  4. opcional: `ascua.matches.faulted` > 0.
- Dashboard o *workbook* con partidas activas, conexiones, jugadas/s, p95 de latencia, CPU y memoria por réplica.
- Qué hacer con cada alerta: ver [docs/runbooks/](docs/runbooks/README.md).

### 4.8 Prueba de carga contra Azure

Con dev desplegado, ajusta **solo mientras dura la prueba**:
- `EnableDevEndpoints=true`;
- `RoundDuration=00:00:03`;
- `RateLimiting__Auth__PermitLimit=100000`;
- `minReplicas = maxReplicas = 1`.

Después corre:

```bash
loadtests/run.sh --url https://<fqdn-dev> --clock estimate --stages 16,32,64,96,112 --stage-seconds 120 --label azure
```

- Anota CPU y memoria por réplica en Azure Monitor y agrégalos como sección «Azure» en [docs/rendimiento.md](docs/rendimiento.md). Detalles en [loadtests/README.md](loadtests/README.md).
- **Al terminar, vuelve todo a sus valores.**

### 4.9 Costos

Crea `docs/costos.md` con:
- la estimación mensual por recurso (calculadora de Azure) para dev y prod;
- cómo se apaga dev: `minReplicas: 0`, Postgres detenido con `az postgres flexible-server stop` y Redis eliminado o en Basic;
- el crédito restante de Azure for Students.

Pon un **presupuesto con alerta** en la suscripción.

### 4.10 Cerrar

- [ ] La URL pública en el `README.md`, en la línea «URL pública: _pendiente_».
- [ ] `docs/runbooks/rollback.md` con los pasos y el tiempo medido.
- [ ] Evidencia de «ambiente desde cero»: captura o enlace de una ejecución de `infra.yml` + `deploy.yml` sobre un grupo de recursos vacío.
- [ ] Marcar tus casillas en [docs/pendientes.md](docs/pendientes.md) y en [infra/README.md](infra/README.md).

## 5. Checklist para la rúbrica

Esto es lo que el profesor va a buscar en tu parte:

| Requisito | Dónde se demuestra |
|---|---|
| Todos los recursos en Bicep; un ambiente nuevo en una sola ejecución del pipeline | `infra/`, ejecución de `infra.yml` |
| CI/CD: build, lint, pruebas, contratos y escaneo (ya está), imagen al registro, dev automático, prod con aprobación | `ci.yml` + `deploy.yml` + environment `production` |
| Revisiones con división de tráfico y drenado; rollback documentado y **probado** en < 5 min | `deploy.yml`, `rollback.yml`, `docs/runbooks/rollback.md` |
| Identidad administrada para todo; ninguna cadena con clave en la configuración | Bicep (roles) + variables `UseEntraAuth` |
| Secretos en Key Vault leídos con identidad administrada | Secreto `jwt-signing-key` + `Ascua__KeyVaultUri` |
| OpenTelemetry → Application Insights; ≥2 alertas; sondas | `alerts.bicep`, dashboard |
| Costo dentro del crédito, estimación documentada, dev se apaga | `docs/costos.md` |
| URL pública disponible durante la revisión | `README.md` |

## 6. Si algo falla

| Síntoma | Causa probable |
|---|---|
| La réplica no arranca y el log dice `Ascua:KeyVaultUri`/`SecretClient` | Falta `Key Vault Secrets User` para la identidad, falta `Ascua__Azure__ManagedIdentityClientId`, o el secreto no existe |
| `password authentication failed` / `no pg_hba.conf entry` | No se creó el rol con `pgaadauth_create_principal`, el `Username` no es el nombre exacto de la identidad o falta el firewall |
| La app arranca pero `/health/ready` da 503 con `redis` | Falta la *access policy* de Redis para la identidad, el puerto no es 6380 o falta `ssl=True` |
| El cliente web no conecta el WebSocket (403) | `Ascua__Cors__AllowedOrigins__0` no coincide **exactamente** con el origen (`https://` y sin `/` final) |
| Todos los usuarios aparecen con la misma IP en el rate limiting | Falta `Ascua__TrustForwardedHeaders=true` |
| Al iniciar sesión, los tokens fallan en algunas réplicas | Estás usando la clave efímera (`AllowEphemeralKey=true`) con más de una réplica: configura Key Vault |

Cualquier duda, pregúntale a Jan. Los runbooks de operación están en [docs/runbooks/](docs/runbooks/README.md) y el modelo de amenazas en [docs/modelo-de-amenazas.md](docs/modelo-de-amenazas.md).
