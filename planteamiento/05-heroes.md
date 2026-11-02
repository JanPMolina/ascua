# Los nueve héroes

> **Estado:** aprobado por el equipo el 2-nov-2026; en implementación. **Reemplaza** a Yunque, Chispa, Cuervo y Raíz: esos héroes se borran del juego, del contrato y del código. Sustituye a [01-juego.md](01-juego.md) §9 y a las reglas de héroes de §14. Los números son punto de partida: el motor se balancea con simulación (§14 de este documento) y aquí queda el valor final.

## 1. La idea

**Cada héroe rompe una regla del juego.** No son clases con números distintos: cambian qué significa jugar. La sorpresa no viene de dados, sino de lo que otro jugador decidió en secreto, así que siempre se puede entender qué pasó y por qué.

Reglas de diseño:
- **Una orden, una dirección.** Toda habilidad usa las órdenes de hoy (`MOVE`, `ATTACK`, `ABILITY`, `INTERACT`, `GUARD`). No hay selección de objetivos: tienen que caber en 20 s.
- **Pasiva que define el personaje** y **activa que hay que preparar**: las mejores jugadas requieren crear la situación antes.
- **Sin nuevos dados.** El azar sigue siendo el de cofres y bendiciones.

## 2. Resumen

| Héroe (en el cable) | Rompe la regla | Vida | Alcance | Inic. | Enf. | Pasiva | Activa |
|---|---|---|---|---|---|---|---|
| **Errata** (`ERRATA`) | Los golpes caen donde se anunciaron | 4 | 1 | 6 | 2 | Fe de erratas | Corregir |
| **Anteayer** (`ANTEAYER`) | Lo hecho, hecho está | 4 | 2 | 5 | 5 | Eco | Volver |
| **Hilván** (`HILVAN`) | Tienes un solo cuerpo | 5 | 1 | 3 | 3 | Espejo | Tensar |
| **Polizón** (`POLIZON`) | Eres tu cuerpo | 3 | 1 | 7 | 3 | Sin casa | Abordar |
| **La Escucha** (`ESCUCHA`) | Ves lo que tienes cerca | 5 | 4 | 2 | 4 | Ciega | Grito |
| **La Usurera** (`USURERA`) | Ayudar es gratis | 4 | 1 | 1 | 2 | Interés | Préstamo |
| **El Avaro** (`AVARO`) | La vida y la puntuación son cosas distintas | ascuas | 1 | 2 | 2 | Todo lo que tengo | Soborno |
| **El Mampostero** (`MAMPOSTERO`) | La mazmorra está quieta | 6 | 1 | 1 | 3 | Conoce las piedras | Corrimiento |
| **El Campanero** (`CAMPANERO`) | El reloj es igual para todos | 5 | 2 | 4 | 4 | Último en hablar | Repique |

Todos hacen 1 de daño básico. Iniciativa: en la fase 3 actúa primero el número mayor; en empate, el asiento menor. El enfriamiento sigue la regla general: usada en la ronda N, disponible en N+C. **Una habilidad que no hace nada no gasta el enfriamiento.**

En una sala cada héroe es único (`HeroTaken`); la cola rápida asigna héroes distintos (`HeroAssignment`).

## 3. Orden de la ronda

Se agrega una fase y dos momentos nuevos; en negrita lo nuevo:

0. Se revelan los caminos elegidos.
1. `GUARDAR` y **Volver** (Anteayer).
2. **Corrimiento** (Mampostero).
3. Movimientos con rebote (**Espejo** de Hilván; **Polizón mueve a su huésped**).
4. Ataques, habilidades e interacciones por iniciativa; **el Eco de Anteayer actúa justo después de su dueño**.
5. Los enemigos ejecutan lo anunciado.
6. Los enemigos se mueven y anuncian (**salvo el huésped de Polizón**).
7. Disparadores y limpieza: **drenaje del huésped, interés de las deudas, ruido de la ronda**.

Las fases de 01-juego.md §3 conservan su contenido; solo cambia la numeración interna.

