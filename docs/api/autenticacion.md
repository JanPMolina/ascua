# Usuarios y autenticación

Referencia viva en `/docs` (Scalar) y `/openapi/v1.json` (OpenAPI 3.1). Este documento explica las decisiones.

## Endpoints

| Método y ruta | Auth | Respuesta correcta |
|---|---|---|
| `POST /api/v1/auth/register` | anónimo | `201` perfil, `Location: /api/v1/users/me` |
| `POST /api/v1/auth/login` | anónimo | `200` tokens |
| `POST /api/v1/auth/refresh` | anónimo (refresh token en el cuerpo) | `200` tokens nuevos |
| `POST /api/v1/auth/logout` | anónimo (refresh token en el cuerpo) | `204` (idempotente) |
| `GET /api/v1/users/me` | Bearer | `200` perfil |
| `PATCH /api/v1/users/me` | Bearer | `200` perfil; cambiar la contraseña exige `currentPassword` y cierra todas las sesiones |

Respuesta de tokens:

```json
{
  "accessToken": "<JWT ES256>",
  "tokenType": "Bearer",
  "expiresIn": 600,
  "refreshToken": "<opaco, base64url de 256 bits>",
  "refreshTokenExpiresAt": "2026-11-05T12:00:00+00:00"
}
```

## Tokens

- **Acceso:** JWT firmado con **ES256** (ECDSA P-256), 10 minutos. Claims `sub` (id del usuario), `name`, `role` (`player` o `admin`), `jti`, `iat`, `nbf`, `exp`, `iss` y `aud`. Al validar solo se acepta ES256 y 30 s de desfase de reloj.
- **Clave de firma:** detrás de `ISigningKeyProvider` (`src/Ascua.Infrastructure/Auth/SigningKeys.cs`). En Development se genera una clave efímera al arrancar. En Azure se debe implementar el proveedor de Key Vault; si `Ascua:KeyVaultUri` está definido y el proveedor no existe, la app no arranca (falla explícita en lugar de firmar con una clave débil).
- **Refresh:** valor opaco aleatorio; en Postgres solo se guarda su SHA-256. Vida de 7 días. Cada login crea una **familia**; cada refresh **rota** el token (el viejo queda revocado con `replaced_by_token_id`).
- **Detección de reutilización:** si llega un refresh token ya rotado o revocado, se revoca **toda la familia** y se responde `refresh-token-reused`. Dos refresh simultáneos del mismo token también cuentan como reutilización (concurrencia optimista con la columna `xmin`). Otras sesiones del mismo usuario (otras familias) no se ven afectadas.
- **Suspensión:** un usuario suspendido no puede hacer login, refresh (se revoca la familia) ni leer o modificar su perfil, aunque su JWT siga vigente.

## Contraseñas

- **Argon2id** (RFC 9106, librería Konscious.Security.Cryptography) con los parámetros mínimos recomendados por OWASP: `m=19456` KiB (19 MiB), `t=2`, `p=1`; sal aleatoria de 16 bytes, salida de 32 bytes. Formato PHC: `$argon2id$v=19$m=19456,t=2,p=1$<sal>$<hash>`; los parámetros viajan con cada hash, así que subirlos después no invalida los existentes.
- Comparación en tiempo constante. Un usuario inexistente verifica contra un hash de relleno para no revelar por tiempo de respuesta qué nombres existen, y la respuesta es la misma que con contraseña errónea.
- Longitud: 10 a 128 caracteres (el máximo acota el costo de Argon2id).
- Nunca se registran en logs ni se devuelven en respuestas; hay una prueba de integración que lo verifica con todos los niveles de log activos.

## Validación de entrada

| Campo | Regla |
|---|---|
| `username` | 3 a 32 caracteres, `^[A-Za-z0-9_]+$`, único sin distinguir mayúsculas |
| `email` | formato de correo, máximo 254, único sin distinguir mayúsculas |
| `password` / `newPassword` | 10 a 128 caracteres |
| `refreshToken` | 1 a 128 caracteres |

## Límites de tasa

Ventana fija, configurable en `Ascua:RateLimiting`:

- `/api/v1/auth/*`: 10 solicitudes por minuto **por IP**.
- `/api/v1/users/me`: 60 por minuto **por usuario**.

Al excederse: `429` con `Retry-After` (segundos) y Problem Details `rate-limited`. Detrás del ingress de Container Apps hay que activar `Ascua:TrustForwardedHeaders` para limitar por la IP real del cliente.

## Problem Details (RFC 9457)

Todas las respuestas de error usan `application/problem+json` con `type` = `urn:ascua:problem:<código>`, el mismo código en la extensión `code` y `traceId` para correlacionar con los logs. Los códigos son estables: los clientes deben decidir por `type`/`code`, no por `title` ni `detail`.

| Código | HTTP | Cuándo |
|---|---|---|
| `validation` | 400 | Cuerpo ausente, JSON mal formado o campos inválidos (`errors` trae los mensajes por campo) |
| `invalid-current-password` | 400 | `PATCH /users/me` con `newPassword` y `currentPassword` ausente o incorrecta |
| `unauthorized` | 401 | Falta el token de acceso, es inválido o expiró (con `WWW-Authenticate: Bearer`) |
| `invalid-credentials` | 401 | Usuario o contraseña incorrectos (no distingue cuál) |
| `invalid-refresh-token` | 401 | Refresh token desconocido o expirado |
| `refresh-token-reused` | 401 | Refresh token ya rotado o revocado; se revocó la familia |
| `forbidden` | 403 | Autenticado pero sin permiso |
| `account-suspended` | 403 | La cuenta está suspendida |
| `not-found` | 404 | Ruta o recurso inexistente |
| `username-taken` | 409 | Nombre de usuario en uso |
| `email-taken` | 409 | Correo en uso |
| `rate-limited` | 429 | Límite de tasa excedido |
| `internal` | 500 | Error no controlado (sin detalles internos) |

## Persistencia

Tablas `users` y `refresh_tokens` (snake_case) creadas por la migración `InitialUsers` en `src/Ascua.Infrastructure/Persistence/Migrations`. Nueva migración:

```bash
dotnet tool install --global dotnet-ef --version 10.0.14
dotnet ef migrations add <Nombre> -p src/Ascua.Infrastructure -s src/Ascua.Infrastructure -o Persistence/Migrations
```

La conexión se construye con `NpgsqlDataSource` en `PostgresDataSource.Create`; ahí está documentado el punto de extensión para autenticación Entra con `DefaultAzureCredential` (token como contraseña periódica), que conectará el despliegue en Azure.
