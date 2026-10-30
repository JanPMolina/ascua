# Modelo de amenazas

Este documento analiza las amenazas al servidor de Ascua con **STRIDE**, componente por componente. Cada amenaza indica:
- la **mitigación** implementada;
- la **prueba** que la demuestra, en `tests/` y en verde en CI;
- el **riesgo residual**.

Lo que todavía falta está en **Pendiente**, al final, y también en [pendientes.md](pendientes.md).

## Alcance y activos

**Qué se protege**, de más a menos crítico:
1. **Información oculta de cada jugador:** pacto secreto, bendiciones, orden de la ronda en curso y casillas fuera de su visión. Si se filtra, el juego se rompe. El enunciado lo exige: «ningún mensaje, endpoint ni log».
2. **Integridad de la partida:**
   - solo el servidor resuelve, valida y decide el azar;
   - cada jugada se aplica una sola vez y en orden;
   - el resultado es inmutable.
3. **Cuentas:** contraseñas, *refresh tokens*, la clave de firma JWT y el rol `admin`.
4. **Disponibilidad:** muchas partidas simultáneas; la falla de una no afecta a las demás.
5. **Auditoría:** las acciones de administración son trazables y no se pueden alterar.

**Actores:**
- **Jugador legítimo.**
- **Jugador tramposo:** está autenticado y quiere ver lo oculto o actuar fuera de turno.
- **Atacante anónimo en Internet.**
- **Administrador**, que puede abusar del rol o perder su cuenta.
- **Dependencia comprometida:** un paquete de NuGet o una acción de GitHub.

## Diagrama y fronteras de confianza

```
 Navegador / bot ──HTTPS (REST)──┐            ┌──────────── Azure Container Apps ─────────────┐
                 ──WSS (SignalR)─┼──[F1]──▶   │ Ingress TLS ─▶ Réplica N: Ascua.Api            │
                                 │            │   REST · MatchHub · RoundTimerService           │
                                 │            │   Ascua.Domain (puro, sin E/S)                  │
                                 │            └───────┬──────────────┬──────────────┬──────────┘
                                 │                [F2]│          [F2]│          [F3]│
                                 │              Redis (estado   PostgreSQL       Key Vault
                                 │              caliente, lock, (eventos,        (clave JWT)
                                 │              backplane)      usuarios, audit)
 GitHub Actions ──OIDC──[F4]──▶ ACR / Azure (despliegue)
```

| Frontera | Qué la cruza |
|---|---|
| **F1** | Internet ↔ app. Todo lo que entra es hostil hasta que se valida |
| **F2** | App ↔ datos. Red privada e identidad administrada |
| **F3** | App ↔ secretos |
| **F4** | Pipeline ↔ nube |

## Amenazas por componente

### 1. Autenticación y sesiones (REST `/api/v1/auth`)

| STRIDE | Amenaza | Mitigación | Prueba |
|---|---|---|---|
| S | Adivinar contraseñas o probar credenciales filtradas en otros sitios | Argon2id (Konscious) con sal aleatoria; límite de tasa por IP en `/auth` (429 con `Retry-After`) | `Auth_endpoints_are_rate_limited_with_retry_after`, `PasswordHasherTests` |
| S | Averiguar qué usuarios existen | El mismo 401 para un usuario desconocido y para una contraseña incorrecta | `Wrong_password_and_unknown_user_return_the_same_401` |
| S | Robar un *refresh token* y reusarlo | *Refresh* rotativo de un solo uso. Si se reusa uno ya rotado, se **revoca toda la familia**. En la base se guarda solo el hash | `Refresh_rotates_token_and_new_token_works`, `Reusing_a_rotated_refresh_token_revokes_the_whole_family`, `Concurrent_refresh_with_the_same_token_lets_only_one_win` |
| S | Falsificar un JWT | ES256 con clave privada fuera del repositorio; se valida emisor, audiencia, vencimiento y `kid` | `Profile_without_valid_token_is_401_problem` |
| T | Cambiar la contraseña de una sesión robada | Se exige la contraseña actual, y el cambio cierra las demás sesiones | `Changing_password_requires_current_one_and_closes_sessions` |
| R | Negar un cierre de sesión | El *logout* revoca la sesión en la base | `Logout_revokes_the_session` |
| I | Que contraseñas o hashes aparezcan en logs o respuestas | Ningún DTO ni log lleva la contraseña ni el hash | `Passwords_and_hashes_never_appear_in_logs_or_responses` |
| D | Saturar la API con registros o logins | Límite de tasa por IP en `/auth` y por usuario en el resto, más `ForwardedHeaders` solo detrás del ingress (`Ascua__TrustForwardedHeaders`) para ver la IP real | `Auth_endpoints_are_rate_limited_with_retry_after` |
| E | Seguir operando con una cuenta suspendida | `IUserAccessGate`: se revisa en cada petición y al consumir un ticket WS, y se revocan los *refresh* | `Suspended_user_cannot_login_refresh_or_read_profile`, `Suspending_a_user_cuts_login_refresh_live_access_token_and_websocket` |