## 4. Errata

**Pasiva, Fe de erratas.** Ve todos los ataques anunciados de la sala, aunque estén en la niebla (solo las casillas, no quién los anuncia). Si un monstruo muere por un golpe que ella desvió, el golpe final es de ella.

**Activa, Corregir** (`ABILITY dir`, enf. 2). Cada enemigo que tenga **alguna** casilla anunciada a ≤2 de ella desplaza **todo** su anuncio 1 casilla en esa dirección. La casilla que caería en un muro o fuera de la sala se pierde. Sin anuncios en rango no hace nada.

**Un golpe desviado golpea todo lo que haya en sus casillas:** héroes activos, monstruos y Renegados, con el daño de quien lo anunció.

Casos:
- **Puede golpear a quien lo anunció.** El Celador recibe la mitad, redondeando hacia arriba, de su propio golpe desviado (ni su inmunidad frontal lo salva: es un golpe sin origen).
- **Anónimo:** si le cae a un aliado, el registro culpa al monstruo. No hay fuego amigo ni robo. Cuenta como **traición** (§13).
- **Un anuncio estorbado** por una Brasa se puede corregir; se ejecuta cuando le toque.
- **Renegada:** sus desvíos no dañan monstruos. Enfriamiento ×2.

## 5. Anteayer

**Pasiva, Eco.** Dos rondas después de cada `ATTACK`, una copia translúcida repite ese ataque **desde donde estabas y en esa dirección**, con tu daño y tu alcance actuales y las reglas normales (el fuego amigo es tuyo). El Eco es intangible y no se puede dañar. Mover, guardar o interactuar no dejan eco de ataque. La posición y la dirección del próximo Eco las ve quien tenga esa casilla en su visión.

**Activa, Volver** (`ABILITY`, la dirección no importa, enf. 5). En la fase 1 saltas a donde estabas hace 2 rondas y, si tenías más vida entonces, la recuperas. El escudo no vuelve. Si la casilla está ocupada, caes en la libre más cercana. Sin 2 rondas de historia en esta sala no hace nada.

Casos:
- **Caído o extinto:** los ecos ya programados **siguen disparando**. Un golpe final da sus ascuas (y si está extinto, van a sus ascuas guardadas). Sí: puede matar al Celador estando caído, y cuenta para el pacto.
- **Cambio de sala:** se borra la historia y los ecos pendientes.
- **Brasa:** como Brasa no deja ecos.
- **Renegado:** daño máximo 1, también en el Eco.

## 6. Hilván

**Pasiva, Espejo.** Dos cuerpos, una sola orden y una sola vida. Empiezan en la misma fila, a 2 casillas. `MOVE` norte y sur mueven a los dos igual; este y oeste los mueven al revés. Cada cuerpo rebota por su cuenta, así que un muro es la única forma de desalinearlos. Si los dos apuntan a la misma casilla, ninguno entra. `ATTACK` e `INTERACT` los usan **los dos**, también en espejo. `GUARDAR` protege a ambos. Ve con los dos cuerpos.

**Activa, Tensar** (`ABILITY`, la dirección no importa, enf. 3). Si los cuerpos están en la misma fila o columna, a ≥2 de distancia y sin muro entre ellos, todo lo que haya **entre** ellos recibe 2 de daño, aliados incluidos (fuego amigo normal). Si no se cumple, no hace nada.

Casos:
- **Un golpe a cada cuerpo son dos golpes** a la vida compartida. Trampas y fuego afectan a cada cuerpo. Si **cualquiera** cae en una grieta, Hilván se extingue.
- **Tensar al Celador** ignora su inmunidad frontal: lo cruza desde ambos lados.
- **Caído:** quedan los dos cuerpos en el tablero, y se le puede levantar desde cualquiera. Basta un cuerpo sobre la escalera.
- **Camino:** la Brasa es una sola, en el cuerpo principal. Al renacer o reaparecer, el segundo cuerpo aparece a 2 casillas en la misma fila, o en la libre más cercana.
- **Renegado:** Tensar hace como máximo 1.

