# Infraestructura (responsable: Juan Pablo)

> **Empieza por [Juan_Pablo_instrucciones.md](../Juan_Pablo_instrucciones.md)**: el paso a paso completo, las variables y cómo trabajamos en paralelo.

Aquí va **todo lo de Azure**, en Bicep. Un ambiente nuevo debe levantarse con una sola ejecución del pipeline.

## Pendiente

La lista completa del proyecto, con lo de ambos, está en [docs/pendientes.md](../docs/pendientes.md).

- [ ] `main.bicep` con alcance de suscripción y módulos:
  - Container Apps Environment y app;
  - Azure Cache for Redis;
  - PostgreSQL Flexible Server;
  - Key Vault;
  - Container Registry;
  - Log Analytics y Application Insights;
  - identidad administrada con sus permisos.
- [ ] `parameters/dev.bicepparam` y `parameters/prod.bicepparam`.
- [ ] Workflows en `.github/workflows/`:
  - `infra.yml`: ambiente desde cero;
  - `deploy.yml`: dev automático, prod con aprobación;
  - `rollback.yml`: menos de 5 minutos.
- [ ] Federación OIDC de GitHub con Azure; *environments* `dev` y `production` en GitHub.
- [ ] Alertas (p95 > 200 ms, errores > 1%, reinicios) y estimación de costos en `docs/costos.md`.
- [ ] Key Vault con el secreto `jwt-signing-key` y roles de Entra en Postgres y Redis para la identidad. El código ya está listo: solo hay que configurarlo.
- [ ] Correr `loadtests/run.sh` contra dev y agregar CPU y memoria por réplica a `docs/rendimiento.md`.

Lo que la app espera recibir (imagen, puerto, sondas y variables de entorno) está en la sección **"Interfaz entre los dos"** de [planteamiento/03-plan-de-trabajo.md](../planteamiento/03-plan-de-trabajo.md).
