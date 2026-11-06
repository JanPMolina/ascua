# Versión 4 del juego

> **Estado:** rama `feat/juego-v4`, cortada de `feat/red-local`. Solo para jugar en red local: **no va a `main`**. Este documento **manda sobre** 01-juego.md, 04-caminos-tras-morir.md, 05-heroes.md y 06-version-3.md: donde se contradigan, vale lo de aquí. Los números finales están en §9 y en [docs/balance.md](../docs/balance.md). En el código y el contrato siguen los nombres internos ([glosario](../docs/glosario.md)).

Viene de jugar la versión 3 con amigos: los turnos y la interfaz confundían tanto que nadie usó las habilidades, la votación entre salas no aportaba, las flechas en pantalla eran feas y 20 s por ronda era demasiado.

## 1. De 2 a 5 jugadores

Salas, cola rápida, asientos, entradas, colores y listas admiten **hasta 5 jugadores**. El mínimo sigue siendo 2. La cola rápida forma grupos de 2 a 5 como hoy forma de 2 a 4. Cada héroe sigue siendo único en la partida.

## 2. Tamaño de la sala

| Jugadores | Lado | Centro `c` | Escalera y Celador | Entradas (fila `lado − 2`), por asiento |
|---|---|---|---|---|
| 2 a 4 | **9** | 4 | (4, 3) | (3,7) (5,7) (2,7) (6,7) |
| 5 | **11** | 5 | (5, 4) | (4,9) (6,9) (3,9) (7,9) (5,9) |

Regla general: `c = lado / 2`; escalera y Celador en `(c, c − 1)`; entradas en la fila `lado − 2`, en las columnas `c−1, c+1, c−2, c+2, c`. Todas las salas de una partida tienen el mismo lado, fijado al crearla.

**Nada supone 9.** Todo se mide con el lado de la sala:
- **Generación:** obstáculos en las filas `1 … lado − 3`. Con 11 hay más: 9 muros (6 con 9), 4 pinchos (3; en Santuario 8 en vez de 6), 2 braseros en la sala del Celador (igual) y los demás como antes. Los monstruos nacen en filas `≤ lado − 4` y a `≤ c` casillas de la escalera (4 con 9, 5 con 11).
- **Alcance:** el Ballestero y el huésped Ballestero de Polizón alcanzan todo el lado.
- **Corrimiento del Mampostero:** desplaza filas o columnas con módulo `lado`.
- **Inundación:** como mucho `lado` filas.
- **Cruz del Celador, visión, Flotar, Hilván** y toda validación de casillas: dentro de `0 … lado − 1`.
- **Hilván** empieza con su segundo cuerpo a 2 al este; si esa casilla es la entrada de otro asiento o está fuera, a 2 al oeste.
- **El tamaño viaja en la vista:** `view.room.size`. El cliente y los bots dibujan y razonan con él.

## 3. La dificultad escala con los jugadores

| Dato | 2 | 3 | 4 | 5 |
|---|---|---|---|---|
| Vida del Celador (por jugador) | 9 (18) | 12 (36) | 15 (60) | 15 (75) |
| Monstruos por sala (antes del tipo de sala) | los 2 primeros | todos | todos | todos **+1 Carroñero** en las salas 1 a 4 |
| Vida de los monstruos | la del catálogo | igual | igual | igual |
| Invocaciones de cada Tótem (tope) | 3 | 3 | 3 | **4** |

Los valores salen de la simulación con todas las combinaciones de héroes (§9). Viven en `Difficulty` (dominio). Las maldiciones (`FIERCE`, `WARDEN_PLUS`) se aplican encima, como antes.

## 4. La ronda: planear por fases, con el cursor

El motor no cambia la resolución: **primero todos los pasos y luego todas las acciones**, por iniciativa, como en 06-version-3 §1. Lo que cambia es cómo se arma la orden en pantalla. La orden en el cable sigue siendo `{ type, direction?, step? }`; el cliente traduce los clics.

**Fase 1, moverte.** Se resaltan las casillas vecinas a las que se puede caminar (N, S, E, O). Clic en una para dar el paso; clic en la propia casilla para quedarse. Se puede volver a esta fase en cualquier momento.

**Fase 2, actuar.** Botones grandes: **Atacar**, la habilidad del héroe **con su nombre**, **Usar**, **Empujar** y **Defender**. Al elegir uno se resaltan en el tablero los objetivos válidos **desde la casilla de llegada**, y un clic en uno lo fija:

