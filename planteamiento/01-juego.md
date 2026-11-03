# El juego: **Ascua** (nombre provisional)

> Mazmorra semi-cooperativa por **turnos simultáneos** para 2-4 jugadores. Está inspirada en los *patrones* de Guidus (ver [../inspiracion](../inspiracion/README.md)), pero las reglas son propias: sin tiempo real, sin chocar para atacar y con puntuación individual y objetivos secretos.

## 1. Premisa

Una hoguera antigua, el **Umbral**, se está apagando. De 2 a 4 portadores bajan a la cripta para matar al **Celador**, que se alimenta de ella. Si nadie lo derrota, todos pierden. Si cae, solo **un** portador puede reclamar el fuego: el que haya reunido más **ascuas**.

**Tensión central:** hay que cooperar para sobrevivir, pero también competir por el botín, por el golpe final y por los objetivos secretos.

## 2. Formato de partida

| Parámetro | Valor (configurable) |
|---|---|
| Jugadores | 2-4 humanos; empieza con ≥2 listos |
| Duración objetivo | 10-15 min |
| Estructura | 4 salas + sala del Celador (5 "pisos") |
| Sala | Cuadrícula de 9×9 con muros, trampas, cofres, enemigos y una escalera |
| Ronda | **Planificación** (20 s) → **resolución** en el servidor → difusión |
| Límite | 60 rondas; si se agotan, el Umbral se apaga y todos pierden |

## 3. La ronda (turno simultáneo)

1. **Planificación.** Cada jugador envía en secreto **una orden** para la ronda `N`. Los demás solo ven "Jugador X ya decidió".
2. **Cierre.** La ronda se cierra cuando todos los jugadores vivos enviaron su orden o cuando vence el temporizador. A quien no envió nada se le aplica la **acción por defecto: `GUARDAR`**.
3. **Resolución determinista** en el servidor, en orden fijo:
   1. `GUARDAR` y escudos se activan.
   2. **Movimientos:** si dos personajes van a la misma casilla, **ninguno se mueve** (rebote). Tampoco se puede entrar en una casilla ocupada.
   3. **Ataques y habilidades** de los héroes, ordenados por iniciativa (atributo del héroe y, en empate, orden de asiento).
   4. **Los enemigos ejecutan** los ataques que **anunciaron en la ronda anterior**.
   5. **Los enemigos se mueven y anuncian** las casillas que golpearán en la ronda `N+1` (el *telegraph*). El anuncio es visible para quien tenga esas casillas en su visión.
   6. Disparadores (bendiciones, trampas, muertes, botín) y limpieza.
4. **Difusión.** Cada jugador recibe **su propia vista** del nuevo estado, con número de secuencia.

> La esquiva por reflejos de Guidus se convierte aquí en **anticipación**: ves dónde caerá el golpe y decides si te apartas, guardas o arriesgas un ataque.

### Órdenes posibles

| Orden | Efecto |
|---|---|
| `MOVER(dir)` | 1 casilla en N/S/E/O |
| `ATACAR(dir)` | Ataque básico según el alcance del héroe |
| `HABILIDAD(dir)` | Habilidad del héroe; consume recurso o tiene enfriamiento |
| `INTERACTUAR(dir)` | Abrir un cofre, levantar a un aliado caído o activar un altar (siempre adyacente) |
| `GUARDAR` | Reduce a la mitad el daño recibido en la ronda; acción por defecto |

## 4. Atributos y combate

- **Vida:** de 4 a 8 corazones, en enteros (el Avaro no tiene: su vida son sus ascuas). **Escudo:** se consume primero.
- **Daño:** enteros.
- **Orientación:** cada héroe mira hacia su última dirección. Importa para los ataques por la espalda.
- **Visión:** radio 3 (niebla de guerra; radio 1 para La Escucha). Las posiciones de los **aliados** siempre se ven, salvo Polizón dentro de un huésped.
- **Fuego amigo: sí.** Golpear a otro portador le quita la mitad del daño y te da **1 ascua robada**. Traicionar es posible, pero arriesga la partida de todos.

