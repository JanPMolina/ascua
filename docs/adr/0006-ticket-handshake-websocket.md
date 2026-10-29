# ADR-0006: Ticket de un solo uso para el *handshake* WebSocket

- **Estado:** aceptada
- **Fecha:** 2026-10-29
- **Autores:** Equipo Ascua

## Contexto

La API WebSocket de los navegadores **no permite enviar cabeceras** (`Authorization`) en el
*upgrade*. Por eso el cliente de SignalR manda el token en el *query string* (`?access_token=...`).
Poner ahí el **JWT de acceso** (ADR-0004) es riesgoso: las URL quedan en logs del *ingress*, del
proxy y de diagnóstico, y un JWT vale 10 minutos y sirve para toda la API REST.

Además, la conexión WS se ata a **una** partida: solo deben entrar participantes, y el enunciado pide
autenticación y autorización también en el canal en tiempo real.

## Decisión

1. El cliente, autenticado con su JWT por HTTP, pide `POST /api/v1/realtime/tickets` con el `matchId`.
2. El servidor verifica que el usuario participe en la partida y genera un **ticket opaco** de 32
   bytes aleatorios (CSPRNG, base64url), guardado en Redis como `ticket:{hash}` → `{userId, matchId}`
   con **TTL de 30 s**.
3. El cliente abre `wss://.../realtime/match?protocolVersion=1&access_token={ticket}`.
4. En el *upgrade*, un manejador de autenticación propio lo consume con **`GETDEL`** (atómico): si no
   existe, venció o ya se usó, responde **HTTP 401 `TICKET_INVALID`** y no se abre el socket. Si es
   válido, crea el `ClaimsPrincipal` del usuario con el `matchId` autorizado.
5. Cada reconexión pide un ticket nuevo. Los tickets se rotulan en los logs como `[REDACTED]`.

## Alternativas consideradas

| Alternativa | Por qué no |
|---|---|
| JWT de acceso en el *query* | Queda en logs y es reutilizable durante 10 min contra toda la API |
| Cookie de sesión en el *upgrade* | Funciona en navegador, pero exige CSRF/validación de `Origin` estricta y no sirve igual para el bot .NET; mezcla dos modelos de sesión |
| Autenticar con un primer mensaje dentro del socket | El socket queda abierto sin autenticar (superficie de DoS) y SignalR no lo soporta de forma natural |
| Subprotocolo `Sec-WebSocket-Protocol` con el token | Truco no estándar, también termina en logs y SignalR no lo usa |

## Consecuencias

- **Positivas:** lo que aparece en una URL caduca en 30 s y no sirve dos veces; el ticket solo vale
  para el WS de **una** partida, no para la API. El rechazo ocurre antes de abrir el socket, sin
  consumir recursos del hub.
- **Negativas:** una petición HTTP extra por conexión y una dependencia de Redis para conectar (si
  Redis no responde, nadie puede conectar; aceptado porque sin Redis tampoco hay partida).
- **Pruebas:** `Ascua.Security.Tests` cubre ticket reutilizado, vencido, de otra partida y de otro
  usuario; las razones de cierre están documentadas en `contracts/asyncapi.yaml`.