| Acción | Qué se resalta | Qué se envía |
|---|---|---|
| Atacar | Las casillas en línea recta dentro del alcance, en las 4 direcciones, cortadas por muros. Enemigos y aliados se marcan aparte | `ATTACK`, con la dirección de la casilla elegida |
| Habilidad con dirección | Las casillas vecinas en las 4 direcciones (o las de su alcance: La Escucha hasta 4) | `ABILITY` con esa dirección |
| Habilidad sin dirección (Volver, Tensar, Repique, Préstamo…) | La casilla del héroe | `ABILITY`; la dirección la pone el cliente (norte) |
| Usar | Las casillas vecinas con algo que usar (cofre, altar, aliado derribado, brasero) | `INTERACT` |
| Empujar | Las casillas vecinas con algo que se pueda empujar | `PUSH` |
| Defender | Nada: se elige con el botón | `GUARD`, que **borra el paso** |

**Listo.** El botón **Listo** envía la orden. Hasta pulsarlo se puede cambiar todo. Si se acaba el tiempo con algo planeado, el cliente envía lo planeado; sin nada planeado, el héroe defiende (lo hace el servidor, como hoy).

**Muy visible:** el indicador «1 Moverte → 2 Actuar → Listo» con la fase actual, la ficha de la habilidad (qué hace, su espera), y un **resumen en lenguaje simple** del plan: «Te mueves al norte y atacas al Carroñero».

**Funciona igual en todos los estados:**
- **Fantasma:** fase 1 es Flotar (2 casillas en línea); fase 2: Frenar, Escudo, Revivir o Luz (Luz borra el paso, como Defender).
- **Traidor:** las mismas fases; sus límites no cambian.
- **Hilván:** la fase 1 resalta el destino de los dos cuerpos (el segundo en espejo); los objetivos se resaltan desde los dos.
- **Polizón dentro de un huésped:** la fase 1 mueve al huésped; Atacar sale desde él (con su alcance), Abordar/Salir desde la casilla de llegada.
- **La Escucha:** sus objetivos llegan hasta 4 casillas.
- **Campanero:** `LastWord` se muestra como resumen de lo que hicieron los demás; él planea igual, con su tiempo.
- **Errata:** Corregir resalta las 4 direcciones y explica qué anuncios a ≤2 se moverían.

**Teclado:** los atajos quedan como extra discreto (flechas para el paso, Q, E, F, Espacio, Enter para Listo). Todo se puede hacer **solo con el ratón o en táctil**.

## 5. Sin flechas

Se quitan la cruceta de direcciones y los botones de flecha de la interfaz.

## 6. Sin votación ni sabotaje

**Escalera.** Con la sala sin monstruos, un héroe activo en la escalera lleva a todos **directo** a la sala siguiente, al final de esa ronda. No hay interludio, ni El Juicio, ni puertas.

**Tipo de sala al azar.** Las salas 2, 3 y 4 tienen un tipo al azar (CSPRNG del servidor) entre Tesoro, Arena, Santuario, Oscura e Inundada, **distinto del tipo de la sala anterior**. Se sortea al crear la partida y **nadie lo sabe hasta entrar**: la vista solo trae la sala actual. La primera es normal y la última el Celador. El tipo se anuncia al entrar (evento `FLOOR_ENTERED` con `detail` = tipo) y se ve en `view.room`.

**Sabotaje:** desaparece. La orden `SABOTAGE` se rechaza con `INVALID_ORDER`.

**Lo que dependía de votos, condenas o sabotaje:**

| Qué | Antes | Ahora |
|---|---|---|
| Misión `BETRAY_TWICE_UNCONVICTED` | 2 traiciones sin condena | **Ya no se ofrece** (sin sabotaje casi nadie podía) |
| Misión `CORRECT_CONVICTION` | Acusar a un culpable | **Ya no se ofrece** |
| Título `MOST_TRIAL_GOLD_LOST` | Oro perdido en juicios | **Desaparece** |
| Título `MOST_BETRAYALS` | Más traiciones | Sigue: traiciones de Errata y Polizón |
| `view.allies[].convictions`, `view.me.canSabotage`, `view.interlude`, `view.lastVerdict` | | **Desaparecen** |
| Traiciones (Errata, Polizón) y fuego amigo | Se revelan al final | **Igual** |

