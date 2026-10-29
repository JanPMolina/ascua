# Monstruos y jefes

Los enemigos tienen **pocas reglas y muy legibles**. Cada uno añade una sola idea de comportamiento. Las zonas posteriores reutilizan enemigos con otro color y más vida.

## Calabozo

| Enemigo | Regla de comportamiento |
|---|---|
| Ghoul | Enemigo básico, sin habilidades |
| Murciélago | Poca vida, en grupos; **vuela**: ignora abismos y trampas de suelo |
| Hechicero | Dispara un proyectil **lento en línea recta**; +1 de daño |
| Slime | Al morir se **divide en 2** slimes pequeños |
| Rana | Se mueve **2 casillas** por turno en lugar de 1 |
| Piedra invocadora | Inmóvil; invoca enemigos adyacentes hasta un **tope**, después queda inerte |

**Jefes del calabozo** (sale 1 de 4 al azar):
- **Rey Slime**: ataque en nova a las 8 casillas; al morir se divide en 4. Hay que matarlos a todos.
- **Rey Fantasma**: **invulnerable**. Aparece un **núcleo** en un lugar aleatorio y golpearlo daña al rey. El núcleo se rompe con un golpe y reaparece en otro sitio, y el rey se teletransporta junto al jugador.
- **Rey Rana**: como la rana, pero llena el mapa de **pantanos**.
- **Liche**: como el hechicero, y además **invoca 2 ghouls**.

## Pasajes (compartidos por las 3 rutas)
- Versiones más fuertes: Ghoul azul, Murciélago vampiro, Hechicero rojo, Rana roja, Slime de sangre (se divide en 3).
- **Esqueleto**: **bloquea siempre el primer golpe**.
- **Jefe: Comandante Caballero Negro.** Inmune a los golpes **de frente**. Ataca en 3×3 delante de él con mucho tiempo de preparación y recuperación, así que se gana **rodeándolo por el lado o la espalda**.

## Centros de ruta
- **Alcantarilla**: Ghoul venenoso, Flor del abismo (torreta), estatua invocadora. **Jefe: Gorgona** (ataque de 2 casillas de alcance, petrifica).
- **Jardín subterráneo**: Ghoul del jardín, Flor del abismo, **Druida**, **Gólem**. **Jefe: Guardián Antiguo**, con mano izquierda y mano derecha como partes separadas.
- **Campo de entrenamiento**: Ghoul de hielo, **Espada maldita** (si te golpea, **invierte izquierda y derecha**), Caballero negro. **Jefe: Caballero del Vacío**.

## Palacio
- **Caballero rojo**: defensa frontal.
- **Gárgola**: vuela sobre los huecos y bloquea el primer golpe.
- **Alto hechicero**.
- **Caballero real**: un esqueleto mejorado que ataca rápido.
- **Mago real**: una flor que se mueve.
- **Jefe "???"** en 3 fases:
  1. Dos **Comandantes Caballero Blanco** que **comparten la vida**: matar a uno basta.
  2. El rey baja del trono.
  3. Fase final con patrones variados.

## Abismo
- Versiones corruptas de enemigos anteriores: esqueleto sin preparación, gárgola oscura y otros.
- **Elemental**: gólem y murciélago a la vez.
- **Observador**: hechicero y murciélago a la vez.
- **Guía del Abismo**: un druida mejorado que **cura por completo a toda la sala y da un escudo** de un golpe.
- **Jaula de la muerte**: parpadea y explota, también si la matas.
- **Tánatos**: ataca en 3×3 y **no vuela**, así que le afectan las trampas y las caídas.
- **Destructor de almas**: alcance de 2.
- **Eldritch**: invoca y dispara.
- **Jefes:**
  - **Faetón, el Coloso Antiguo**: hay que destruir sus manos para dañar el cuerpo, y las regenera cada tercio de su vida.
  - **Rey del Abismo**, destructor del palacio.

## Patrones de diseño útiles (traducibles a turnos)

1. **Una regla por enemigo**: dividirse, moverse doble, volar, bloquear el primer golpe, invocar hasta un tope, invulnerable salvo por un núcleo.
2. **Ataques telegrafiados**: preparación y recuperación largas. En un juego por turnos equivale a "anuncia en el turno N y ejecuta en el N+1".
3. **Posicionamiento**: inmunidad frontal, que obliga a flanquear.
4. **Partes de jefe** como entidades separadas (manos, núcleo, gemelos con vida compartida).
5. **Interacción con el entorno**: trampas que dañan a todos, pantanos, voladores contra trampas de suelo.
6. **Estados alterados** del jugador: controles invertidos, petrificación, congelación.
