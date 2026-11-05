# Balance

Resultados del simulador `tools/Ascua.Sim` contra las metas de [05-heroes.md §14](../planteamiento/05-heroes.md). No se movió ninguna meta.

## Versión 3

Reglas en [06-version-3.md](../planteamiento/06-version-3.md); valores finales en su §12.

**En resumen:** con 10 % de traición se cumplen las 6 metas. **El riesgo sigue siendo que entre personas sea fácil,** y no se pudo subir más la dificultad: el tope que manda es el de rondas, no el Celador. Las maldiciones existen para eso: `WARDEN_PLUS` y `FIERCE` son lo primero que hay que probar si el grupo arrasa.

| Métrica | Meta | Traición 10 % | ¿Cumple? | Traición 0 % | Traición 30 % |
|---|---|---|---|---|---|
| Partidas en que cae el Celador | 35–65 % | **49,1 %** (2 jug.: 44,2; 3: 47,1; 4: 51,7) | Sí | 60,5 % | **31,1 %** (bajo la meta) |
| Victoria por héroe (4 jugadores) | 18–32 % | **19,8–29,3 %** | Sí | 18,7–29,8 % | 19,5–31,1 % |
| Peor dúo | < 1,5× | **1,12×** en victoria, 1,07× en caída del Celador | Sí | 1,14× | 1,18× |
| Rondas por partida | 25–55 | **54,8** (2 jug.: 52,9; 3: 54,9; 4: 55,3) | Sí, por poco | 53,4 | **56,5** (sobre la meta) |
| Caídos o extintos | 30–70 % | **46,8 %** | Sí | **28,9 %** (bajo la meta) | 63,8 % |
| Umbral | < 25 % | **8,5 %** (de 10 287) | Sí | 5,4 % | 11,8 % |

**Método:** las 246 combinaciones de 2, 3 y 4 jugadores, 200 partidas por combinación (49 200 por corrida), semilla `20261102`. Los bots deciden solo con su `PlayerView` y lo que reciben por eventos. Para reproducirla:

```bash
dotnet run -c Release --project tools/Ascua.Sim -- --games 200 --seed 20261102 --betrayal 10
```

La meta se mide con 10 % de traición, como antes. Con 30 % falla por abajo y con 0 % los caídos quedan un punto bajo la meta: la traición ahora pesa de verdad, porque un sabotaje es 1 de daño seguro por sala. Un grupo que se sabotea mucho no llega. Es lo que se busca, pero a 30 % el Celador cae menos de lo que dice la meta.

### Antes y después

«Antes» es la versión 2, con sus bots (traición 10 %).

| Métrica | Versión 2 | Versión 3 |
|---|---|---|
| Celador cae | 45,5 % | 49,1 % |
| Con 2 jugadores | 27,8 % | 44,2 % |
| Victoria por héroe | 15,1–33,1 % | 19,8–29,3 % |
| Peor dúo | 1,26× | 1,12× |
| Rondas por partida | 55,9 | 54,8 |
| Llegan al límite de 60 | 50,2 % | 45,9 % |
| Rondas por sala | 7,5 / 8,6 / 10,9 / 13,0 / 17,7 | 9,0 / 10,3 / 11,4 / 12,0 / 16,2 |
| Caídos o extintos | 39,7 % | 46,8 % |
| Umbral | 6,6 % | 8,5 % |

### Por héroe (4 jugadores, traición 10 %)

| Héroe | Victoria | Celador cae con él | Cae o se extingue | Habilidad/partida | Golpes finales | Ascuas | Daño a aliados |
|---|---|---|---|---|---|---|---|
| Anteayer | 29,3 % | 50,7 % | 45,0 % | 2,0 | 3,8 | 8,3 | 2,5 |
| Campanero | 28,9 % | 50,6 % | 56,1 % | 0,5 | 4,1 | 8,7 | 3,1 |
| Errata | 27,2 % | 53,3 % | 54,7 % | 4,5 | 4,5 | 8,2 | 4,8 |
| Usurera | 27,1 % | 52,3 % | 67,8 % | 3,0 | 2,7 | 9,0 | 1,9 |
| Escucha | 26,8 % | 52,6 % | 46,9 % | 1,3 | 4,1 | 8,4 | 3,6 |
| Avaro | 25,5 % | 53,7 % | 13,9 % | 0,7 | 3,6 | 8,4 | 2,1 |
| Polizón | 20,3 % | 49,7 % | 45,3 % | 1,4 | 3,9 | 7,6 | 2,0 |
| Mampostero | 19,9 % | 51,3 % | 34,5 % | 2,4 | 3,6 | 7,7 | 2,1 |
| Hilván | 19,8 % | 51,3 % | 62,1 % | 0,1 | 4,0 | 7,3 | 2,1 |

