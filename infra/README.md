# Azure: Ascua

Implementación propuesta el 11-nov-2026. La compilación local no demuestra un despliegue real. **No se han marcado requisitos como terminados sin evidencia de Azure.**

## Suscripción y duración

Usar **Azure for Students**, no `Azure subscription 1` de pago por uso. Education muestra USD 100 disponibles y vencimiento 11-nov-2027. La app debe estar disponible hasta el **22-nov-2026 inclusive**, con revisión el 19. No se programa eliminación automática de datos.

La directiva `sys.regionrestriction` de esta suscripción permite **westus2, brazilsouth, chilecentral, belgiumcentral y northcentralus**. La validación inicial rechazó eastus2. Los valores por defecto se ajustaron a **westus2**; confirmar también disponibilidad de cada SKU antes de desplegar la plataforma.

**Preparación verificada en Azure:** despliegue `ascua-bootstrap` en estado Succeeded el 11-nov; grupos dev/prod, identidad y federación OIDC creados, con Contributor + User Access Administrator en ambos grupos. La creación de credenciales es secuencial para evitar conflictos del servicio. Login OIDC real comprobado en infra.yml #10. Por decisión de Juan Pablo, **producción permanece bloqueada hasta que Jan configure la aprobación de GitHub**.

## Preparación del propietario (una vez)

1. Confirmar la suscripción con `az account show`. Registrar Microsoft.App, Microsoft.OperationalInsights, Microsoft.Insights, Microsoft.ManagedIdentity, Microsoft.ContainerRegistry, Microsoft.KeyVault, Microsoft.Cache, Microsoft.DBforPostgreSQL y Microsoft.AlertsManagement. Esperar `Registered`.
2. Revisar `bootstrap.bicep` antes de ejecutar. Crea grupos vacíos dev/prod y una identidad `id-ascua-github`. Otorga a esta identidad **Contributor + User Access Administrator únicamente en rg-ascua-dev y rg-ascua-prod**, sin permisos sobre otros proyectos ni la suscripción. Configura federación con el repositorio exacto y los entornos `dev`/`production`; no crea contraseñas de GitHub. La infraestructura normal usa despliegues de grupo; `main.bicep` permite además la reconstrucción completa por el propietario.
3. Tras autorizar esos permisos, ejecutar desde una sesión del propietario:

```bash
az deployment sub what-if --location westus2 --template-file infra/bootstrap.bicep
az deployment sub create --name ascua-bootstrap --location westus2 --template-file infra/bootstrap.bicep
```

4. Un administrador del repositorio debe crear `dev` y `production`. En production configurar revisores requeridos, restricciones de rama/tag y evitar omitir la aprobación. **En repositorios privados, GitHub Free/Pro/Team no ofrecen required reviewers**: comprobar el plan del propietario. Si la opción no existe, acordar una alternativa de aprobación con el profesor antes de habilitar prod. Los workflows fallan antes del login si production carece de revisores; no simulan una aprobación.
5. Variables (no secretos), de repositorio o entorno:

| Variable | Valor |
|---|---|
| AZURE_SUBSCRIPTION_ID | Suscripción Azure for Students |
| AZURE_TENANT_ID | Tenant de esa suscripción |
| AZURE_CLIENT_ID | Salida clientId del bootstrap |
| AZURE_PRINCIPAL_ID | Salida principalId del bootstrap |
| POSTGRES_ADMIN_OBJECT_ID | Object ID del usuario humano en ese tenant |
| POSTGRES_ADMIN_NAME | Nombre de inicio de sesión del administrador humano |
| ALERT_EMAIL | Correo del operador que recibirá las alertas |
| AZURE_LOCATION | westus2, o región admitida por la suscripción y todos los servicios |

Los GUID no son credenciales. No guardar tokens, claves PEM ni cadenas con contraseña.

El subject OIDC observado en GitHub incluye IDs inmutables: `repo:JanPMolina@231949390/ascua@1438994878:environment:dev` (o `production`). La federación de bootstrap debe coincidir exactamente con ese valor; el formato anterior sin IDs produce AADSTS700213. No ampliar a otros repositorios para resolverlo.

## Primera ejecución