### 2. Salas y emparejamiento (REST `/api/v1/rooms`, `/matchmaking`)

| STRIDE | Amenaza | Mitigación | Prueba |
|---|---|---|---|
| S/E | Entrar a una sala privada sin invitación | Las salas privadas no se listan; para entrar hace falta el código, generado con `RandomNumberGenerator` | `Private_room_is_hidden_from_strangers_and_joined_by_invite_code` |
| T | Que varios se unan a la vez y se pase la capacidad | Scripts Lua atómicos en Redis (ADR-0007) | `Concurrent_joins_to_a_room_of_four_let_exactly_four_in` |
| T | Quedar asignado a dos partidas para duplicar recompensas o bloquear a otros | Marca de «partida activa» por usuario; un usuario está en una sola sala o cola a la vez | `A_user_is_in_one_room_or_queue_at_a_time`, `Concurrent_matchers_never_assign_a_player_to_two_matches` |
| I | Ver los detalles privados de salas ajenas | La lista pública no muestra el código ni datos privados | `Public_room_is_listed_and_visible_to_others_without_private_details` |
| D | Mandar entradas enormes o inválidas | Validación de esquema y tamaño, con errores Problem Details (RFC 9457) | `Lobby_endpoints_require_authentication_and_validate_input`, `Invalid_registration_returns_validation_problem_with_field_errors` |

### 3. Tiempo real (`MatchHub`, SignalR sobre WSS)

| STRIDE | Amenaza | Mitigación | Prueba |
|---|---|---|---|
| S | Abrir el WebSocket sin autenticarse o con un ticket ajeno | Ticket de **un solo uso**, 32 bytes de CSPRNG, guardado en Redis solo como hash SHA-256, con TTL de 30 s y atado a usuario y partida; la versión del protocolo se revisa antes de gastarlo (ADR-0006) | `Ticket_is_single_use_and_protocol_version_is_checked_before_spending_it` |
| S | Abrir el WebSocket desde un origen malicioso (CSWSH) | Lista blanca de orígenes (`Ascua:Cors:AllowedOrigins`) en el *handshake* | Configurado en `TicketAuthentication`. **Falta una prueba propia; ver Pendiente** |
| E | Actuar en una partida ajena o fuera del turno | Se verifica que el usuario sea participante en `JoinMatch` y en cada orden; el dominio rechaza a extraños y las órdenes de otra ronda | `Non_participant_cannot_get_a_ticket_join_or_act`, `Strangers_cannot_send_orders`, `Orders_for_another_round_are_stale` |
| T | Enviar resultados en lugar de intenciones | El cliente solo manda órdenes (`MOVE`, `ATTACK`…); el servidor resuelve todo. Una orden inválida se reemplaza por `GUARD` | `Malformed_orders_are_rejected_with_a_stable_code`, `The_resolver_replaces_invalid_orders_with_guard`, `Order_for_another_round_or_with_bad_schema_is_rejected_without_closing_the_match` |
| T | Repetir o duplicar una jugada | `clientActionId` idempotente; un solo cerrador por ronda (lock en Redis) más `UNIQUE(match_id, seq)` en Postgres | `Resent_order_is_acknowledged_once_and_applied_once`, `Two_replicas_closing_the_same_round_produce_a_single_resolution` |
| T | Manipular el azar o el reloj desde el cliente | Temporizadores y azar solo en el servidor; la semilla de la partida sale de `SecureRandom` (CSPRNG) | `The_same_input_with_the_same_random_source_gives_the_same_result`, `Timer_closes_the_round_with_guard_when_the_deadline_expires` |
| I | **Ver el pacto, las bendiciones o la orden de otro jugador, o casillas fuera de la propia visión** | `PlayerViewProjector`: cada mensaje se arma por jugador; niebla de guerra; las vistas no tienen ningún campo que pueda llevar la semilla | `The_view_of_A_never_contains_the_secrets_of_B`, `A_player_view_never_carries_another_player_secrets`, `No_view_ever_leaks_another_bearers_pact_or_blessings` (propiedades), `Views_have_no_member_that_could_carry_the_seed`, `Fog_of_war_only_sends_tiles_within_vision` |
| D | Inundar el hub con mensajes | *Token bucket* por conexión (`HubTokenLimit`/`HubTokensPerSecond`); más de 50 rechazos en 60 s cierra la conexión con 4029 | Configurado en `RealtimeConnections` |
| D | Mandar mensajes gigantes | `MaximumReceiveMessageSize` de 4 KiB | Configurado en `RealtimeServiceCollectionExtensions` |
| D | Abrir conexiones sin límite | Máximo 3 conexiones por usuario en todas las réplicas | `A_user_cannot_open_more_than_three_connections` |
| D | Que una partida con un bug tumbe el proceso o a las demás | Aislamiento por partida: la excepción la marca `FAULTED`, sin afectar al resto | Código en `MatchEngine`. **Falta la prueba; ver Pendiente** |
| R | Negar haber abandonado | Al vencer la gracia de reconexión se aplica el abandono, y queda como evento en `match_events` | `Leaving_an_in_progress_match_counts_as_abandonment` |