**Por partida:** 4,17 sabotajes, 0,16 empujones, 3,72 interludios, 2,41 acusaciones, 0,50 condenas de culpables, 0,11 de inocentes, 0,88 misiones cumplidas. Las puertas se eligen casi por igual.

### Qué se cambió y por qué

| Cambio | Antes → ahora | Por qué, con el dato medido |
|---|---|---|
| Vida del Celador | 5 → 6 por jugador | Con el paso y la acción, los bots lo matan en 17 rondas si llegan. Con 7 por jugador, la media sube a 55,5 rondas: fuera de la meta |
| Polilla y Saltasapo | 1 → 2 | Son los monstruos que casi no alargan la sala: salen poco y vienen solos |
| Carroñero, Gelatina y Ballestero | Se quedan en 1 | Con 2 de vida cae el Celador en 13,5–34 % y la media llega a 56–58 rondas: dos de cada tres partidas se agotan antes del final |
| Polizón, vida | 4 → 6 | Por debajo de la meta en todas las pruebas (14–16 %). Llega a 20,3 % |
| Campanero, vida | 5 → 4 | Con alcance 2, el paso le rendía más que a nadie: 34,6 %. Baja a 28,9 % |
| Usurera, interés | Cada 5, tope 4 → cada 7, tope 3 | Con paso y acción cobraba aún más: 37–41 %. Baja a 27,1 % |
| Eco y asistencia | El Eco no cobra asistencia | Anteayer llegaba a 36 %. Baja a 29,3 % |
| Huésped y asistencia | El golpe desde un huésped sí cobra | Ayuda a Polizón |

**Bots.** Se les enseñó lo mismo que hará una persona, en este orden:
- Sumar acción a cada paso, y pegar o abrir lo que quede a mano aunque solo quisieran caminar.
- Salir de un golpe y actuar en la misma ronda, si se puede.
- Recordar dónde vieron al Celador. Antes, quien lo perdía de vista se iba a explorar: la mitad del grupo no peleaba.
- Pensar en el reloj: si van tarde, dejan los cofres y los altares.
- Sabotear con el parámetro de traición (una tirada por ronda; el triple si su misión lo pide), acusar a quien estaba en línea con quien recibió un golpe sin autor en la fase de acciones (70 %) y nunca si traicionaron ellos, y elegir puerta (Tesoro si su misión son los cofres; si no, al azar).

### Lo que no se pudo, y qué mirar

- **Monstruos.** Se pedía subir su vida con claridad. El reloj no lo deja: cada punto en un monstruo común cuesta 1–2 rondas por sala, y las partidas ya están en 54,8 de 55. Se subieron los dos que menos alargan.
- **El mayor riesgo sigue siendo que sea fácil.** Un sidestep con ataque al mismo monstruo no se puede hacer (al apartarte dejas de estar adyacente), pero personas coordinadas enfocan mejor que los bots: 16 rondas contra el Celador con 4 héroes siguen siendo muchas. Si caen demasiado rápido, primero las maldiciones; luego, `EnemyCatalog.WardenHpPerPlayer`.
- **Empujón:** 0,16 por partida. Los bots solo empujan a una grieta o, con su misión, a unos pinchos. Entre personas pesa más.
- **Hilván y Tensar:** 0,1 usos por partida; su dato subestima al héroe.
- **Las 12 mejoras** salen del reparto al azar y los bots casi no las aprovechan (salvo Escucha con Lámpara). El sim mide que no rompen el juego, no si son divertidas.
- **Mejoras fuertes sin medir aparte:** Brasa viva quema siempre y Escarcha congela siempre. Si dominan entre personas, ahí hay que mirar primero.

## Versión 2 (histórico)