Fusionar el PR con CI en verde. Ejecutar `Infraestructura Azure` en main, destino dev. El workflow muestra what-if, despliega plataforma, espera propagación de RBAC, crea la clave JWT si no existe, crea el principal de la app en Postgres, construye/escanea/publica la imagen, despliega y comprueba readiness. La IP temporal del runner en Postgres se elimina incluso si el bootstrap falla. Un grupo nuevo y vacío se completa en una sola ejecución; la configuración OIDC es preparación de la cuenta.

La clave se genera dentro de un directorio temporal privado del runner, se envía a Key Vault con `--output none` y se elimina. No va a artifacts ni logs. La identidad del pipeline tiene Secrets Officer sobre el vault para inicializarla; la app solo Secrets User.

Reejecutar infra en un ambiente existente actualiza la plataforma y conserva la app; los cambios de versión pasan por deploy.yml. Para actualizar configuración de la app, revisar un what-if específico manteniendo imagen y tráfico actuales.

## Diferencias verificadas respecto al documento original

- Azure Cache for Redis Basic/Standard bloquea altas para tenants nuevos desde abril de 2026. Se usa **Azure Managed Redis Balanced B0, NoCluster**, TLS puerto 10000, sin claves de acceso, identidad Entra. NoCluster evita los problemas CROSSSLOT de los scripts Lua; su compatibilidad real se verificará con una partida y la carga en Azure. No usar EnterpriseCluster como sustituto de NoCluster.
- Dev sin alta disponibilidad Redis; prod con HA. Confirmar disponibilidad de B0/NoCluster y políticas regionales antes del aprovisionamiento.
- El FQDN de la app se deriva del dominio del Environment; no hace falta desplegar una imagen temporal ni arrancar con una clave JWT efímera.
- Límite diario de logs: **0,1 GB por entorno** para proteger el crédito. No es un límite duro de facturación ni un sustituto de un presupuesto.
- Alertas: errores HTTP >1%, partidas FAULTED >0 y reinicios >0. Workbook con juego, errores, latencia media/máxima y recursos. La telemetría agregada no permite afirmar un p95 de muestras originales; conservar el p95 medido por la prueba de carga. CPU/memoria por réplica se extraen en Azure Monitor con la dimensión Replica.

## Validación y pendientes

Comprobaciones locales: compilar main, bootstrap y parámetros; actionlint sobre los tres workflows. El PR ejecuta shellcheck y CI de la app. Falta comprobar en Azure: proveedores, políticas/regiones/cuotas, permisos de OIDC y Postgres, lectura JWT, conexión Managed Redis, sondas, una partida completa, trazas/métricas/alertas reales, carga, reconstrucción y rollback medido <5 min.

No actualizar la URL del README, marcar casillas ni crear v1.0.0 hasta tener esa evidencia.

Fuentes: [retirada de Azure Cache for Redis](https://learn.microsoft.com/en-us/azure/azure-cache-for-redis/cache-whats-new), [políticas de clustering](https://learn.microsoft.com/en-us/azure/redis/choose-clustering-policy), [protección de entornos GitHub](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments).

### Restricción regional de PostgreSQL (11-nov-2026)

La consulta real de capacidades de la suscripción estudiantil rechaza PostgreSQL en `westus2` (`OfferRestricted`, versiones vacías). `northcentralus` permite PostgreSQL 17 y `Standard_B1ms`, por eso solo PostgreSQL usa `postgresLocation=northcentralus`; los demás recursos permanecen en `westus2`. Esta separación añade latencia y posible transferencia interregional: medirla en la prueba de carga y conservar la reserva de USD 95. Se puede cambiar el parámetro antes de crear un ambiente nuevo.

### Modo del Environment

Se declara `environmentMode: WorkloadProfiles` con API 2026-07-01 y únicamente el perfil Consumption. El entorno real creado inicialmente devolvió modo Express, que rechazó `terminationGracePeriodSeconds` y no admite revisiones múltiples. No retirar drenado, división de tráfico, identidad ni observabilidad para acomodar Express. El 12-nov UTC se solicitó cambiar el entorno vacío al modo estándar; verificar `environmentMode` y `provisioningState` antes de reintentar la app. Fuente: https://learn.microsoft.com/azure/container-apps/express-overview