Las misiones ofrecidas quedan en 5: `MOST_WARDEN_DAMAGE`, `OPEN_3_CHESTS`, `REVIVE_2_ALLIES`, `OUTGOLD_TARGET`, `PUSH_2_INTO_HAZARD`. Las dos opciones de cada jugador siguen siendo distintas entre sí.

## 7. Sin límite de rondas

La partida termina **solo cuando gana un bando**: cae el Celador, o no queda ningún héroe activo (derrota de todos, o «Ganan los Traidores» si hay un Traidor en pie), o se abandona. La meta de rondas por partida es solo informativa.

**El frío** evita una partida eterna. Desde la **ronda 80**, en la limpieza de cada ronda que sea múltiplo de 3, cada héroe activo **y cada Traidor presente** recibe daño de frío: **1, más 1 por cada 15 rondas pasadas de la 80** (rondas 81–94: 1; 95–109: 2; …). No lo reduce Defender ni el escudo, no tiene autor y no da oro a nadie. No afecta a Fantasmas, derribados ni muertos. Polizón dentro de un huésped lo recibe él. Al Avaro le cuesta oro, que **se pierde** (no cae al suelo). Evento `CHILLED` (en pantalla «El frío»), público, con el daño. `view.chillRound` dice desde qué ronda empieza.

**Por qué termina siempre.** El daño del frío crece sin tope y lo que se recupera por ronda está acotado (pociones, Calidez, drenaje, Hoja hambrienta, Volver, el oro que gana el Avaro al levantar a alguien), así que tarde o temprano ningún héroe queda activo y la partida acaba en derrota (o en «Ganan los Traidores», si a uno le queda vida). Además siguen: la expiración por inactividad del servidor (`InactivityTimeout`, 10 min sin órdenes) y el abandono cuando no queda nadie conectado. El simulador falla si una partida pasa de 1000 rondas: en 223 200 partidas la más larga duró 288.

## 8. El reloj

| | Antes | Ahora |
|---|---|---|
| Ronda | 20 s | **11 s** |
| Repique del Campanero (lo que les queda a los demás) | 8 s | **5 s** (8 × 11 / 20 = 4,4, hacia arriba) |
| Maldición `HASTE` (Prisa) | 12 s | **7 s** (12 × 11 / 20 = 6,6, hacia arriba) |

## 9. Números

Salen de la simulación (todas las combinaciones de 2 a 5 jugadores, 200 partidas cada una, semilla `20261102`); el detalle está en [docs/balance.md](../docs/balance.md).

| Dato | Versión 3 | Versión 4 |
|---|---|---|
| Vida del Celador | 6 × jugadores | **9, 12, 15 y 15 × jugadores** (2, 3, 4 y 5) |
| Monstruos con 5 jugadores | — | +1 Carroñero por sala; Tótem hasta 4 invocaciones |
| Hilván, vida | 8 | **9** |
| Polizón, vida | 6 | **7** |
| Avaro, oro al empezar | 4 | **5** |
| Usurera, interés | cada 7 rondas | **cada 10** (tope 3) |
| Límite de rondas | 60 | **ninguno**; el frío desde la 80 |

## 10. Contrato