**En resumen:** con los números originales el Celador no caía nunca. Con los finales cae en el 45,5 % de las partidas y se cumplen 4 de las 6 metas. Dos quedan fuera: la duración, por 0,9 rondas, y la victoria por héroe. **Estos números están calibrados contra bots que pierden muchas acciones, así que hay que confirmarlos jugando**: una partida entre personas puede resultar bastante más fácil.

### Metas contra resultado

Corrida principal con 10 % de traición. La última columna muestra cuánto se mueve el resultado con 0 % y 30 %.

| Métrica | Meta | Resultado | ¿Cumple? | Traición 0 % / 30 % |
|---|---|---|---|---|
| Partidas en que cae el Celador | 35–65 % | **45,5 %** | Sí | 47,6 % / 40,4 % |
| Victoria por héroe (4 jugadores; azar = 25 %) | 18–32 % | **15,1–33,1 %** | **No:** 5 de 9 en rango | 15,2–34,3 % / 13,4–33,1 % |
| Ningún dúo por encima de 1,5× lo esperado | < 1,5× | **1,26×** en victoria, 1,10× en caída del Celador | Sí | 1,27× / 1,22× en victoria |
| Rondas por partida | 25–55 de 60 | **55,9** | **No**, por 0,9 | 55,7 / 56,5 |
| Héroes caídos o extintos al menos una vez | 30–70 % | **39,7 %** | Sí | 37,6 % / 44,9 % |
| Partidas con Renegado en que gana «El Umbral reclama» | < 25 % | **6,6 %** (de 9765) | Sí | 6,5 % / 6,9 % |

**Método:** las 246 combinaciones de 2, 3 y 4 jugadores (36 + 84 + 126), 200 partidas por combinación (49 200 por corrida), semilla fija `20261102`. Los bots deciden solo con su `PlayerView`. Parámetros: venganza 30 % y Repique perdido 20 %. Para reproducirla:

```bash
dotnet run -c Release --project tools/Ascua.Sim -- --games 200 --seed 20261102 --betrayal 10
```

Cambia `--betrayal` a 0 o 30 para las otras dos corridas. La versión corta corre en CI dentro de `SimulatorTests`: comprueba que las partidas terminan, que cada héroe usa su habilidad y que la misma semilla da la misma partida. No comprueba las metas.

### Por número de jugadores

| Jugadores | Celador cae | Rondas | Caídos o extintos | Umbral | Llega al límite de rondas | Derrota colectiva |
|---|---|---|---|---|---|---|
| 2 | **27,8 %** (bajo la meta) | 55,7 | 44,8 % | 16,9 % | 55,7 % | 13,8 % |
| 3 | 43,1 % | 56,2 | 41,3 % | 9,3 % | 52,4 % | 2,5 % |
| 4 | 52,1 % | 55,9 | 38,1 % | 2,3 % | 47,1 % | 0,3 % |

La meta del Celador se cumple en el total, pero **no con 2 jugadores**. Se dejó así a propósito: bajar más al Celador solo para ese caso exigiría una fórmula distinta a «vida por jugador».

### Por héroe (4 jugadores, traición 10 %)

| Héroe | Victoria | Celador cae con él | Cae o se extingue | Habilidad/partida | Golpes finales | Ascuas | Daño a aliados |
|---|---|---|---|---|---|---|---|
| Usurera | **33,1 %** (alto) | 52,3 % | 51,3 % | 2,3 | 3,3 | 8,3 | 0,3 |
| Errata | **32,9 %** (alto) | 55,1 % | 42,4 % | 5,6 | 4,6 | 6,7 | 4,2 |
| Avaro | 28,8 % | 54,4 % | 10,5 % | 0,6 | 3,9 | 6,9 | 0,3 |
| Anteayer | 28,7 % | 50,9 % | 34,4 % | 2,0 | 4,0 | 6,6 | 0,6 |
| Campanero | 26,5 % | 51,7 % | 37,6 % | 0,3 | 4,4 | 6,9 | 1,0 |
| Escucha | 23,6 % | 52,1 % | 27,2 % | 2,5 | 4,1 | 6,3 | 1,4 |
| Mampostero | 19,4 % | 52,4 % | 25,9 % | 2,5 | 4,2 | 6,1 | 0,4 |
| Polizón | **15,8 %** (bajo) | 48,2 % | 55,8 % | 1,2 | 3,9 | 5,3 | 0,3 |
| Hilván | **15,1 %** (bajo) | 52,1 % | 57,8 % | 0,0 | 4,1 | 4,8 | 0,5 |

