# Ascua: servidor de juego por turnos en Azure

Proyecto 2 de Diseño de Software (II-2026). Es un servidor autoritativo para **Ascua**, una mazmorra semi-cooperativa por turnos simultáneos para 2-4 jugadores.

- **Stack:** C# / .NET 10, SignalR (WebSocket + MessagePack), Redis, PostgreSQL y Azure Container Apps.
- **Equipo:**
  - Jan: juego, servidor, seguridad, contratos, pruebas y documentación.
  - Juan Pablo: todo lo de Azure (IaC, despliegue, identidad, monitoreo).

> **URL pública:** _pendiente_ · **Documentación de la API:** `/docs`

## Correr localmente (menos de 10 minutos)

Requisitos: Docker Desktop.

```bash
docker compose up --build
```

- App y cliente de prueba: http://localhost:8080
- API (OpenAPI 3.1 con Scalar): http://localhost:8080/docs
- Salud: http://localhost:8080/health/live · http://localhost:8080/health/ready

## Desarrollar y probar

Requisitos: [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet test Ascua.slnx
```

```bash
dotnet run --project src/Ascua.Api
```

```bash
dotnet run --project src/Ascua.Bot -- http://localhost:8080
```

### Contrato REST (`contracts/openapi.json`)

Es la copia versionada del OpenAPI 3.1 que genera la API en `/openapi/v1.json` (sin `servers`, que
depende del despliegue). La prueba `OpenApiContractTests` falla si el código y la copia difieren, y el
CI además la valida con Redocly (`redocly.yaml`). Si cambias un endpoint a propósito, regénerala y
revisa el diff antes de hacer commit:

```bash
ASCUA_UPDATE_OPENAPI=1 dotnet test Ascua.slnx --filter OpenApiContractTests
```

```bash
npx --yes @redocly/cli@2.72.1 lint contracts/openapi.json
```

## Prueba de carga

Solo hace falta Docker. Levanta la API limitada a 0,5 vCPU / 1 GiB y juega N partidas simultáneas de 4 bots:

```bash
loadtests/run.sh --stages 4,8,12,16,24,32 --stage-seconds 60 --label rampa
```

Detalles y opciones (contra Azure, PowerShell) en [loadtests/README.md](loadtests/README.md); resultados, punto de quiebre y optimizaciones medidas en [docs/rendimiento.md](docs/rendimiento.md).

## Estructura

```
src/
  Ascua.Domain/          Reglas del juego (puro: sin red, BD ni framework)
  Ascua.Application/     Casos de uso y puertos
  Ascua.Infrastructure/  Redis, Postgres, Key Vault, azar seguro
  Ascua.Api/             REST, hub SignalR, auth, salud, cliente web (wwwroot)
  Ascua.Bot/             Bot y pruebas de carga
tests/                   Unitarias, arquitectura, integración (+ concurrencia, seguridad, resiliencia)
contracts/               AsyncAPI 3.0 (tiempo real) y OpenAPI 3.1 (REST)
infra/                   Bicep (Azure)
docs/                    ADRs, API, rendimiento, runbooks, modelo de amenazas, pendientes
loadtests/               Prueba de carga con NBomber y reportes
planteamiento/           Reglas del juego, arquitectura y plan de trabajo
inspiracion/             Investigación sobre Guidus (juego que inspira Ascua)
Proyecto3.pdf            Enunciado
```

## Documentos clave

- [Reglas del juego](planteamiento/01-juego.md)
- [Arquitectura](planteamiento/02-arquitectura.md)
- [Plan de trabajo y roles](planteamiento/03-plan-de-trabajo.md)
- [Modelo de amenazas](docs/modelo-de-amenazas.md)
- [Pendientes hacia v1.0.0](docs/pendientes.md)
- [Rendimiento y prueba de carga](docs/rendimiento.md)
- [Decisiones de arquitectura (ADR)](docs/adr/README.md)
- API por área: [autenticación](docs/api/autenticacion.md), [salas](docs/api/salas.md), [tiempo real](docs/api/tiempo-real.md), [historial](docs/api/historial.md), [administración](docs/api/administracion.md)
- [Runbooks de operación](docs/runbooks/README.md)