## 5. Caer, levantar y abandonar

- **Vida a 0 → *Caído*.** El héroe no actúa y conserva sus ascuas.
- **Levantar:** un aliado adyacente usa `INTERACTUAR` para levantarlo con 2 de vida y gana **+2 ascuas**. Esto incentiva cooperar.
- **3 rondas caído sin que nadie lo levante → *Extinto*.** Sus ascuas pasan a ser **ascuas guardadas** y elige camino: **Amistad** (Brasa errante que ayuda al grupo y renace al bajar de piso) o **Venganza** (Renegado del lado de la mazmorra). Cada extinción vuelve a abrir la elección. Reglas completas en [04-caminos-tras-morir.md](04-caminos-tras-morir.md).
- **Desconexión:** periodo de gracia de **60 s** (configurable). Durante la gracia, el servidor aplica `GUARDAR` en cada ronda.
- **Abandono:** si la gracia vence, el héroe se retira y pierde todas sus ascuas. Si quedan menos de 1 humano activo, la partida termina como *abandonada*.

## 6. Ascuas (puntuación individual)

| Fuente | Ascuas |
|---|---|
| Golpe final a un enemigo | +1 |
| Abrir un cofre | +1 |
| Levantar a un aliado | +2 |
| Golpe final al Celador | +5 |
| Pacto secreto cumplido (se revela al final) | +4 |
| Golpear a un aliado | +1 robada al aliado |
| Renacer como Brasa errante | ⌈guardadas × (25% + 8% × ecos)⌉, tope 85% ([04](04-caminos-tras-morir.md)) |
| Volver como Renegado | ⌈guardadas / 2⌉; +1 robada por golpe a un héroe, +2 por dejarlo caído |
| Derrotar a un Renegado | +2 |

## 7. Fin de partida

- **El Celador cae:** gana quien tenga **más ascuas** entre los no extintos, las Brasas errantes y los Renegados en pie. Si hay empate en el máximo, la partida termina en **empate** entre ellos.
- **Todos los héroes caídos o extintos con algún Renegado en pie:** **«El Umbral reclama»** (`THRESHOLD_CLAIMED`): ganan los Renegados en pie, el de más ascuas (ver [04-caminos-tras-morir.md](04-caminos-tras-morir.md)).
- **Todos caídos o extintos sin Renegados en pie, o el límite de rondas se agota:** **derrota colectiva**. Se registra como empate de tipo "el Umbral se apagó".
- El resultado es **inmutable** y la partida se puede **reproducir ronda a ronda** desde el log de eventos.

## 8. Información oculta (requisito de seguridad)

| Dato | Quién lo ve |
|---|---|
| Orden de la ronda en curso | Solo su autor, hasta la resolución |
| **Pacto secreto**, uno por jugador, asignado al azar | Solo su dueño; se revela al final |
| Opciones del draft de bendiciones (1 entre 2) | Solo el que elige |
| Elección de camino, ecos y ascuas guardadas ([04](04-caminos-tras-morir.md)) | Solo su dueño (el camino se revela al resolver la ronda) |
| Bendiciones obtenidas | Su dueño; los demás ven solo cuántas tiene |
| Contenido de un cofre | Solo quien lo abre |
| Casillas fuera de tu visión | Nadie las recibe, ni siquiera "ocultas" en el JSON |
| Polizón dentro de un huésped | Nadie ve dónde está, ni sus aliados |
| Deuda con la Usurera | El deudor y ella |
| Traiciones anónimas (Corregir, huésped) | Nadie hasta la pantalla final |
| Semilla del generador | Nadie; nunca sale del servidor |

**Ejemplos de pactos:** "dar el golpe final al Celador", "terminar sin haber caído", "abrir 3 cofres", "levantar a 2 aliados", "golpear a un aliado al menos 2 veces sin que te descubran".

## 9. Héroes (9)

Cada héroe rompe una regla del juego. Las reglas exactas están en [05-heroes.md](05-heroes.md), que manda sobre este resumen.

