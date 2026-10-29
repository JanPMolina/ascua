# ADR-0002: SignalR solo sobre WebSockets con MessagePack y sin *sticky sessions*

- **Estado:** aceptada
- **Fecha:** 2026-10-29
- **Autores:** Equipo Ascua

## Contexto

El servidor empuja a cada jugador su vista tras cada ronda y avisa de conexiones, drafts y fin de
partida; el cliente nunca debe hacer *polling*. Corre en Azure Container Apps con varias réplicas
detrás del *ingress*, que no garantiza afinidad. La meta de rendimiento es p95 < 200 ms desde el
cierre de la ronda hasta que el último jugador recibe `RoundResolved`.

SignalR, por defecto, hace una petición `negotiate` y luego abre el transporte. Si `negotiate` y la
conexión caen en réplicas distintas, la conexión falla, y los *fallbacks* (SSE, *long polling*)
necesitan que todas las peticiones de un cliente vayan a la misma réplica: es decir, *sticky sessions*.

## Decisión

- **SignalR** (ASP.NET Core) para el hub `/realtime/match`, con **solo WebSockets** y
  `SkipNegotiation = true`: la conexión es una única petición `GET` de *upgrade*, que cualquier
  réplica puede atender.
- **Protocolo MessagePack** (`AddMessagePackProtocol`) con mapas de claves de texto, para mensajes
  compactos y compatibles al agregar campos. JSON solo se habilita en `local` y `dev` para depurar.
- **Backplane de Redis** (`AddStackExchangeRedis`) para que `Clients.Group`/`Clients.User` alcancen
  conexiones de cualquier réplica.
- Heartbeat 10 s / timeout 30 s, máximo 4 KB por mensaje y reconexión del lado del cliente.
- El contrato se documenta a mano en `contracts/asyncapi.yaml` (AsyncAPI 3.0) y se valida en CI.

## Alternativas consideradas

| Alternativa | Por qué no |
|---|---|
| SignalR con todos los transportes | Requiere *sticky sessions* (afinidad de cookie en el *ingress*), lo que rompe el balanceo y complica el drenado |
| Azure SignalR Service | Resuelve afinidad y escala, pero cuesta y oculta justo lo que el curso quiere evaluar (difusión entre réplicas). Se deja como opción futura |
| WebSocket crudo (`System.Net.WebSockets`) | Control total, pero habría que construir *framing*, invocaciones con respuesta, grupos, reconexión y *backplane* |
| gRPC *streaming* | No funciona en navegadores sin gRPC-Web, y gRPC-Web no soporta *streaming* bidireccional |
| JSON como protocolo principal | Más legible pero bastante más pesado; la optimización "JSON con vista completa → MessagePack con deltas" es una de las medidas de rendimiento del proyecto |

## Consecuencias

- **Positivas:** cualquier réplica acepta cualquier conexión; desplegar o escalar no exige afinidad.
  Mensajes pequeños y tipados. Grupos y usuarios de SignalR simplifican las vistas privadas.
- **Negativas:** clientes detrás de proxies que bloquean WebSockets no pueden jugar (aceptado: es un
  juego en tiempo real y el *ingress* soporta WSS). MessagePack es binario: depurar requiere JSON en
  dev o herramientas que lo decodifiquen.
- **Operación:** al apagarse, una réplica envía `ServerDraining` y cierra con `SERVER_DRAINING`; los
  clientes reconectan a otra réplica. El *backplane* agrega un salto de red por difusión, que se mide
  en `ascua.broadcast.latency`.