### 4. Historial y reproducción (REST `/api/v1/matches`)

| STRIDE | Amenaza | Mitigación | Prueba |
|---|---|---|---|
| I | Ver el detalle o la reproducción de una partida ajena, o saber si existe | Una partida ajena responde igual que una inexistente (404) | `Detail_and_replay_of_someone_elses_match_answer_like_a_missing_one`, `History_is_paginated_newest_first_and_only_lists_own_matches` |
| I | Ver los secretos de otros en la reproducción | La reproducción usa la vista del jugador que la pide; solo un admin puede pedir `?as=full`, y queda auditado | `Finished_match_replays_deterministically_with_the_requesters_view_only`, `Admin_can_request_the_full_view_and_a_player_cannot` |
| T | Reescribir el resultado de una partida terminada | El resultado es inmutable: las filas de partidas terminadas no se pueden reescribir | `Finished_match_rows_cannot_be_rewritten` |

### 5. Administración (REST `/api/v1/admin`)

| STRIDE | Amenaza | Mitigación | Prueba |
|---|---|---|---|
| E | Que un jugador use funciones de admin | Política `admin` (claim `role=admin` de una cuenta no suspendida): 401 para anónimos y 403 para jugadores | `Admin_endpoints_reject_anonymous_with_401_and_players_with_403` |
| R | Que un admin niegue lo que hizo | Cada acción y su fila de `audit_log` se guardan en la misma transacción, con actor, IP, `traceId` y motivo obligatorio | `Terminating_a_match_stops_it_notifies_players_releases_them_and_is_audited`, `Admin_actions_require_a_reason` |
| T | Borrar o alterar la auditoría | `audit_log` es *append-only*: la app solo inserta y un *trigger* rechaza `UPDATE`, `DELETE` y `TRUNCATE` | `Audit_log_never_contains_passwords_or_tokens_and_is_append_only` |
| I | Que la vista de admin filtre tokens o ids de conexión | Las respuestas solo traen datos agregados, nunca tickets ni tokens | `Admin_lists_active_matches_and_live_connections_without_secrets` |
| E | Tener un admin por defecto con credenciales conocidas | No existe; el primero se promueve con `BootstrapUsername` a partir de una cuenta ya registrada, y queda auditado | `Bootstrap_setting_promotes_the_named_user_once_and_audits_it_as_system` |