| Héroe | Rompe la regla | Vida | Alcance | Pasiva | Habilidad |
|---|---|---|---|---|---|
| **Errata** | Los golpes caen donde se anunciaron | 4 | 1 | Ve todos los anuncios de la sala | **Corregir:** desplaza los anuncios cercanos 1 casilla |
| **Anteayer** | Lo hecho, hecho está | 4 | 1 | Sus ataques se repiten 2 rondas después | **Volver:** salta a donde estaba hace 2 rondas |
| **Hilván** | Tienes un solo cuerpo | 8 | 1 | Dos cuerpos en espejo, una sola vida | **Tensar:** daña lo que haya entre sus cuerpos |
| **Polizón** | Eres tu cuerpo | 4 | 1 | Dentro de un huésped nadie lo ve | **Abordar:** entra en un enemigo adyacente |
| **La Escucha** | Ves lo que tienes cerca | 5 | 4 | Solo percibe su radio y lo que hace ruido | **Grito:** revela la sala a su bando |
| **La Usurera** | Ayudar es gratis | 4 | 1 | Cobra deudas con interés | **Préstamo:** cura o levanta y deja una deuda |
| **El Avaro** | La vida y la puntuación son cosas distintas | ascuas | 1 | Su vida son sus ascuas | **Soborno:** paga para cancelar un anuncio |
| **El Mampostero** | La mazmorra está quieta | 6 | 1 | Conoce las piedras | **Corrimiento:** desliza una fila o columna |
| **El Campanero** | El reloj es igual para todos | 5 | 2 | Oye las órdenes de todos si decide último | **Repique:** deja a los demás con 8 s |

## 10. Bendiciones (12) y sinergias (3)

Se consiguen en el **draft inicial** (1 entre 2, privado) y en los **altares**, que se abren con la llave de plata que sueltan los cofres.

| Bendición | Disparador | Efecto | Coste |
|---|---|---|---|
| Brasa viva | al atacar | 20% de quemar (1 de daño por ronda, 2 rondas) | — |
| Paso ígneo | al moverse | 15% de dejar fuego en la casilla anterior | — |
| Puño de piedra | pasivo | +1 de daño | −1 de vida máxima |
| Corazón doble | pasivo | +2 de vida máxima | — |
| Hoja hambrienta | al matar | cura 1 | — |
| Espejo roto | al recibir un golpe | 25% de devolver 1 de daño | — |
| Lámpara | pasivo | +1 de visión | — |
| Ganzúa | al interactuar | los cofres sueltan +1 ascua | — |
| Último aliento | al caer | 1 vez: queda con 1 de vida | — |
| Codicia | pasivo | +1 ascua por golpe final | recibe +1 de daño |
| Escarcha | al atacar | 20% de congelar (pierde su siguiente acción; no afecta al Celador) | — |
| Ojo de búho | pasivo | ve los anuncios de ataque fuera de su visión | — |

**Sinergias (recetas):**
- **Brasa viva + Paso ígneo = Infierno:** todo el fuego dura 1 ronda más y hace +1.
- **Espejo roto + Corazón doble = Coraza viva:** el reflejo pasa a 50%.
- **Codicia + Ganzúa = Saqueador:** tus cofres revelan si contienen llave de plata *antes* de abrirlos. Esta información también es solo tuya.

## 11. Enemigos (6) y jefe

| Enemigo | Regla única |
|---|---|
| **Carroñero** | Básico: se acerca y golpea en una casilla adyacente |
| **Polilla** | Vuela: ignora trampas de suelo y abismos; 1 de vida |
| **Gelatina** | Al morir se divide en 2 gelatinas de 1 de vida |
| **Saltasapo** | Se mueve 2 casillas por ronda |
| **Ballestero** | Anuncia una **línea completa** y dispara la ronda siguiente |
| **Tótem** | Inmóvil; invoca 1 carroñero cada 5 rondas (máx. 3) y luego se apaga |