Contrato **4.0.0** y `protocolVersion` **4** (la 3 se rechaza). Detalle en [Para el cliente](#para-el-cliente), en [`contracts/asyncapi.yaml`](../contracts/asyncapi.yaml) y en [ADR-0011](../docs/adr/0011-juego-v4-contrato-4.md).

## Aclaraciones

Decisiones tomadas donde el encargo no alcanzaba.

- **Tipo de sala distinto del anterior.** Como hacían las puertas; evita dos Inundadas seguidas.
- **El tipo se sortea al crear la partida**, no al pisar la escalera: es lo más simple (no hay que regenerar salas en mitad de una ronda) y nadie lo ve antes, porque la vista solo trae la sala actual. Vuelve a ser como en la versión 2: todas las salas nacen con la partida.
- **El frío.** El encargo pide que ninguna partida quede sin final sin poner un límite de rondas. Defender no lo reduce porque quien no envía nada defiende: si lo redujera, un grupo inactivo podría alargar la partida para siempre.
- **El frío también alcanza a los Traidores.** Sin límite de rondas, las partidas largas terminaban casi siempre con todos los héroes caídos y un Traidor en pie: «Ganan los Traidores» subía al 38 % en la simulación. Con el frío para todos queda en 20 %, bajo la meta.
- **El oro que el frío le quita al Avaro se pierde.** Si cayera al suelo, el Avaro lo recogía y no se apagaba nunca: fue la primera partida eterna que encontró el simulador.
- **Empieza en la ronda 80**, no antes: con la 70 había que bajar al Celador y los héroes caídos pasaban de la meta.
- **Límite de rondas solo para pruebas.** `Ascua:Realtime:TestRoundLimit` termina la partida en derrota al resolver esa ronda. Existe para que las pruebas de integración terminen una partida en pocas rondas; no se configura en ningún entorno y en el juego no hay límite.
- **Atajos de teclado.** Flechas (paso u objetivo), A, Q, E, F, Espacio, Enter y Esc. Sabotaje (R) ya no existe.
- **Listo sin plan** envía Defender. Con algo planeado y sin pulsar Listo, el cliente envía el plan 0,9 s antes del cierre.
- **Empujar** resalta solo la casilla vecina (antes la vista previa marcaba 2).
- **Repique y Prisa** se redondean hacia arriba para no dejar menos de 5 s a nadie.
- **Habilidad sin dirección.** El cable sigue pidiendo dirección para `ABILITY`; el cliente envía norte cuando no importa.
- **`SABOTAGE` y `MatchPhase.Interlude` en el código.** El valor del enum se conserva para leer partidas viejas del historial, pero se rechaza siempre.

## Para el cliente

Lo que cambia en el cable (detalle en [`contracts/asyncapi.yaml`](../contracts/asyncapi.yaml) y [ADR-0011](../docs/adr/0011-juego-v4-contrato-4.md)). En pantalla se dice oro, derribado, muerto, Fantasma, Traidor, mejora, misión secreta, ataque marcado, espera, Defender y Usar.

| Qué | Antes (3.0.0) | Ahora (4.0.0) |
|---|---|---|
| Conexión | `protocolVersion=3` | `protocolVersion=4` |
| Jugadores | 2 a 4 | **2 a 5**; `POST /api/v1/rooms { maxPlayers }` acepta 2 a 5 (por defecto 5) |
| Sala | 9 × 9 fijo | `view.room.size`: 9, u 11 con 5 jugadores. Casillas de 0 a `size − 1` |
| Orden | `SABOTAGE` posible | `MOVE`, `ATTACK`, `ABILITY`, `INTERACT`, `PUSH`, `GUARD`; `SABOTAGE` da `INVALID_PAYLOAD` |
| Escalera | `INTERLUDE` + `InterludeOffer`/`CastInterludeVote`/`InterludeResolved` | En el mismo `RoundResolved`: `delta.floorChanged` y `FLOOR_ENTERED { amount: sala, detail: tipo }` |
| Vista | `interlude`, `lastVerdict`, `me.canSabotage`, `allies[].convictions` | Se quitan; llega `chillRound` |
| Rondas | tope 60 | sin tope (`round` hasta 9999); evento `CHILLED { amount: daño }` |
| Reloj | 20 s; Repique 8 s; Prisa 12 s | **11 s; Repique 5 s; Prisa 7 s** |
| Títulos y misiones | `MOST_TRIAL_GOLD_LOST`, `BETRAY_TWICE_UNCONVICTED`, `CORRECT_CONVICTION` | Ya no salen (el historial viejo puede traerlos) |

**El plan por fases es del cliente.** Cada clic se traduce a la orden de siempre:

| En pantalla | Se envía |
|---|---|
| Fase 1, clic en una casilla vecina | `step` (o, sin acción, `MOVE` con esa dirección) |
| Fase 1, clic en el propio héroe | sin paso |
| Fase 2, botón y clic en un objetivo resaltado | `type` y la `direction` de esa casilla desde la de llegada |
| Habilidad sin dirección | `ABILITY` con `direction: "N"` |
| Defender | `GUARD`, sin paso |
| Listo sin nada planeado | `GUARD` |
| Se acaba el plazo con algo planeado | lo planeado, 0,9 s antes del cierre |

Los objetivos resaltados son una aproximación con lo que trae la vista; el servidor decide. La partida sigue aceptando una sola orden por ronda: el plan se puede cambiar hasta pulsar Listo, no después.
