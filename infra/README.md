# Infraestructura (responsable: Juan Pablo)

Aquí va **todo lo de Azure**, en Bicep. Un ambiente nuevo debe levantarse con una sola ejecución del pipeline.

## Pendiente

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

Lo que la app espera recibir (imagen, puerto, sondas y variables de entorno) está en la sección **"Interfaz entre los dos"** de [planteamiento/03-plan-de-trabajo.md](../planteamiento/03-plan-de-trabajo.md).