## 7. Polizón

**Pasiva, Sin casa.** Tiene poca vida y nadie sabe dónde está cuando va dentro de un monstruo: desaparece de la vista de **todos**, aliados incluidos. Su huésped se ve como un monstruo cualquiera.

**Activa, Abordar** (`ABILITY dir`, enf. 3 desde que sale). Entra en el enemigo adyacente en esa dirección.
- **Dentro:** `MOVE` mueve al huésped a su velocidad. `ATTACK dir` hace que golpee **ya, sin anunciar**, con su daño y su alcance (el Ballestero dispara al primer objetivo en línea; los demás, a la casilla vecina). `ABILITY dir` sale a esa casilla, o a la libre más cercana, y deja al huésped sin actuar esa ronda. `GUARDAR` e `INTERACT` no hacen nada.
- **El huésped** no anuncia ni actúa por su cuenta, y los monstruos no atacan a Polizón.
- **Drenaje:** cada 2 rondas dentro, el huésped pierde 1 de vida y Polizón cura 1. Si el drenaje lo mata, es golpe final de Polizón.
- **Si otro mata al huésped,** Polizón sale en esa casilla y recibe 1 de daño; el golpe final es de quien lo mató.
- **Un héroe que golpea al huésped** no sabe a quién tiene dentro.
- **Si Polizón golpea a un aliado desde dentro,** el registro culpa al monstruo: sin fuego amigo ni robo, y cuenta como **traición** (§13).

**Celador y Tótem:** al Tótem no se entra. Al Celador tampoco: `Abordar` lo **gira 180°** hasta su próximo anuncio, sin moverse (enf. 6). Polizón tiene la iniciativa más alta, así que los aliados pueden golpearlo de espaldas en esa misma ronda. Su anuncio no cambia.

Casos:
- **La sala no se despeja** mientras haya un huésped vivo.
- **Sin casa y con vida 0** dentro no ocurre: si le llega daño, sale primero y cae afuera.
- **Renegado:** puede abordar. Sin drenaje; su huésped no daña monstruos y hace como máximo 1 a los héroes. Enfriamiento ×2.

## 8. La Escucha

**Pasiva, Ciega.** Conoce el terreno de la sala, pero de lo que hay encima solo percibe lo que está **a 1 casilla** y lo que **hizo ruido** en la última resolución, en cualquier parte de la sala: moverse, atacar, usar una habilidad, abrir un cofre, recibir un golpe. Lo que se quedó quieto no existe para ella. Las Brasas no hacen ruido. Un anuncio solo lo ve si cae sobre ella, a 1 casilla o es de un enemigo que oye.

**Ataque, A tientas** (`ATTACK dir`). Golpea lo primero que haya hasta 4 casillas en línea, amigo o enemigo (los muros lo detienen). Hace 1 de daño; **2 si el objetivo no hizo ruido**, porque no lo vio venir.

**Activa, Grito** (`ABILITY`, la dirección no importa, enf. 4). Hasta la próxima resolución, **todo su bando ve la sala entera**, y en esta ronda **todos los monstruos la eligen como objetivo**.

Casos:
- La Lámpara le da radio 2.
- **Renegada:** Grito revela la sala a los Renegados y no atrae a nadie. Daño máximo 1 en A tientas.
- **El huésped de Polizón** hace ruido como cualquier monstruo.

## 9. La Usurera

**Activa, Préstamo** (`ABILITY dir`, enf. 2). Al primer aliado en línea a ≤2 casillas, herido o caído: le cura 3, o lo levanta con 3 de vida. No pide permiso. Le crea una **deuda de 2 ascuas**, que se suma a la que tenga. A sí misma no se puede prestar. Un aliado con la vida llena no cuenta.

**Pasiva, Interés.**
- Cada 3 rondas con deuda, la deuda sube 1 (tope 6).
- **Cada ascua que gane el deudor le llega a ella** hasta saldar la deuda.
- **Si se extingue,** cobra de las ascuas guardadas lo que alcance.
- **Al terminar la partida, cobra lo que quede** de las ascuas del deudor, **antes** de decidir al ganador.

