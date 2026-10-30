# Seguridad del transporte

Qué protege la conexión entre el navegador y la API, y por qué. El código está en `src/Ascua.Api/Security/` (`Cors.cs` y `SecurityHeaders.cs`) y el orden en `src/Ascua.Api/Program.cs`. Las pruebas están en `tests/Ascua.Integration.Tests/SecurityHeadersTests.cs`.

## HTTPS y WSS

- **En Azure** el ingress de Container Apps termina TLS. Con `allowInsecure: false` (lo configura Juan Pablo en Bicep), el ingress redirige HTTP a HTTPS y el WebSocket solo se abre como `wss://`. La app recibe HTTP dentro del ambiente y sabe que el cliente usó HTTPS por `X-Forwarded-Proto`, que solo se acepta con `Ascua:TrustForwardedHeaders=true`.
- **No hay `UseHttpsRedirection`.** El ingress ya redirige, y la app no tiene puerto HTTPS propio: el middleware solo registraría una advertencia por petición sin redirigir nada. Localmente, `docker compose` sirve solo HTTP en `localhost:8080`.
- **HSTS** (`Strict-Transport-Security: max-age=31536000`, un año) fuera de Development. ASP.NET Core solo lo envía en respuestas HTTPS y nunca a `localhost`. No lleva `includeSubDomains` ni `preload`, porque el dominio `*.azurecontainerapps.io` no es nuestro.

## CORS

Una sola lista blanca para REST y WebSocket: `Ascua:Cors:AllowedOrigins` (`Ascua__Cors__AllowedOrigins__0`, `__1`… en variables de entorno).

| Aspecto | Valor | Por qué |
|---|---|---|
| Orígenes | solo los de la lista; nunca `AllowAnyOrigin` | un sitio ajeno no puede leer respuestas de la API con el token de la víctima |
| Credenciales | no (`Access-Control-Allow-Credentials` ausente) | el JWT viaja en `Authorization`, no en cookies |
| Métodos | `GET`, `POST`, `PUT`, `PATCH`, `DELETE` | los que usa la API |
| Cabeceras de la petición | `Authorization`, `Content-Type` | las únicas que envía el cliente (`Accept` ya es simple) |
| Cabeceras expuestas | `Location`, `Retry-After` | `201` con la ruta del recurso y `429` con la espera |
| Caché del preflight | 10 minutos | menos preflights sin dejar una política vieja mucho tiempo |

`UseCors` va después de los archivos estáticos y antes de la autenticación y del *rate limiting*. Así, un preflight `OPTIONS` se responde con `204` sin pedir token y sin gastar cupo. Un origen fuera de la lista recibe la respuesta sin ningún `Access-Control-Allow-*`, y el navegador no deja leerla.

En el WebSocket, `UseWebSockets` rechaza con `403` un upgrade cuyo `Origin` no está en la lista. Un cliente que no es navegador (el bot o la prueba de carga) no envía `Origin` y se autentica con el ticket. La prueba `WebSocket_upgrade_from_foreign_origin_is_rejected_before_spending_the_ticket` lo verifica contra un Kestrel real, porque el cliente WebSocket del TestServer se salta esa verificación: un `Origin` ajeno recibe `403`; con el mismo ticket, el origen permitido sí abre el socket (el rechazo no gastó el ticket); y sin `Origin` también entra.

## Encabezados de seguridad

Van en **todas** las respuestas: API, cliente web, archivos estáticos, `/docs` y errores (`401`, `404`, `500`). Se escriben en `Response.OnStarting` para que sobrevivan a `UseExceptionHandler`, que limpia los encabezados cuando algo falla.

| Encabezado | Valor | Para qué |
|---|---|---|
| `Content-Security-Policy` | ver abajo | sin scripts inyectados, sin orígenes ajenos y sin ser embebida (`frame-ancestors 'none'`) |
| `X-Content-Type-Options` | `nosniff` | el navegador respeta el `Content-Type` (un JSON no se ejecuta como script) |
| `X-Frame-Options` | `DENY` | *clickjacking* en navegadores sin `frame-ancestors` |
| `Referrer-Policy` | `no-referrer` | la URL (con ids de partida) no se filtra a otros sitios |
| `Cross-Origin-Opener-Policy` | `same-origin` | otra pestaña abierta con `window.open` no conserva referencia a la nuestra |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=(), payment=(), usb=()` | el juego no usa esas API; un script inyectado tampoco podría |
| `Server` | ausente (`AddServerHeader = false`) | no se anuncia Kestrel |

### Content-Security-Policy

**Cliente web, API y todo lo demás:**

```
default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self';
base-uri 'none'; form-action 'none'; frame-ancestors 'none'
```

El cliente no tiene scripts ni estilos inline, y SignalR está *vendorizado* con SRI. `connect-src 'self'` cubre el `fetch` a la API y el `ws://`/`wss://` al mismo host. Antes esta política vivía como `<meta>` en `index.html`; ahora la envía el servidor, que además puede aplicar `frame-ancestors` (un `<meta>` no puede).

**`/docs` (Scalar):**

```
default-src 'none'; script-src 'self' 'sha256-…'; style-src 'self' 'unsafe-inline'; img-src 'self' data:;
font-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'
```

- **Script inline por hash:** Scalar arranca con un `<script type="module">` inline. Se permite solo ese script, por su SHA-256 (`SecurityHeaders.ScalarInitScriptHash`), y no con `'unsafe-inline'`. Si una actualización de Scalar o un cambio en sus opciones altera ese script, la prueba `Docs_inline_script_is_allowed_by_hash_not_by_unsafe_inline` falla y muestra el hash nuevo.
- **`'unsafe-inline'` en estilos:** Vue inyecta estilos en tiempo de ejecución y no se pueden enumerar por hash. Es el único relajamiento y aplica solo a `/docs`.
- **Fuentes:** `DefaultFonts = false` evita las fuentes de `fonts.scalar.com`, así que ningún origen externo queda permitido.