### Antes y después

«Antes» son los números originales de 05-heroes.md **con los bots ya mejorados** (7380 partidas, 30 por combinación, misma semilla). Por eso prueba que arreglar los bots no bastaba.

| Métrica | Antes | Después |
|---|---|---|
| Celador cae | 0,0 % | 45,5 % |
| Partidas que llegan al límite de 60 rondas | 95,5 % | 50,2 % |
| Rondas por partida | 59,3 | 55,9 |
| Rondas por sala (si se llega) | 13,3 / 15,3 / 19,0 / 13,8 / 5,7 | 7,5 / 8,6 / 10,9 / 13,0 / 17,7 |
| Caídos o extintos | 40,8 % | 39,7 % |
| Umbral | 1,2 % | 6,6 % |
| Victoria por héroe | Sin datos útiles: casi ninguna partida tenía ganador | 15,1–33,1 % |

Los caídos casi no cambian, así que el juego no se apagó para cumplir la tabla: sigue igual de peligroso, pero ahora se puede terminar.

### Qué se cambió y por qué

El orden fue el que se pidió: primero los bots, luego los números globales y al final los héroes.

#### 1. Bots (sin tocar el juego)

Solo usan lo que su jugador ve. Estos errores gastaban rondas:

| Error del bot | Arreglo |
|---|---|
| Rodear unos pinchos los hacía volver justo en la ronda mala, una y otra vez | Espera hasta 2 rondas ante un paso peligroso, o carga si aguanta el golpe |
| Bailaban frente a un Ballestero que no veían | Avanzan si les disparan desde donde no ven |
| Chocaban con cofres ya abiertos y con muros; recorrían lo ya visto | Memoria de cofres abiertos, choques y casillas vistas |
| Se desviaban a cualquier cofre | Solo a ≤3 casillas (≤6 si el pacto los pide) |
| Quien llegaba a la escalera se iba a pelear, y al caer el último monstruo nadie estaba encima | El primero sostiene la escalera mientras otros dos pelean |
| Contra el Celador golpeaban de frente, sin efecto | El más cercano esquiva, los demás lo golpean desde donde no los ve, y con ≤4 de vida ya nadie esquiva |
| El Avaro pagaba 3 ascuas para evitar un golpe de 2 | Solo soborna al Celador si con eso salva también a un aliado |

#### 2. Números globales

| Cambio | Antes → ahora | Por qué, con el dato medido |
|---|---|---|
| Vida del Celador | 12 → 5 por jugador | Las cuatro salas anteriores consumen unas 40 rondas de las 60. En la pelea final aislada, con 8 por jugador se tardaba unas 37 rondas; con 5 y bots enfocados, unas 26, y caía en 72 / 93 / 98,5 % (2/3/4 jugadores) |
| Golpe del Celador | Cada ronda → una sí y una no | Sin ventana no se le podía rodear. En la pelea aislada con 8 por jugador, pasó de caer en 0,4 / 3,9 / 12 % a caer en 55 / 80 / 90 % |
| Vida de monstruos | Carroñero, Gelatina y Ballestero 2 → 1; Tótem 3 → 2 | Cada sala tomaba de 13 a 19 rondas y no quedaba tiempo para el Celador |
| Tótem | Invoca cada 3 → cada 5 rondas | Las invocaciones alargaban la sala 4 |
| Monstruos por sala | Sala 3: 4 → 3; sala 4: 3 → 2; con 2 jugadores, los 2 primeros | Con 2 jugadores los mismos enemigos tardan el doble en caer |
| Escalera | (4,0) → (4,3); entradas de fila 8 a fila 7 | El recorrido por sala baja de 9 o 10 casillas a 5 o 6 |
| Dónde nacen los monstruos | Filas 1–4 → a ≤4 de la escalera, filas ≤5 | Sin rincones lejos que recorrer. El Celador pasó de 31,0 a 37,1 % con 3 jugadores y de 43,7 a 48,3 % con 4 |
| Castigo por Renegado | −6 con mínimo 12 → −3 con mínimo 5 | **Era un error:** con 2 jugadores, el piso de 12 le subía la vida máxima de 10 a 12 |

