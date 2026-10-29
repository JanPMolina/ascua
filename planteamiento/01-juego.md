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

- **Vida:** de 4 a 6 corazones, en enteros. **Escudo:** se consume primero.
- **Daño:** enteros. **Bloqueo:** probabilidad de anular un golpe, resuelta con un generador criptográficamente seguro en el servidor.
- **Orientación:** cada héroe mira hacia su última dirección. Importa para los ataques por la espalda y para la defensa frontal.
- **Visión:** radio 3 (niebla de guerra). Las posiciones de los **aliados** siempre se ven, salvo que estén en sigilo.
- **Fuego amigo: sí.** Golpear a otro portador le quita la mitad del daño y te da **1 ascua robada**. Traicionar es posible, pero arriesga la partida de todos.

## 5. Caer, levantar y abandonar

- **Vida a 0 → *Caído*.** El héroe no actúa y conserva sus ascuas.
- **Levantar:** un aliado adyacente usa `INTERACTUAR` para levantarlo con 2 de vida y gana **+2 ascuas**. Esto incentiva cooperar.
- **3 rondas caído sin que nadie lo levante → *Extinto*.** Queda fuera de la partida y **pierde la mitad de sus ascuas**.
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

## 7. Fin de partida

- **El Celador cae:** gana quien tenga **más ascuas** entre los no extintos. Si hay empate en el máximo, la partida termina en **empate** entre ellos.
- **Todos caídos o extintos, o el límite de rondas se agota:** **derrota colectiva**. Se registra como empate de tipo "el Umbral se apagó".
- El resultado es **inmutable** y la partida se puede **reproducir ronda a ronda** desde el log de eventos.

## 8. Información oculta (requisito de seguridad)

| Dato | Quién lo ve |
|---|---|
| Orden de la ronda en curso | Solo su autor, hasta la resolución |
| **Pacto secreto**, uno por jugador, asignado al azar | Solo su dueño; se revela al final |
| Opciones del draft de bendiciones (1 entre 2) | Solo el que elige |
| Bendiciones obtenidas | Su dueño; los demás ven solo cuántas tiene |
| Contenido de un cofre | Solo quien lo abre |
| Casillas fuera de tu visión | Nadie las recibe, ni siquiera "ocultas" en el JSON |
| Héroe en sigilo | Su dueño; para los demás, su posición desaparece |
| Semilla del generador | Nadie; nunca sale del servidor |

**Ejemplos de pactos:** "dar el golpe final al Celador", "terminar sin haber caído", "abrir 3 cofres", "levantar a 2 aliados", "golpear a un aliado al menos 2 veces sin que te descubran".

## 9. Héroes (4)

| Héroe | Rol | Vida | Alcance | Pasiva | Habilidad |
|---|---|---|---|---|---|
| **Yunque** | Tanque | 6 | 1 | 25% de bloqueo frontal | **Muralla** (enfriamiento 3): esta ronda, los aliados detrás de él (en línea) no reciben daño |
| **Chispa** | Daño a distancia | 4 | 3 en línea | +1 carga por cada ronda sin atacar (máx. 3) | **Descarga:** gasta todas las cargas y golpea en **cruz** desde sí con 1+cargas de daño |
| **Cuervo** | Pícaro | 4 | 1 | Daño ×2 por la espalda | **Paso sombrío** (enfriamiento 4): se mueve 2 casillas y queda en **sigilo** 2 rondas, invisible para enemigos y jugadores |
| **Raíz** | Soporte | 5 | 1 | Los objetos de curación curan +1 | **Brote** (enfriamiento 3): cura 2 a un aliado a ≤2 casillas, **o a sí mismo** |

## 10. Bendiciones (12) y sinergias (3)

Se consiguen en el **draft inicial** (1 entre 2, privado) y en los **altares**, que se abren con la llave de plata que sueltan los cofres.

| Bendición | Disparador | Efecto | Coste |
|---|---|---|---|
| Brasa viva | al atacar | 20% de quemar (1 de daño por ronda, 2 rondas) | — |
| Paso ígneo | al moverse | 15% de dejar fuego en la casilla anterior | — |
| Puño de piedra | pasivo | +1 de daño | −1 de vida máxima |
| Corazón doble | pasivo | +2 de vida máxima | −10% de bloqueo |
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
| **Tótem** | Inmóvil; invoca 1 carroñero cada 3 rondas (máx. 3) y luego se apaga |

**El Celador** (jefe, sala 5), en 2 fases:
- **Fase 1:** inmune a los golpes **de frente**. Anuncia un 3×3 delante de él, así que hay que flanquearlo.
- **Fase 2** (≤50% de vida): invoca 2 tótems y alterna el 3×3 con una **cruz completa** de la sala.
- Su vida escala con el número de jugadores: `12 × jugadores`.

## 12. Trampas y entorno

- **Pinchos:** se activan cada 2 rondas y afectan a todos los que no vuelan.
- **Brasero:** si un ataque lo vuelca, prende fuego en cruz.
- **Grieta:** caer en ella es *Extinción* inmediata para quien no vuela.

## 13. Por qué este diseño sirve para el servidor

- **Turnos simultáneos:** hay concurrencia real (muchas órdenes por ronda), un cierre por temporizador y una acción por defecto. Cubre los objetivos 2 y 4 del enunciado.
- **Mucha información oculta:** pactos, draft, cofres, niebla y sigilo. Permite pruebas de "no filtración" muy concretas.
- **Resolución pura y determinista** (estado + órdenes + generador → nuevo estado + eventos). Es ideal para pruebas unitarias y para reproducir partidas.
- **Bots triviales:** basta con elegir una orden válida al azar, lo que facilita las pruebas de carga con cientos de partidas.
- **Originalidad:** no hay tiempo real, no se ataca chocando, la puntuación es individual, hay pactos secretos y fuego amigo con robo. **No es una recreación de Guidus.**