### 6. Datos y secretos (Redis, Postgres, Key Vault)

| STRIDE | Amenaza | Mitigación | Prueba o evidencia |
|---|---|---|---|
| I | Un secreto en el repositorio | gitleaks en cada push y PR; en local, la clave JWT es efímera y no hay contraseñas por defecto | Job `secrets-scan` del CI |
| I | Robar la clave de firma JWT | En Azure vive en Key Vault y se lee con identidad administrada; rotación en [runbooks/rotar-clave-jwt.md](runbooks/rotar-clave-jwt.md) | **Pendiente (Juan Pablo):** `KeyVaultSigningKeyProvider` |
| S/I | Robar la cadena de conexión de Postgres o Redis | En Azure se usa autenticación Entra con identidad administrada, sin contraseñas en la configuración | **Pendiente (Juan Pablo)** |
| T | Perder o duplicar jugadas confirmadas por un reinicio o una carrera | *Event sourcing* liviano en Postgres con `UNIQUE(match_id, seq)`; Redis se puede reconstruir desde Postgres (ADR-0003) | `Two_players_play_a_full_match_with_contiguous_seq_and_immutable_result`. **Falta la prueba de reinicio; ver Pendiente** |
| D | Que se caiga Redis o Postgres | `/health/ready` saca a la réplica del balanceo sin reiniciarla; [runbook](runbooks/dependencias-caidas.md) | **Falta la prueba de resiliencia; ver Pendiente** |

### 7. Transporte y navegador

| STRIDE | Amenaza | Mitigación | Prueba |
|---|---|---|---|
| I/T | Interceptar el tráfico | Solo HTTPS y WSS: TLS en el ingress de Container Apps | Configuración de Juan Pablo |
| T | XSS en el cliente web | CSP estricta (`script-src 'self'`, sin *inline*); el cliente pinta con `textContent`, nunca con HTML | `Vendored_scripts_match_their_subresource_integrity_hashes` (las librerías externas van con SRI). **Faltan las cabeceras del servidor; ver Pendiente** |
| S | *Clickjacking* o CSRF | La API usa `Authorization: Bearer` (sin cookies), así que CSRF no aplica; `frame-ancestors 'none'` llega con las cabeceras de seguridad | **Pendiente** |

### 8. Cadena de suministro y contenedor

| Amenaza | Mitigación | Evidencia |
|---|---|---|
| Dependencia con una vulnerabilidad conocida | Auditoría de NuGet con warnings como errores; Dependabot para NuGet y Actions; versiones centralizadas con *pinning* transitivo | CI `build-test`, PRs de Dependabot |
| Código con fallas de seguridad | CodeQL en cada PR; falla con hallazgos de nivel *error* | `codeql.yml` |
| Imagen con CVEs críticos o que corra como root | Imagen *chiseled* (sin shell ni gestor de paquetes), usuario no root (UID 1654); Trivy falla con CVEs críticos | Job `image` del CI, `Dockerfile` |
| Pipeline comprometido con credenciales de larga vida | Federación OIDC GitHub → Azure, sin secretos guardados; *environments* con aprobación manual para prod | **Pendiente (Juan Pablo)** |

## Riesgos residuales aceptados

- **Colusión fuera del juego:** dos jugadores pueden contarse sus pactos por otro canal. El servidor no puede evitarlo, y el diseño semi-cooperativo lo asume.
- **Bots que juegan como humanos:** no hay CAPTCHA ni detección de bots; el límite de tasa acota el abuso.
- **Un admin malicioso:** puede terminar partidas o suspender cuentas, pero todo queda auditado sin posibilidad de borrarlo.
- **Denegación de servicio volumétrica:** está fuera del alcance de la app; se delega en la plataforma (ingress de Azure).

## Pendiente

- [ ] CORS de la API REST y cabeceras de seguridad HTTP con su prueba de integración (CSP, `nosniff`, `frame-ancestors`, HSTS), más una prueba del rechazo de orígenes en el WebSocket.
- [ ] Pruebas de resiliencia: reinicio de instancia, caída de Redis o Postgres, y aislamiento `FAULTED`.
- [ ] Juan Pablo: Key Vault, Entra en Postgres y Redis, OIDC.