**El Celador** (jefe, sala 5), en 2 fases:
- **Fase 1:** inmune a los golpes **de frente**. Anuncia un 3×3 delante de él, así que hay que flanquearlo. Golpea una ronda sí y una no: entre golpes no se mueve, no se gira ni anuncia.
- **Fase 2** (≤50% de vida): invoca 2 tótems y alterna el 3×3 con una **cruz completa** de la sala.
- Su vida escala con el número de jugadores: `5 × jugadores`.

## 12. Trampas y entorno

- **Pinchos:** se activan cada 2 rondas y afectan a todos los que no vuelan.
- **Brasero:** si un ataque lo vuelca, prende fuego en cruz.
- **Grieta:** caer en ella es *Extinción* inmediata para quien no vuela.

## 13. Por qué este diseño sirve para el servidor

- **Turnos simultáneos:** hay concurrencia real (muchas órdenes por ronda), un cierre por temporizador y una acción por defecto. Cubre los objetivos 2 y 4 del enunciado.
- **Mucha información oculta:** pactos, draft, cofres, niebla, huéspedes y traiciones anónimas. Permite pruebas de "no filtración" muy concretas.
- **Resolución pura y determinista** (estado + órdenes + generador → nuevo estado + eventos). Es ideal para pruebas unitarias y para reproducir partidas.
- **Bots triviales:** basta con elegir una orden válida al azar, lo que facilita las pruebas de carga con cientos de partidas.
- **Originalidad:** no hay tiempo real, no se ataca chocando, la puntuación es individual, hay pactos secretos y fuego amigo con robo. **No es una recreación de Guidus.**

## 14. Aclaraciones de implementación

Decisiones tomadas al implementar el motor (`src/Ascua.Domain`) donde el texto era ambiguo o no daba números. Se eligió siempre lo más simple. Si alguna cambia, hay que cambiar también sus pruebas en `tests/Ascua.Domain.Tests`.

**Tablero y visión**
- La sala completa (9×9) es jugable; los muros son casillas interiores. Fuera de la sala cuenta como muro.
- Distancias, alcances y visión usan **distancia Manhattan**. La visión es individual (no se comparte con aliados) y no la tapan los muros.
- La escalera está en (4,3) y solo funciona con la sala **sin enemigos**: si un héroe activo termina la ronda sobre ella, todos los héroes no extintos pasan a la sala siguiente (los caídos siguen caídos).
- Cofres, altares y braseros bloquean el paso. Las grietas se pueden pisar (y extinguen). Los pinchos se activan en las rondas pares.
- Generación: cada sala recibe muros, pinchos, braseros, grietas (salas 2-3), altares (salas 2 y 4) y 2 cofres al azar; se valida con BFS que toda casilla transitable y cada objeto sean alcanzables desde la entrada (si falla 50 veces, sala sin obstáculos). Enemigos fijos por sala (con 2 jugadores, solo los 2 primeros); nacen a ≤4 casillas de la escalera, en filas ≤5. El Celador empieza en (4,3) y las entradas están en la fila 7.

**Números que el documento no fijaba**
- Daño base de todos los héroes: 1. Iniciativa: ver [05-heroes.md](05-heroes.md) §2 (empate por asiento).
- Enemigos (vida/daño): Carroñero 1/1, Polilla 1/1, Gelatina 1/1, Saltasapo 1/1, Ballestero 1/2, Tótem 2/–, Celador `5 × jugadores`/2.
- Contenido de un cofre: siempre +1 ascua, más una de {nada, poción que cura 2, +1 de escudo} y 50% de llave de plata. El escudo solo sale de los cofres.
- Fuego (Paso ígneo, braseros) y quemadura (Brasa viva): 1 de daño durante 2 rondas. Con **Infierno**, el fuego y las quemaduras **de ese héroe** duran 3 rondas y hacen 2.

