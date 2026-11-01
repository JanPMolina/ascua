# Caminos tras la extinción: Amistad y Venganza

> **Estado:** aprobado el 31-oct-2026; implementado en el servidor (contrato AsyncAPI 1.3.0). Ver «Aclaraciones de implementación» al final. Amplía [01-juego.md](01-juego.md) §5 («Caer, levantar y abandonar»).

## Problema

Hoy, un héroe que se extingue (3 rondas caído sin que nadie lo levante) queda fuera: solo mira hasta el final. Con partidas de 10-15 min, puede pasar más de la mitad de la partida sin jugar.

## Resumen

Al **extinguirse**, el jugador elige uno de dos caminos:

| Camino | Qué es | Duración |
|---|---|---|
| **Amistad: Brasa errante** | Espíritu que ayuda al grupo y gana *ecos* | El resto de la sala; renace como héroe en la escalera al bajar de piso |
| **Venganza: Renegado** | Vuelve con su héroe, pero del lado de la mazmorra | Hasta que lo derroten |

Reglas comunes:
- **Plazo para elegir:** 15 s. Si no elige, se toma **Amistad**.
- **La elección es privada** hasta que se resuelve la ronda; ahí todos ven qué camino tomó.
- **Cada vez que se extinga se vuelve a elegir,** sin importar qué camino tomó antes. Por ejemplo: un Renegado derrotado puede elegir Amistad y volver al grupo en el piso siguiente.
- **Caer (0 de vida) no cambia:** los aliados pueden levantarlo durante 3 rondas. Solo la extinción abre la elección.
- **Al extinguirse ya no se pierde la mitad de las ascuas como hoy.** Pasan a ser **ascuas guardadas**, y cada camino decide cuántas recupera.

## 1. Camino de la Amistad: Brasa errante

### Cómo es
- **Intangible:** atraviesa muros, enemigos y héroes; nada le hace daño ni lo bloquea.
- **Visión:** radio 4 (los héroes tienen 3). La Lámpara suma +1.
- **Visible para el grupo** como una llama azul; los enemigos lo ignoran.
- **Una orden por ronda,** como los demás, con el mismo reloj de 20 s.

### Órdenes de la Brasa

Usan los mismos tipos de orden del contrato, con otro efecto:

| Orden | Habilidad | Efecto | Ecos |
|---|---|---|---|
| `MOVER(dir)` | **Deriva** | Avanza hasta **2** casillas en esa dirección, atravesando todo | — |
| `HABILIDAD(dir)` | **Avivar** | El primer aliado a ≤2 casillas en esa dirección recibe **+1 de escudo** (máx. 2 de escudo dado por brasas). Enfriamiento 1 | **+1 eco por cada punto de daño que ese escudo absorba** |
| `INTERACTUAR(dir)` | **Reavivar** | Levanta a un aliado **caído** adyacente con 1 de vida | **+3 ecos** |
| `ATACAR(dir)` | **Estorbo** | Un enemigo a ≤2 casillas en esa dirección **retrasa una ronda** su ataque anunciado. No funciona con el Celador en fase 2. Enfriamiento 2 | **+1 eco** si ese ataque iba a golpear a un héroe |
| `GUARDAR` | **Faro** | Ilumina un radio de 2 a su alrededor y comparte esa visión con todo el grupo esta ronda | **+1 eco** si un aliado da un golpe final dentro de esa zona |

Además, como pasiva, **Calidez:** un aliado que termina la ronda adyacente a la Brasa cura 1 cada 3 rondas, contadas por aliado. Da **+1 eco** por cada curación.

### Renacer

Cuando el grupo baja de piso, la Brasa renace **en la escalera** de la sala nueva:
- **Vida:** `⌈vidaMáxima / 2⌉`, la mitad de su vida máxima, redondeando hacia arriba.
- **Ascuas:** `⌈guardadas × (25% + 8% × ecos)⌉`, con un tope de **85%** de las guardadas. Las ascuas que haya ganado como héroe antes de extinguirse ya están en «guardadas».
- **Conserva** sus bendiciones y su pacto. Los enfriamientos y cargas empiezan en 0.

**Balance hacia el «≈80% si ayuda bastante».** Una sala dura unas 10-15 rondas. Una Brasa activa consigue unos 5-8 ecos:
- 0 ecos → 25% de las ascuas guardadas;
- 3 ecos → 49%;
- 7 ecos → **81%**;
- 8 o más ecos → 85% (tope).