Casos:
- **La deuda sobrevive** a la Brasa y al Renegado. Las ascuas con las que se renace pagan.
- **Avaro:** no acepta préstamos (§10).
- **Polizón dentro de un huésped** no se puede encontrar, pero sus ascuas siguen pagando.
- **Levantar con Préstamo** no da las +2 ascuas de levantar con `INTERACT` ni cuenta para el pacto.
- **Renegada:** solo presta a Renegados; lo que ya le deben lo sigue cobrando.
- **Visibilidad:** la deuda de cada uno la ven el deudor y ella.

## 10. El Avaro

**Pasiva, Todo lo que tengo.** No tiene barra de vida: **su vida son sus ascuas**. Empieza con 3. Cada punto de daño, después de guardia y escudo, tira 1 ascua a una casilla (la suya) y el primero que la pise se la queda. Con 0 ascuas y otro golpe, cae.

**Activa, Soborno** (`ABILITY dir`, enf. 2). Paga **1 ascua** al primer enemigo en línea a ≤3 casillas: cancela su anuncio y esa ronda no actúa. No se puede pagar con la última. El Celador cuesta **3** y solo cancela el anuncio. El enemigo se queda con el pago y **lo suelta al morir**.

Casos:
- **Curarse no existe para él:** ni pociones ni Calidez ni Préstamo. Sus fuentes son ganar ascuas y recoger las del suelo.
- **Fuego amigo:** el atacante le roba 1; el resto cae al suelo.
- **Levantarlo:** vuelve con 2 ascuas (quien lo levanta gana las +2 de siempre).
- **Corazón doble:** +2 ascuas al elegirla. **Puño de piedra:** −1 ascua al elegirla. **Último aliento:** se queda con 1.
- **Extinción y caminos:** como cae con 0, sus ascuas guardadas son `⌈máximo que llegó a tener / 2⌉`. Con la fórmula del camino vuelve con al menos 2. Renacer no da vida aparte.
- **Ascuas en el suelo:** cualquiera las recoge al pisarlas, Renegados incluidos, y se pierden al cambiar de sala. Quien las recoge paga antes sus deudas.

## 11. El Mampostero

**Pasiva, Conoce las piedras.** Los pinchos y las grietas no le afectan.

**Activa, Corrimiento** (`ABILITY dir`, enf. 3). En la fase 2 desliza 1 casilla en esa dirección **su fila** (este/oeste) o **su columna** (norte/sur), con todo lo que tenga encima: terreno, cofres, fuego, ascuas en el suelo, héroes, caídos, enemigos y a él mismo. Lo que sale por un borde entra por el otro y, si es una criatura, **recibe 2 de daño**.

Casos:
- **Los anuncios no se mueven:** están marcados en el suelo. Se puede sacar a alguien del golpe o meterlo.
- **Todos ven** el corrimiento: la sala tiembla, y el evento dice qué línea se movió.
- **Las órdenes de movimiento** se resuelven después: alguien puede terminar donde no quería.
- **Daño a aliados** por el borde: fuego amigo normal. **Muerte de monstruo:** golpe final suyo.
- **Hilván** se mueve por cuerpo: un corrimiento lo puede desalinear.
- **La Brasa** es intangible y no se mueve.
- **Nunca deja la sala rota:** si la escalera o un héroe activo quedarían incomunicados, no se hace y no gasta el enfriamiento.
- **Renegado:** daño máximo 1 a los héroes. Enfriamiento ×2.

## 12. El Campanero

**Pasiva, Último en hablar.** Si todos los demás ya decidieron y él todavía no, recibe en privado **las órdenes de todos** (tipo y dirección) y puede responder con lo que quede de reloj. Como Renegado solo ve los tipos.