#### 3. Héroes (solo los que los datos mostraban rotos)

Las cifras «antes» salen de mediciones parciales de 30 partidas por combinación con 4 jugadores, antes de cerrar los números globales. Sirven para ver el orden de magnitud.

| Héroe | Cambio | Por qué | Victoria |
|---|---|---|---|
| Usurera | Deuda 2 → 1; interés cada 3 → 5; tope 6 → 4 | Ganaba de lejos, con 9,5 ascuas de media | 47,2 → 33,1 % |
| Errata | Enfriamiento 2 → 3 | Encima del rango, usando la habilidad 7 veces por partida | 37,1 → 32,9 % |
| Anteayer | Alcance 2 → 1 | Con los Ecos a distancia estaba muy arriba | 39,9 → 28,7 % |
| Avaro | Ascuas iniciales 3 → 4 | Su vida y su puntuación son lo mismo: con 3 casi no ganaba | 7,0 → 28,8 % |
| Hilván | Vida 5 → 8 | Cada golpe a un cuerpo cuenta, y caía en dos de cada tres partidas | 13,2 → 15,1 % |
| Polizón | Vida 3 → 4 | Con 3 de vida casi no ganaba | 8,8 → 15,8 % |

El alza del Avaro no viene solo de la ascua inicial: el mayor salto fue el arreglo del bot. Con ese arreglo y 6 ascuas iniciales llegó a 40,3 %, y por eso se quedó en 4.

El tope de vida del contrato sube de 8 a 10, porque Hilván con Corazón doble llega a 10.

### Lo que no se cumplió

No se movió ninguna meta.

- **Rondas por partida, 55,9 contra 55.** La media está limitada por el tope de 60 y la mitad de las partidas llega a él. Bajarla más exigía recortar otra vez al Celador o a los monstruos, y ya están muy recortados. Se prefirió no forzarla por una ronda.
- **Hilván (15,1 %).** Los bots casi nunca usan Tensar (0,0 por partida con un decimal). Exige alinear dos cuerpos que se mueven en espejo durante varias rondas, justo lo que un bot con reglas simples no planea, así que su resultado subestima al héroe. Las reglas de Tensar sí están probadas en `HilvanTests`. No se subió más su vida para compensar un límite del bot.
- **Polizón (15,8 %).** Usa Abordar 1,2 veces por partida. El bot no aprovecha ir escondido ni elegir bien al huésped, que es lo que hace raro al héroe.
- **Usurera (33,1 %) y Errata (32,9 %).** Superan el tope por 1,1 y 0,9 puntos. Se dejaron así para no quitarles lo que los define. La diferencia es pequeña y se mueve con el nivel de traición: Errata marca 34,3 / 32,9 / 30,4 % con 0 / 10 / 30 %.
- **Celador con 2 jugadores (27,8 %).** Ver la tabla por número de jugadores.

**Dúos:** ninguno pasa de 1,5×. El más alto es Errata + Usurera, con 1,26× en victoria, y son justo los dos héroes más altos.

### Límites del método

- **Los bots no faroleán.** No mienten con las señales, no aguantan la orden para negarle información al Campanero y no planean traiciones: solo las cometen al azar según `--betrayal`. El Campanero usa Repique 0,3 veces por partida. Todo lo que hace especial a Errata, al Campanero y a La Escucha entre personas queda fuera de estos datos.
- **El riesgo mayor es que el juego quede fácil.** Las metas se alcanzaron con bots que todavía pierden muchas acciones (unas 19 rondas para 20 de vida, cuando cuatro héroes podrían hacerlo en 5 o 6). Con un Celador de 5 por jugador y monstruos de 1 de vida, un grupo coordinado de personas podría arrasar.
- **Los números deben confirmarse jugando.** Si eso pasa, lo primero que se debe subir es la vida del Celador (`EnemyCatalog.WardenHpPerPlayer`) y después la de los monstruos, antes de tocar a un héroe.
- **«Victoria» es por puntuación:** ascuas más pacto, y solo cuando cae el Celador. Un héroe que junta ascuas puede ganar sin pelear mejor.
- **Mide el promedio entre combinaciones,** no una mesa concreta: un buen cuarteto entre personas puede comportarse muy distinto.
