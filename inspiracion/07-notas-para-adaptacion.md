# Notas para adaptar Guidus al enunciado

> No es el planteamiento final. Son observaciones para la siguiente fase, en la que cruzamos lo investigado con las restricciones de [Proyecto3.pdf](../Proyecto3.pdf).

## Choques entre Guidus y el enunciado

| Guidus | Enunciado | Implicación |
|---|---|---|
| Tiempo real, se juega por reflejos | **Por turnos** (o turnos simultáneos) | La esquiva por reflejos desaparece. La tensión tiene que venir de **anticipar**: ataques telegrafiados con un turno de antelación |
| Un solo jugador contra la IA | **2 o más humanos** | Hay que definir la relación entre jugadores: cooperativo contra la mazmorra, competitivo, o ambos (semi-cooperativo) |
| Azar resuelto en el cliente | **Azar y relojes en el servidor**, con un generador criptográficamente seguro | Bendiciones, cofres, botín y bloqueos se resuelven en el servidor y quedan en el registro de jugadas para reproducirlos |
| Progresión de meta-juego larga (*grind*) | No se evalúa; el alcance es de 2 semanas | Conviene dejar fuera piedras, artefactos y gacha, o reducirlos al mínimo |
| ~27 héroes, más de 100 bendiciones | El juego no tiene nota | Basta con **3-4 héroes y 10-20 bendiciones** para demostrar el motor de reglas |
| Reproducción exacta de reglas | **Prohibido copiar 1 a 1** | Se toman **patrones** (moverse para atacar, recurso cargable, bendición con coste, sinergias por receta, enemigos con una sola regla), no las listas concretas |

## Lo que encaja muy bien

1. **Cuadrícula y movimiento en 4 direcciones.** Las jugadas son intenciones pequeñas y fáciles de validar: `move(dir)`, `skill(dir)`, `interact(dir)`. Encaja con el modelo autoritativo y con validar cada mensaje contra su esquema.
2. **Reglas deterministas a partir de una semilla.** Si cada partida guarda la semilla del generador y la secuencia de jugadas, se puede **reproducir jugada por jugada**, como exige el enunciado.
3. **Información oculta natural:**
   - Las bendiciones de cada jugador, o la elección privada de 1 entre 2.
   - La **niebla de guerra** y el radio de visión que el juego ya tiene.
   - El contenido de los cofres o de un templo antes de abrirlo.
   - En modo competitivo, las órdenes secretas de turno simultáneo.
   Con esto se demuestra la "vista por jugador" sin filtraciones.
4. **Temporizador por turno con acción por defecto.** Si alguien no juega a tiempo, la acción por defecto es "esperar" o "defender".
5. **Abandono y reconexión.** El héroe de quien abandona puede pasar a la IA, quedarse quieto o caer, según la regla del juego.
6. **Disparadores como eventos de dominio** (al moverse, al atacar, al matar, al recibir un golpe, al entrar a una sala). Es una arquitectura limpia para un módulo de dominio puro.
7. **Enemigos con una sola regla y ataques telegrafiados.** Los turnos los traducen de forma directa: "el enemigo anuncia en el turno N las casillas que golpeará en el N+1".

## Ideas de formato (para discutir)

- **A. Cooperativo por turnos simultáneos.** De 2 a 4 héroes en una mazmorra corta (3-5 salas y un jefe). Cada ronda todos envían su orden en secreto, el servidor resuelve la ronda y luego actúan los enemigos. Concurrencia real con turnos simultáneos.
- **B. Competitivo en la misma mazmorra.** Dos o más jugadores se disputan llaves, templos y bendiciones. Gana quien derrota al jefe, o quien más puntos acumula. Información oculta fuerte.
- **C. Semi-cooperativo.** Cooperan contra la mazmorra, pero solo uno se lleva la "corona" final. Cada jugador tiene bendiciones secretas y puede traicionar al resto.

## Riesgos que hay que vigilar

- **Originalidad:** hay que cambiar nombres, héroes, bendiciones y, sobre todo, el núcleo de reglas. El juego no puede ser "Guidus por turnos".
- **Alcance:** el juego es la excusa. Si las reglas crecen demasiado, se come el tiempo del servidor, Azure, la seguridad y las pruebas de carga, que es lo que se evalúa (unos 95 de los 100 puntos).
- **Pruebas de carga:** las reglas tienen que permitir **bots** simples (moverse o atacar al azar entre jugadas válidas) para simular muchas partidas a la vez.
