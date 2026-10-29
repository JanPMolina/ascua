# ADR-0004: JWT propio frente a un proveedor OIDC externo

- **Estado:** aceptada
- **Fecha:** 2026-10-29
- **Autores:** Equipo Ascua

## Contexto

El enunciado pide registro e inicio de sesión, contraseñas almacenadas de forma segura, sesiones con
expiración y revocación, roles (`admin`) y autenticación también para el canal en tiempo real. El
equipo es de 2 personas, el plazo es corto y el presupuesto es el crédito de Azure for Students.
La evaluación valora entender y defender los mecanismos de seguridad, no solo configurarlos.

## Decisión

Emitir **JWT propios** desde la API:

- **Contraseñas** con **Argon2id** (parámetros de OWASP), con sal por usuario.
- **Access token** JWT de **10 min**, firmado con **ES256**; la clave privada vive en **Key Vault** y
  se lee con identidad administrada. Incluye `sub`, `role`, `jti` y `exp`.
- **Refresh token** opaco y **rotativo**, guardado como *hash* en Postgres y agrupado en familias:
  si se presenta uno ya usado, se revoca **toda la familia** (detección de reutilización).
- Validación estándar de ASP.NET Core (`AddJwtBearer`) con verificación de emisor, audiencia,
  expiración y algoritmo.
- El tiempo real no recibe el JWT: usa un ticket de un solo uso (ADR-0006).

## Alternativas consideradas

| Alternativa | Por qué no |
|---|---|
| Microsoft Entra External ID / Azure AD B2C | Sólido y sin contraseñas propias, pero configuración extensa, flujos de redirección que complican el bot de carga y el cliente de prueba, y dependencia de un *tenant* externo |
| Auth0, Keycloak u otro OIDC | Auth0 tiene límites en el plan gratuito; Keycloak es otro servicio que operar y pagar |
| Cookies de sesión con estado en servidor | Simple para navegador, pero incómodo para el bot .NET y para el *handshake* WS; requiere CSRF |
| ASP.NET Core Identity con sus *bearer tokens* | Tokens opacos propios de Identity, sin control fino de rotación y familia de *refresh* |

## Consecuencias

- **Positivas:** control total y explicable del ciclo de vida de sesiones; sin costo ni dependencia
  externa; el bot y las pruebas se autentican con un `POST` simple.
- **Negativas:** el equipo asume la responsabilidad de almacenar contraseñas y de implementar bien la
  rotación. Se mitiga con librerías probadas (Argon2id, `Microsoft.IdentityModel`) y con pruebas de
  seguridad (reutilización de *refresh*, token vencido, algoritmo `none`, firma alterada).
- **Revocación:** un *access* robado vale hasta 10 min; es el compromiso aceptado a cambio de no
  consultar estado en cada petición. Cerrar sesión revoca la familia de *refresh*.
- **Futuro:** si se necesitara inicio de sesión social, se puede agregar un OIDC externo como
  proveedor adicional sin cambiar el modelo de *access*/*refresh* propio.
