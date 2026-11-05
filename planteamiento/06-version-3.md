# Versión 3 del juego

> **Estado:** en implementación en la rama `feat/juego-v3`, que solo vive para jugar en red local. **No va a `main`.** Este documento **manda sobre** 01-juego.md, 04-caminos-tras-morir.md y 05-heroes.md: donde se contradigan, vale lo de aquí. Los números finales están en §12 y en [docs/balance.md](../docs/balance.md). En el código y el contrato siguen los nombres internos ([glosario](../docs/glosario.md)).

## 1. Mover y actuar en la misma ronda

**La orden.** Un paso opcional (`step`: N/S/E/O) y una acción opcional con dirección.

| Orden | `direction` | `step` |
|---|---|---|
| `MOVE` | Es el paso | No lleva |
| `ATTACK`, `ABILITY`, `INTERACT`, `SABOTAGE`, `PUSH` | Sí | Opcional |
| `GUARD` | No lleva | **No lleva:** quien defiende se planta |
| Sin orden | — | Vale como `GUARD` |

**Orden de la resolución** (los nombres entre paréntesis son el campo `phase` de los eventos, §10):

0. Se revelan los caminos elegidos (`REVEAL`).
1. Defender y Volver (`GUARD`).
2. Corrimiento (`SHIFT`).
3. **Todos los pasos**, con rebotes (`MOVEMENT`).
4. **Todas las acciones**, por iniciativa, desde donde quedó cada uno (`ACTIONS`). Si el paso rebotó, la acción sale de la casilla original.
5. Los enemigos ejecutan lo anunciado (`ENEMY_ATTACKS`).
6. Los enemigos se mueven y anuncian (`ENEMY_MOVES`).
7. Disparadores y limpieza (`CLEANUP`). Si se activa la escalera, abre el interludio.
8. Fin de partida (`END`).

**Casos**
- **Paso y mirada.** El paso gira al héroe hacia donde camina; la acción lo gira hacia su dirección. El sabotaje no lo gira (§2).
- **Pisar una grieta con el paso** mata igual que antes, y esa acción ya no ocurre. El Mampostero sigue siendo inmune.
- **Congelado** pierde la orden entera, paso incluido.
- **Una orden inválida** al resolver sigue siendo `GUARD`: se pierde también el paso.

**Héroes**
- **Hilván:** el paso va en espejo y cada cuerpo rebota por su cuenta. `ATTACK`, `INTERACT` y `PUSH` actúan desde los dos cuerpos, en espejo y desde donde quedó cada uno.
- **Polizón dentro de un huésped:** el paso mueve al huésped a su velocidad y luego `ATTACK` golpea desde la casilla nueva, o `ABILITY` sale desde ella. Fuera de un huésped puede dar el paso y abordar.
- **Fantasma:** Flotar (2 casillas) con `step`, más Estorbo, Escudo o Revivir. Luz (`GUARD`) no lleva paso. `MOVE` sigue siendo solo Flotar.
- **Traidor:** paso y acción como cualquiera; sus límites no cambian.
- **Anteayer:** el Eco repite el ataque desde donde estaba **al atacar**, ya con el paso dado. `Volver` ocurre en la fase 1 y el paso sale de la casilla de llegada.
- **Mampostero:** `Corrimiento` ocurre en la fase 2 y el paso sale de donde lo dejó la sala.
- **Campanero:** `LastWord` trae `step` además de tipo y dirección. Como Traidor sigue oyendo solo los tipos.
- **Errata, Escucha, Usurera, Avaro, Campanero:** su habilidad se mide desde donde quedaron tras el paso.

## 2. Sabotaje

`SABOTAGE dir`: **héroe activo, una vez por sala.** Hace 1 de daño anónimo al **primer aliado activo en línea a ≤2 casillas**. Muros y braseros cortan la línea; los enemigos no.

- **Nadie ve al autor:** el registro muestra el daño sin autor, igual que unos pinchos. El sabotaje no hace ruido, no gira al héroe y no da pista a La Escucha. El autor recibe un aviso privado (`BETRAYAL_RECORDED`).
- **No roba oro.** Al Avaro le tira 1 al suelo, como cualquier golpe.
- **Sin aliado en línea no pasa nada** y no se gasta. Si el aliado **defendía**, el golpe se anula, se gasta el uso y **no** cuenta como traición. Si lo absorbe un escudo, sí cuenta.
- **No pueden:** Fantasma, Traidor, derribados ni Polizón dentro de un huésped (ya tiene su golpe anónimo). La orden se rechaza con `INVALID_ORDER`.
- **Hilván:** se busca desde su cuerpo principal y luego desde el segundo, en espejo. Golpea a uno solo.
- **Es una traición:** se registra (quién, a quién, cuánto, qué sala, de qué tipo) y se revela al final, como las de Errata y Polizón. Las tres son juzgables.