**Combate**
- Orden del daño a un héroe: fuego amigo (½, redondeo hacia arriba) → GUARDAR (½, redondeo hacia abajo) → Codicia (+1 si queda daño) → escudo → vida (el Avaro, ascuas). GUARDAR anula los golpes de 1.
- "De frente" / "por la espalda": se mira la dirección dominante desde el objetivo hacia el atacante; frente = hacia donde mira, espalda = la contraria. Aplica a la inmunidad del Celador en fase 1 y al pacto de golpear aliados. Los golpes del entorno (trampas, fuego) no tienen origen.
- Ataques: alcance 1 golpea la casilla adyacente; con más alcance (Campanero 2, La Escucha 4) golpea al **primer** objetivo en línea. Muros y braseros detienen el ataque; volcar un brasero prende fuego en cruz. Los caídos no detienen proyectiles.
- Enfriamiento C: usada en la ronda N, vuelve a estar disponible en la ronda N+C.
- Congelado: pierde su siguiente acción. Un enemigo congelado en la fase 3 pierde la ejecución (fase 4) **y** el movimiento/anuncio (fase 5) de esa misma ronda. Un héroe congelado pierde la orden de la ronda siguiente (ni siquiera guarda).
- Gelatina: solo la original se divide; las pequeñas aparecen en su casilla y la libre más cercana.
- Celador en fase 2: al cruzar el 50% invoca 2 tótems junto a él y su primer anuncio es la cruz completa (su fila y su columna); luego alterna con el 3×3, cuyo centro está 2 casillas delante de él. Se acerca solo si el objetivo está a más de 2 casillas. Tras cada golpe descansa una ronda.
- IA de los enemigos: persiguen al héroe activo más cercano por el camino más corto (BFS), rodeando lo que estorba. Ignoran a los caídos y a Polizón dentro de un huésped.
- Tótem: invoca un carroñero en las rondas 5, 10 y 15 desde que está activo y después desaparece.

**Ascuas, caídas y fin**
- Extinguirse (3 rondas completas caído, o grieta) ya no quita ascuas: pasan a ser ascuas guardadas y se elige camino (detalles e implementación en [04-caminos-tras-morir.md](04-caminos-tras-morir.md)).
- El robo por fuego amigo solo ocurre si el golpe conecta y la víctima tiene ascuas.
- Pacto "golpear a un aliado 2 veces sin que te descubran": cuenta los golpes a un aliado **por la espalda** y las traiciones anónimas.
- Los pactos se revelan siempre al terminar; el +4 solo decide el ganador cuando cae el Celador. Si el Celador cae, gana el máximo de `ascuas + pacto` entre los héroes no extintos ni retirados.
- Al llegar a la ronda 60 sin vencer al Celador, la partida termina al resolverse esa ronda.
- Abandono: el héroe retirado pierde todas sus ascuas; la partida queda *abandonada* cuando no queda ningún héroe activo ni caído.

**Draft y validación**
- El draft inicial (2 opciones privadas por jugador) y el de los altares se resuelven con `MatchRules.ChooseBlessing`; `AutoChooseBlessings` elige la primera opción si vence el plazo. La partida pasa a `InProgress` cuando todos eligieron.
- Una orden inválida que llegue al resolver se trata como `GUARDAR`. Códigos de error estables: `INVALID_ORDER`, `NOT_A_PARTICIPANT`, `PLAYER_DOWN`, `PLAYER_OUT`, `STALE_ROUND`, `MATCH_NOT_IN_PROGRESS`, `SKILL_ON_COOLDOWN`, `INVALID_DRAFT_CHOICE`, `NO_PATH_OFFER`, `INVALID_PATH_CHOICE`, `ORDER_TOO_LATE`.

**Información oculta**
- Cada evento lleva su audiencia (todos, un jugador o quien vea la casilla). `PlayerViewProjector.VisibleEvents` filtra con la visión del estado ya resuelto, recorta los anuncios a las casillas visibles y oculta al autor de los golpes anónimos.
- De los aliados se ve la vida actual pero no la máxima (delataría bendiciones). Los cambios de ascuas son eventos privados, pero el total de cada aliado es público en la vista.
- El dominio no guarda ninguna semilla: el azar llega por `IRandom`. `SeededRandom` (SplitMix64) existe solo para pruebas, bots y reproducciones.