Ejemplo con 10 ascuas guardadas:
- sin ayudar, renace con 3;
- ayudando mucho (7 ecos), renace con 9 (⌈8,1⌉).

**En la sala del Celador** no hay piso siguiente, así que la Brasa sigue como Brasa hasta el final. Al terminar la partida, sus ascuas se calculan con la misma fórmula y puede ganar.

## 2. Camino de la Venganza: Renegado

### Cómo es
- **Reaparece** al empezar la sala siguiente, en la casilla libre más lejana de los héroes (a ≥4 de todos).
- **En la sala del Celador** reaparece en la **misma pelea**, 2 rondas después, junto al Celador.
- **Conserva** su héroe, su habilidad, sus bendiciones y su pacto.
- **Lo ven** todos los que lo tengan en su visión, marcado como enemigo, con el aura roja del Renegado.
- **Da órdenes como cualquier jugador** (orden secreta y simultánea). Sus ataques se resuelven en la fase de ataques de héroes, por iniciativa.
- **Los monstruos no lo atacan** y él no puede dañarlos. **No puede** abrir cofres, usar altares ni la escalera.

### Balance (para que no aplaste al grupo)

| Medida | Sala normal | Sala del Celador |
|---|---|---|
| Vida con la que reaparece | 60% de su vida máxima (⌈⌉) | **40%** (⌈⌉) |
| Daño | Máximo 1 por golpe contra héroes (sin bonus de Puño de piedra ni de espalda) | Igual, **y no puede usar su habilidad las primeras 3 rondas** |
| Enfriamiento de la habilidad | **×2** (Chispa: máximo 2 cargas) | ×2 |
| Monstruos de la sala | **1 monstruo menos por Renegado** (mínimo 1) | El Celador pierde **6 de vida máxima por Renegado** (nunca menos de 12) y su fase 2 invoca **1 tótem** en lugar de 2 |
| Escudo de los héroes | Cuando aparece un Renegado, cada héroe vivo recibe **+1 de escudo** | Igual |
| Primer golpe | **El primer golpe del Renegado a cada héroe no hace daño** (lo absorbe el escudo de aviso) | Igual |

### Ascuas y victoria
- **Al reaparecer** conserva `⌈guardadas / 2⌉` de sus ascuas guardadas (lo mismo que se perdía hoy al extinguirse).
- **Gana ascuas** así:
  - **+1 robada** por cada golpe que conecta a un héroe (como el fuego amigo);
  - **+2** por dejar caído a un héroe.
- **Quien derrota a un Renegado** gana **+2 ascuas**. El Renegado se extingue de nuevo y vuelve a elegir camino.
- **Si cae el Celador,** gana quien tenga más ascuas entre los héroes no extintos **y los Renegados en pie**. Un Renegado puede ganar si fue el más eficaz.
- **Si todos los héroes quedan caídos o extintos,** termina con el nuevo resultado **«El Umbral reclama»**: ganan los Renegados en pie, y entre ellos el de más ascuas. Si no hay Renegados en pie, es la derrota colectiva de siempre.
- **Pactos:** se mantienen. Un Renegado puede cumplir el suyo y se revela al final como el de los demás.

## 3. Información oculta y seguridad

| Dato | Quién lo ve |
|---|---|
| Elección de camino (mientras se decide) | Solo quien elige, hasta la resolución |
| Orden de un Renegado | Solo su autor, como cualquier orden |
| Posición del Renegado | Quien lo tenga en su visión (como un enemigo) |
| Ecos de una Brasa | Su dueño. El grupo ve solo que «la Brasa ayudó» en los eventos |
| Ascuas guardadas | Su dueño |

**Validación en el servidor:** cada orden de una Brasa o de un Renegado se valida contra su estado; una orden no válida pasa a `GUARDAR` (o a Faro, para la Brasa). El dominio sigue siendo puro y determinista, y se agregan pruebas de propiedades para los dos caminos.

## 4. Cambios técnicos (resumen)