## 3. El interludio y El Juicio

**Cuándo.** Al activarse la escalera de una sala que no sea la última (`InterludeOpened`). No corre el contador de rondas. Si esa misma ronda termina la partida (ronda 60), no hay interludio.

**Plazo.** Hasta 20 s (`RealtimeOptions.InterludeDuration`), durable como el draft: es el `deadline` de la partida en Postgres. Se cierra antes si ya votaron todos los votantes conectados. Sin ningún voto, se espera el plazo. Quien no votó vota «nadie».

**Quién vota.** Héroes activos, derribados y Fantasmas. **No votan:** Traidores, muertos con la elección de camino abierta ni retirados. A los desconectados no se les espera, **y al cerrar no cuentan como votantes** si no votaron.

**A quién se acusa.** A cualquier otro jugador no retirado (Traidores y muertos incluidos) o a nadie. Un voto por oferta, secreto; repetirlo igual devuelve el mismo ACK y cambiarlo da `VOTE_CLOSED`. Acusarse a uno mismo da `INVALID_VOTE`.

**Condena.** Para cada acusado: votos contra él > (votantes sin contarlo a él) / 2. Con 2 jugadores basta 1 voto. Con 4 votantes puede haber dos condenas a la vez; se resuelven por asiento.

| | Efecto |
|---|---|
| **Culpable** (tiene una traición sin juzgar en la sala que termina) | Pierde 3 de oro. Queda expuesto para todos, con sus traiciones de esa sala (a quién y cuánto). Esas traiciones pasan a juzgadas y ya no cuentan como ocultas. |
| **Inocente** | Cada votante que lo acusó pierde 1 de oro. Nadie sabe públicamente quién votó. |
| **Sin mayoría** | No pasa nada. Las traiciones de esa sala ya no se pueden juzgar: se revelan al final. |

**Al cerrar** todos reciben el recuento: votos por acusado, cuántos votaron «nadie», si hubo condena y qué se reveló, y los votos por puerta. **Quién votó qué nunca se revela.**

**Casos**
- **Oro perdido:** sale de `embers` y, si no alcanza, de las ascuas guardadas (Fantasma, muerto). Nunca baja de 0. No cae al suelo, no paga deudas ni va a nadie.
- **Avaro:** nunca baja de 1 por el Juicio, ni como condenado ni como acusador; tampoco si está derribado o es Fantasma. Sus 3 de oro son su vida, así que se le cobra lo que se pueda.
- **Traidor o muerto acusado:** pueden ser culpables si traicionaron en esa sala antes de morir. El Traidor no vota, pero sí paga.
- **Polizón dentro de un huésped:** vota y se le puede acusar; el voto no revela dónde está.
- **Retirarse durante el interludio:** su voto se descarta. Si ya no queda quien vote, se cierra por plazo.
- **Elección de camino:** el muerto puede elegir camino durante el interludio. El plazo sigue contando desde que abra la ronda siguiente.
- **El interludio no cura ni cuenta rondas de derribado:** al cerrarse siguen como estaban.

## 4. Puertas y salas con reglas propias

En el mismo interludio, **salvo si la sala siguiente es la del Celador**, se ofrecen **2 puertas** al azar (CSPRNG del servidor), distintas entre sí y de tipo distinto a la sala que termina. Gana la mayoría de votos; en empate o sin votos, al azar. La sala 1 es normal y la última sigue siendo el Celador.

**La sala se genera al elegir la puerta.** De la puerta no elegida solo se conoce el tipo; no existe nada más que se pueda filtrar.

| Tipo | Cofres | Monstruos | Regla |
|---|---|---|---|
| `TREASURE` (tesoro) | 4 | +1 Carroñero | — |
| `ARENA` | 0 | +1 o +2 Carroñeros (al azar) | Cada golpe final da **+2** de oro en vez de +1 |
| `SANCTUARY` (santuario) | 1 | Solo el primero de la lista | Un altar garantizado **que no pide llave** y +3 pinchos |
| `DARK` (oscura) | 3 | Los de la sala | Visión −1 para todos, mínimo 1 |
| `FLOODED` (inundada) | 2 | Los de la sala | Ver abajo |