**Activa, Repique** (`ABILITY`, la dirección no importa, enf. 4). En la ronda siguiente, **todos los demás tienen solo 8 s**; él conserva los 20. Quien no decidió a tiempo guarda (o enciende el Faro). La ronda se cierra cuando él decide, o a los 20 s.

Casos:
- **Sigue siendo simultáneo:** su orden se resuelve con las demás; solo tiene más información.
- **Los demás pueden aguantar** y decidir al último segundo para negarle la información: de ahí sale el duelo.
- **Desconectados:** si alguien está en su periodo de gracia, no cuenta para «todos decidieron».
- **Bots y tutorial:** funciona igual.

## 13. Interacciones comunes

| Tema | Regla |
|---|---|
| **Traiciones anónimas** | El daño a un aliado por Corregir o desde un huésped se registra en secreto y **se revela en la pantalla final** («Errata le costó 3 de vida a Ana»). No da robo de ascuas |
| **Pacto «golpear a un aliado 2 veces sin que te descubran»** | Sin héroes en sigilo, cuentan los golpes por la espalda **y las traiciones anónimas** |
| **Brasa errante** | Todos usan las 5 órdenes de la Brasa; la clase vuelve al renacer. Se conservan deudas; se borran ecos de Anteayer, huésped y enfriamientos |
| **Renegado** | Vida, daño máximo 1 y enfriamiento ×2 según 04-caminos-tras-morir.md, con las notas de cada héroe. «Vida» del Avaro: sus ascuas |
| **Bendiciones** | Se mantienen las 12. La que mencionaba a un héroe viejo se reescribe; la poción cura 2 para todos |
| **Se eliminan** | Bloqueo frontal, Muralla, cargas, Descarga, sigilo, Paso sombrío, Brote y sus campos en el contrato. Las reglas de Brasa que los nombran se adaptan |

### Señales rápidas

Cualquier jugador puede enviar a su bando, durante la ronda, una frase fija («¡Aquí!», «¡Cuidado!», «Ayuda», «Es mía») y opcionalmente **marcar una casilla**. Es información libre: **también se puede mentir**.
- Hub: `SendSignal { matchId, kind, position? }`, hasta 1 cada 3 s por jugador (además del límite del hub).
- Llega solo a su bando (`SignalReceived`). Nunca revela nada del servidor: la casilla la eligió quien la envía. Validada contra el tablero.
- No se guarda en el registro de eventos ni afecta a la reproducción.

## 14. Balance: cómo se mide

**Herramienta:** un simulador (`tools/Ascua.Sim`) que juega partidas completas directo contra el motor, con **bots con heurística por héroe** que deciden **solo con su vista por jugador**: esquivan rojo, atacan en línea, abren cofres, van a la escalera y usan su habilidad cuando se da su situación. Con un parámetro de traición. Un bot al azar no sirve: nunca llega al Celador.

**Corridas:** todas las combinaciones de 2, 3 y 4 jugadores (126 cuartetos), con al menos 200 partidas por combinación y semillas fijas, para que sea reproducible.

| Métrica | Meta |
|---|---|
| Partidas en que cae el Celador | 35%–65% |
| Tasa de victoria por héroe (en 4 jugadores; azar = 25%) | Entre 18% y 32% |
| Ningún dúo dentro de un cuarteto | Más de 1,5 veces la tasa esperada |
| Rondas por partida | 25–55 de 60 |
| Héroes que terminan caídos o extintos al menos una vez | 30%–70% |
| Partidas con Renegado en que gana «El Umbral reclama» | Menos de 25% |

**Límite honesto:** los bots no mienten, no faroleán ni aprovechan a fondo a Errata, al Campanero o a La Escucha. La simulación detecta héroes rotos y bugs, pero no reemplaza partidas entre personas: los números finales van a necesitar ajuste jugando.

## 15. Lo que cambia en el contrato

Es un cambio incompatible, por lo que el contrato sube a **2.0.0** y `protocolVersion` a **2**: nuevos valores de `HeroClass` en REST y tiempo real, campos y eventos nuevos, y se quitan los de los héroes viejos.