| Capa | Cambio |
|---|---|
| **Dominio** | Estados nuevos del héroe `EMBER_SPIRIT` y `RENEGADE`; ascuas guardadas y ecos; resolución de las órdenes de la Brasa; facción del Renegado; reglas de reaparición, balance y el resultado `THRESHOLD_CLAIMED` («El Umbral reclama») |
| **Contrato** (AsyncAPI) | Mensaje `PathOffer` (servidor → jugador) y método `ChoosePath { offerId, path: FRIENDSHIP \| VENGEANCE }`; estados y eventos nuevos (`PATH_CHOSEN`, `EMBER_REBORN`, `RENEGADE_SPAWNED`, `ECHO_GAINED`…). Versión 1.3.0, compatible hacia atrás |
| **Servidor** | `ChoosePath` en el hub, plazo durable de 15 s (como el draft) y vistas por jugador con las reglas de la tabla anterior |
| **Cliente** | Diálogo de elección con los dos caminos, dibujo de la Brasa (llama azul) y del Renegado (aura roja), HUD con ecos y ascuas guardadas, sonidos |
| **Docs** | 01-juego.md, guía «Cómo jugar», tutorial (un paso opcional) y modelo de amenazas |
| **Pruebas** | Fórmula de renacer, balance (monstruos y Celador), extinción repetida, no filtración de la elección ni de los ecos, y una partida completa con un Renegado |

## Aclaraciones de implementación

Decisiones tomadas al implementar el motor (`src/Ascua.Domain`, sobre todo `Rules/Paths.cs`) donde el texto era ambiguo. Se eligió siempre lo más simple. Si alguna cambia, hay que cambiar también sus pruebas (`tests/Ascua.Domain.Tests/PathTests.cs` y `PropertyTests.cs`).

**Elección**
- La oferta nace al resolverse la ronda en la que se extingue (evento privado `PathOffered`) y vale para la ronda siguiente. El plazo de 15 s cuenta desde que abre esa ronda y nunca pasa de su cierre. Mientras un jugador conectado tiene la elección abierta, la ronda no se cierra antes de tiempo (igual que con las órdenes).
- Vale la primera elección: no se puede cambiar dentro de la misma oferta. La elección se guarda en el héroe (privada) y se revela al **inicio** de la resolución de esa ronda (`PathChosen`, para todos). Si al resolver la oferta sigue sin respuesta, el propio `Resolver` toma Amistad.
- Si la partida termina en la misma resolución en que alguien se extingue, la oferta no llega a usarse.
- El plazo no necesita una columna nueva: es `plazo de la ronda − duración de la ronda + 15 s`, todo ya guardado en Postgres. Si Redis pierde el plazo, a más tardar el cierre de la ronda resuelve la oferta con Amistad.

**Brasa errante**
- Aparece en la casilla donde se extinguió y da órdenes desde la ronda siguiente a la revelación (en la ronda en que se revela no actúa). Tiene 0 de vida y no la afecta nada (ni monstruos, ni trampas, ni fuego).
- La orden por defecto (no envió nada, o la orden no vale) es `GUARDAR`, es decir, **Faro**.
- **Deriva:** 2 casillas, o menos si llega al borde de la sala. Puede terminar en la casilla de cualquiera.
- **Avivar:** el primer héroe activo en línea a 1 o 2 casillas. Si ese héroe ya tiene 2 puntos de escudo dados por brasas, no recibe más (igual gasta el enfriamiento). El escudo de brasas se consume **antes** que el resto; cada punto que absorbe da 1 eco a la Brasa que lo dio, si sigue siendo Brasa.
- **Reavivar:** la casilla adyacente en esa dirección. No da ascuas ni cuenta para el pacto «levantar a 2 aliados».
- **Estorbo:** el primer enemigo en línea a 1 o 2 casillas. Ese enemigo conserva su anuncio y esa ronda tampoco se mueve ni anuncia; lo ejecuta en la ronda siguiente. Sin enemigo, o contra el Celador en fase 2, no hace nada y no gasta el enfriamiento. El eco se da si, en el momento del Estorbo, alguna casilla anunciada tiene un héroe activo.
- **Enfriamientos** con la regla general del motor («usada en la ronda N, disponible en N+C»): Avivar 1 (se puede usar cada ronda) y Estorbo 2.
- **Faro:** ilumina el radio 2 alrededor de la Brasa desde el inicio de la ronda hasta la siguiente resolución. La vista que recibe el grupo al terminar la ronda incluye esas casillas; los Renegados no las ven. El eco es por cada golpe final de un héroe a un **enemigo** dentro de la zona en esa ronda.
- **Calidez:** al final de la ronda cura 1 a cada héroe activo **herido** a distancia ≤1 de la Brasa (incluida su misma casilla). El turno de cada aliado es de 3 rondas (cura en N, N+3, N+6…) y lo comparten todas las Brasas.
- **Renacer:** «en la escalera» se interpretó como **la entrada** de la sala nueva (las casillas de llegada del grupo, por asiento), no la escalera de salida, que queda junto a los enemigos. Renace con escudo 0, enfriamientos y cargas en 0, y conserva bendiciones, pacto y Último aliento. También renace al entrar a la sala del Celador. Si se revela Amistad en la misma ronda en que el grupo baja, renace en ese mismo momento.
- **Fin de partida:** sus ascuas cuentan con la fórmula de renacer. Las Brasas no cuentan como «héroes» para el fin: si todos los héroes quedan caídos o extintos, la partida termina aunque haya Brasas. Un extinto que todavía no eligió termina con ⌈guardadas / 2⌉ y no puede ganar.