**Inundada.** Desde el borde norte, cada 6 rondas en la sala se inunda una fila más (`floodedRows = rondas dentro / 6`). En la limpieza hace 1 de daño a quien no vuela: héroes activos y enemigos que no vuelan.
- No afecta a Fantasmas, Traidores (el entorno no los daña), derribados, Polillas ni a Polizón dentro de un huésped (el huésped sí recibe).
- **El Mampostero no es inmune:** el agua no es una piedra. Un Corrimiento no mueve el agua: se define por el número de fila.

**Público:** el tipo y el efecto se ven en la oferta y en `view.room` (`type`, `floodedRows`).

## 5. Oro

| Fuente | Oro |
|---|---|
| Golpe final a un enemigo | +1 (+2 en Arena) |
| **Asistencia** | +1 |
| Abrir un cofre | +1 |
| Levantar a un aliado | +2 |
| **Celador: reparto** | 6 según el daño que le hizo cada uno |
| **Celador: golpe final** | +1 (ya no +5) |
| **Misión cumplida** | +3 |
| Golpear a un aliado | +1 robada |

**Reparto del Celador.** Al morir se reparten 6 de oro en proporción al daño real que le hizo cada jugador (sin contar el exceso), con **mayores restos**; en empate de restos, menor asiento. Cuenta cualquier daño con autor: ataques, Eco, habilidades, golpes anónimos (Errata, huésped) y empujones no aplican porque no se le puede empujar. Fuego, quemaduras y trampas no tienen autor. Un retirado no cobra. Un Fantasma o un muerto suma a sus ascuas guardadas, como todo lo que ganan.

**Asistencia.** Cuando muere un enemigo en la fase de acciones, cada héroe que le dio un ataque esa ronda sin ser quien lo mató gana +1, y también quien después le lanza un ataque cuya línea llega a su casilla. Solo el primero cuenta como golpe final. Una vez por héroe y enemigo. Cuentan `ATTACK` y el Eco; no cuentan habilidades, huésped ni Traidor. La línea sigue: un ataque de alcance largo también golpea lo que haya detrás.

**Derrota colectiva.** Es derrota para todos en resultado e historial: `result = DEFEAT`, **nunca** `DRAW`. `DRAW` queda solo para el empate al caer el Celador.

## 6. Misiones

Cada jugador elige **1 entre 2**, en privado, con la mejora inicial (`PactOffer` / `ChoosePact`). Si vence el plazo, la primera. Recompensa **+3**. Se revelan al final; hasta entonces solo las ve su dueño. Ya no hay misiones pasivas.

| Misión (`pactId`) | Se cumple si | Nota |
|---|---|---|
| `MOST_WARDEN_DAMAGE` | Hiciste el mayor daño al Celador | Los empatados en el máximo la cumplen. Hace falta haberle hecho daño |
| `OPEN_3_CHESTS` | Abriste 3 cofres | |
| `REVIVE_2_ALLIES` | Levantaste a 2 aliados con Usar | Préstamo y Revivir no cuentan |
| `BETRAY_TWICE_UNCONVICTED` | 2 traiciones anónimas y **ninguna** condena | |
| `OUTGOLD_TARGET` | `target` termina con menos oro que tú | Se compara el oro final, sin misiones. El servidor elige el objetivo entre los demás al ofrecerla. Solo lo ve el dueño |
| `PUSH_2_INTO_HAZARD` | Empujaste a 2 enemigos a pinchos, fuego o grietas | Polilla sobre grieta no cuenta |
| `CORRECT_CONVICTION` | Acusaste a alguien que resultó condenado y culpable | |

El reparto evita que un jugador vea la misma misión dos veces: las 2 opciones salen seguidas de un mazo barajado.

## 7. Empujón

`PUSH dir`: **héroe activo y Traidor**, espera 3 (no se duplica para el Traidor; contador propio). Mueve 1 casilla en esa dirección a lo que esté **adyacente** en esa dirección, si la casilla de llegada se puede pisar y está libre. Se resuelve en la fase de acciones, por iniciativa. Es visible y con autor (`PUSHED`). No roba oro ni es traición.

