# ADR-0005: .NET 10 en lugar de .NET 8 (OpenAPI 3.1 nativo)

- **Estado:** aceptada
- **Fecha:** 2026-10-29
- **Autores:** Equipo Ascua

## Contexto

El enunciado exige **.NET 8 o superior** y documentar la API REST con **OpenAPI 3.1**. Hoy hay dos
versiones LTS soportadas: .NET 8 (noviembre de 2023, soporte hasta noviembre de 2026, es decir,
**justo en la fecha de entrega**) y .NET 10 (noviembre de 2025, soporte hasta noviembre de 2028).

En .NET 8, `Microsoft.AspNetCore.OpenApi` no existe como generador de documentos (solo metadatos) y
Swashbuckle genera OpenAPI 3.0; obtener 3.1 exige herramientas de terceros o transformar el documento.
En .NET 10, `Microsoft.AspNetCore.OpenApi` genera **OpenAPI 3.1 de forma nativa** (con JSON Schema
2020-12) en tiempo de ejecución y en *build*.

## Decisión

Usar **.NET 10 (LTS)**, fijado en `global.json`, con `Nullable=enable` y `TreatWarningsAsErrors=true`:

- `AddOpenApi()` + `MapOpenApi()` para el documento 3.1 y **Scalar** como UI en `/docs`.
- El documento se exporta en *build* a `contracts/openapi.json` y el CI lo compara para detectar
  cambios de contrato no intencionados.
- Imagen base *chiseled* de .NET 10 sin root.

## Alternativas consideradas

| Alternativa | Por qué no |
|---|---|
| .NET 8 + Swashbuckle | Genera OpenAPI 3.0, no 3.1; Swashbuckle además perdió su lugar en las plantillas oficiales |
| .NET 8 + NSwag u OpenAPI.NET con conversión a 3.1 | Funciona, pero agrega una dependencia y un paso de transformación frágil solo para cumplir la versión |
| .NET 9 | Ya trae `Microsoft.AspNetCore.OpenApi`, pero genera 3.0 y es STS (soporte corto) |

## Consecuencias

- **Positivas:** cumplimiento directo de OpenAPI 3.1 sin herramientas extra; soporte LTS hasta 2028;
  mejoras de rendimiento y de AOT/*trimming* de la versión.
- **Negativas:** algunas librerías pueden tardar en publicar versiones compatibles; se mitiga
  eligiendo dependencias mantenidas (Npgsql, StackExchange.Redis, NBomber) y con Dependabot.
- **Requisito de entorno:** el equipo y el CI necesitan el SDK de .NET 10 (`actions/setup-dotnet` lee
  `global.json`).