**Renegado**
- Recibe ⌈guardadas / 2⌉ ascuas al **revelarse** el camino (no al aparecer); así puntúa aunque no llegue a aparecer.
- Aparece al entrar a la sala siguiente, en la casilla libre más lejana de los héroes (a ≥4 si se puede; si no, la más lejana; en empate, fila y columna menores; nunca en la escalera). Si se reveló en la sala del Celador, aparece al terminar la ronda R + 2 (R = ronda de la revelación) en la casilla libre más cercana al Celador.
- Al bajar de piso, los Renegados presentes también pasan y **aparecen otra vez** en la sala nueva con su vida actual. Cada aparición cuenta para todo el balance: un monstruo menos, +1 de escudo a cada héroe activo y, en la sala del Celador, 3 rondas sin habilidad y −6 de vida máxima del Celador.
- **Un monstruo menos:** se quitan los primeros de la lista de la sala, dejando al menos 1.
- **Celador:** cada aparición en su sala le quita 6 de vida máxima (nunca menos de 12) y lo mismo de vida actual (mínimo 1). Con algún Renegado en la partida (esperando o presente), la fase 2 invoca un solo tótem.
- **Daño:** máximo 1 por golpe contra héroes, aplicado antes de GUARDAR y de Codicia del objetivo; la Descarga de un Renegado hace 1; sin bonus por espalda ni de Puño de piedra. El enfriamiento ×2 se aplica al usar la habilidad (Yunque 6, Cuervo 8, Raíz 6); Chispa tiene como máximo 2 cargas.
- **Escudo de aviso:** el primer golpe de cada Renegado a cada héroe se anula (`HitPrevented` con `WarningShield`); la cuenta se reinicia cada vez que vuelve a extinguirse.
- **Ascuas:** +1 robada por cada golpe que conecta (si el héroe tiene ascuas) y +2 si lo deja caído.
- Los Renegados no se dañan entre sí. Su Muralla protege y su Brote cura solo a otros Renegados (si no hay, Brote lo cura a él).
- Los monstruos no lo atacan ni lo eligen como objetivo, y el entorno tampoco lo daña (trampas, fuego, quemaduras, congelación). Él no daña a los monstruos (el golpe se detiene sin efecto). No puede `INTERACTUAR` (cofres, altares ni levantar) y la escalera solo la activan héroes activos.
- Un héroe que golpea a un Renegado le hace el daño completo (no es fuego amigo ni roba). Al llegar a 0 el Renegado se extingue directamente (Último aliento no aplica), quien lo derrotó gana +2 y se abre una oferta nueva.
- Un Renegado en pie es elegible para ganar cuando cae el Celador (con sus ascuas más el pacto). En «El Umbral reclama» ganan los Renegados en pie con más `ascuas + pacto`; si empatan, ganan todos los empatados. En el historial cuenta como `VICTORY` (o `DRAW` si empatan) para ellos y `DEFEAT` para los demás.

**Información oculta**
- Los portadores del otro bando (Renegado frente al grupo, y al revés) solo se ven con posición dentro de la visión. Un Renegado ve a una Brasa solo dentro de su visión. La vida actual de cada portador sigue siendo pública, como antes.
- Lo que hace una Brasa usa la audiencia nueva `Group`: el grupo lo ve siempre y un Renegado solo si ve la casilla. `RenegadeSpawned` es para todos, pero se le quita la posición a quien no lo ve.
- La extinción ya no quita ascuas: el evento `HeroExtinguished` lleva `EmbersLost = 0` y las ascuas pasan a `SavedEmbers` (privado).
- Una Brasa, un extinto o un Renegado también pueden abandonar (`LeaveMatch` o gracia vencida): se retiran y pierden todo.