- **No se empujan:** Celador, Tótem, Fantasma ni Polizón dentro de un huésped (se empuja a su huésped, y él va dentro).
- **No pueden empujar:** Fantasma, derribados ni Polizón dentro de un huésped. El Traidor solo empuja héroes: no puede dañar monstruos.
- **Sin efecto no se gasta:** nada adyacente, destino bloqueado o Celador.
- **Pinchos y fuego:** aplican su daño normal en la limpieza de esa ronda, sin autor.
- **Grieta y enemigo que no vuela:** muere; golpe final de quien empuja.
- **Grieta y héroe:** queda **derribado**, no muerto. Último aliento se aplica como en cualquier caída. El Mampostero no cae. Pisarla por su cuenta sigue matando.
- **Grieta y Traidor:** queda derrotado (+2 a quien lo empujó). El Traidor no tiene estado de derribado.
- **Derribado:** se le puede empujar; una grieta no le hace más.
- **Hilván empuja** desde sus dos cuerpos, en espejo, con un solo uso. **Si lo empujan,** se mueve ese cuerpo solo; una grieta lo derriba.
- **Oro en el suelo:** un héroe empujado lo recoge.
- **Los anuncios no se mueven:** un empujón puede sacar a alguien del golpe o meterlo.

## 8. Mejoras con precio

Cada mejora cambia una regla y tiene un costo que se ve. El catálogo (`GET /api/v1/catalog`, `BlessingCatalog` en el dominio) da nombre, efecto y costo. La única tirada de dados que queda en las mejoras es ninguna.

| Mejora (`blessingId`) | Efecto | Costo |
|---|---|---|
| Brasa viva (`BRASA_VIVA`) | Tus golpes siempre queman: 1 de daño la ronda siguiente | Recibes 1 de daño al abrir cofres |
| Paso ígneo (`PASO_IGNEO`) | Cada paso deja fuego en la casilla que dejas | El fuego te hace 1 más a ti |
| Puño de piedra (`PUNO_DE_PIEDRA`) | Si atacas sin dar un paso, haces +1 de daño | −1 de vida máxima (el Avaro, −1 de oro) |
| Corazón doble (`CORAZON_DOBLE`) | +2 de vida máxima (el Avaro, +1 de oro) | Nada te cura |
| Hoja hambrienta (`HOJA_HAMBRIENTA`) | Cada golpe final te cura 1 | No puedes defender |
| Espejo roto (`ESPEJO_ROTO`) | Todo golpe con autor que recibes le devuelve 1 de daño | El escudo no te protege: no puedes tener escudo |
| Lámpara (`LAMPARA`) | Ves toda la sala | Los monstruos te eligen siempre |
| Ganzúa (`GANZUA`) | Tus cofres dan +2 de oro | No puedes levantar a aliados con Usar |
| Último aliento (`ULTIMO_ALIENTO`) | Una vez: al caer quedas con 1 de vida | Si vuelves a caer, nadie puede levantarte |
| Codicia (`CODICIA`) | +1 de oro por cada golpe final | Cada golpe que recibes hace +1 |
| Escarcha (`ESCARCHA`) | Tus golpes congelan: pierde su próxima acción | Tus golpes no hacen daño (al Celador y a un Traidor, que no se congelan, sí) |
| Ojo de búho (`OJO_DE_BUHO`) | Ves todos los ataques marcados de la sala | Visión −1, mínimo 1 |

**Sinergias**
- **Infierno** = Brasa viva + Paso ígneo: el fuego no te daña y tu fuego dura 1 ronda más.
- **Coraza viva** = Espejo roto + Corazón doble: tu reflejo hace 2.
- **Saqueador** = Codicia + Ganzúa: ves todos los cofres de la sala y si traen llave.

**Casos**
- **Espejo roto:** no recibe escudo de nadie (cofres, Escudo del Fantasma, aviso del Traidor); el que tenía antes se pierde al elegirla. Con Traidores y Errata, un golpe anónimo no tiene autor visible y no refleja.
- **Hoja hambrienta:** su orden `GUARD` es válida, pero no reduce nada. El Avaro gana +1 de oro en vez de curarse.
- **Corazón doble:** pociones, Préstamo, Calidez, drenaje, Volver y Hoja hambrienta no le curan. El Préstamo sigue levantándolo si está derribado.
- **Ganzúa:** Préstamo y Revivir no usan Usar, así que sí levantan.
- **Lámpara:** La Escucha con Lámpara lo ve todo. El Grito tiene prioridad sobre la Lámpara. Con dos Lámparas, cada monstruo elige la más cercana. Dentro de un huésped no atrae a nadie.
- **Puño de piedra:** el Eco no suma el +1 (no se sabe si dio paso entonces). Un paso que rebotó cuenta como paso.
- **Escarcha:** aplica a ataques y Eco, no a habilidades ni al huésped.
- **Brasa viva:** la quemadura no tiene autor: no da golpe final.

## 9. Maldiciones

El anfitrión las elige al crear la sala (cero o más), públicas y guardadas con la partida.

| Maldición | Efecto |
|---|---|
| `FIERCE` | Monstruos +1 de vida (no el Celador; sí tótems, invocados y gelatinas divididas) |
| `WARDEN_PLUS` | Celador con 50 % más de vida, hacia arriba |
| `DIM` | Visión −1 para todos, mínimo 1; se suma a la sala Oscura y a Ojo de búho |
| `HASTE` | Rondas de 12 s (si la configuración ya las tiene más cortas, se conserva la corta) |

La cola rápida no lleva maldiciones. Se ven en la sala, en la vista (`view.curses`) y en el resultado.

## 10. Fin de partida, revancha y fases

**Títulos** (`MatchFinished.awards`, calculados en el dominio con datos reales). Solo se dan si alguien los ganó (valor > 0), con su motivo. Los empates comparten título.

| Título (`awardId`) | Para quién |
|---|---|
| `MOST_BETRAYALS` | Más traiciones |
| `MOST_WARDEN_DAMAGE` | Más daño al Celador |
| `MOST_REVIVES` | Más aliados levantados |
| `MOST_TRIAL_GOLD_LOST` | Más oro perdido en juicios |
| `RICHEST_DEATH` | Murió con más oro |
| `MOST_KILLS` | Más golpes finales |
| `MOST_CHESTS` | Más cofres |
| `MOST_PUSHES` | Más empujones |

**Revancha.** `POST /api/v1/matches/{matchId}/rematch`, solo para participantes de una partida terminada.
- Crea una **sala privada**, con las mismas maldiciones y tantas plazas como participantes. El id de la sala se deriva del de la partida, así que todos llegan a la misma sala con el mismo código, aunque la pidan a la vez.
- Quien la pide entra a la sala; el primero es el anfitrión. Los demás pueden entrar con el mismo endpoint o con el código. El héroe se vuelve a elegir.
- Si la sala de revancha ya no existe porque arrancó y venció, se crea otra. Si ya arrancó y sigue, responde con su partida (`matchId`).

**Fases en los eventos.** Cada evento visible lleva `phase` (§1), para mostrar la resolución paso a paso. Los eventos fuera de una ronda (`DraftOffered`, votos) no lo llevan.

## 11. Contrato

Contrato **3.0.0** y `protocolVersion` **3** (la 2 se rechaza). Detalle en §13 y en [`contracts/asyncapi.yaml`](../contracts/asyncapi.yaml).

## 12. Números

Se rellenan con la simulación al terminar: ver [docs/balance.md](../docs/balance.md).

## Aclaraciones

Decisiones tomadas donde el encargo no alcanzaba o donde lo pedido no se podía tal cual.

- **Quién cuenta para la mayoría.** El encargo dice que a los desconectados no se les espera. Si contaran como votantes, un desconectado podría impedir cualquier condena con 3 jugadores, así que los que están desconectados y no votaron salen de la cuenta al cerrar.
- **Empujón del Traidor.** Solo a héroes: un Traidor que tira monstruos a una grieta contradice «no puede dañar monstruos» de 04-caminos.
- **Sabotaje sin ruido.** Para que sea anónimo de verdad con La Escucha, que oye a quien actúa.
- **Asistencia con enemigos de varios golpes.** El encargo pensaba en dos ataques sobre un enemigo que muere. Con más vida también cuenta quien pegó **antes** del golpe final, porque su ataque también alcanzó.
- **Sala generada al elegir la puerta.** Antes todas las salas se generaban al crear la partida. Las siguientes se regeneran al elegir la puerta; la del Celador sigue generándose al crear.
- **Mejoras:** Lámpara y Ojo de búho conservan el nombre con otra regla. Se quitó todo porcentaje.
