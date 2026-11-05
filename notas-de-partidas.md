# Notas de las partidas de prueba

Los números los guarda el servidor (`./reporte-partidas.ps1`). Aquí va lo que los números no ven: qué se sintió, qué confundió y qué fue divertido.

## Prueba simulada (4 navegadores con teclas al azar)

Sirve para encontrar fallos, **no para medir balance ni diversión**: nadie decide con intención.

| Comprobado | Resultado |
|---|---|
| Partida completa de 60 rondas en 4 navegadores | Terminó en los cuatro, en 174 s |
| Recargar en la elección de mejora | La oferta vuelve a aparecer |
| Recargar con la ronda abierta | Misma ronda y mismo oro, en 0,3 s |
| Recargar con la orden ya enviada | Misma ronda y mismo oro, en 0,5 s |
| Cerrar la pestaña y volver con el mismo nombre | Vuelve directo a su partida |
| Salir y recargar | Pide el nombre otra vez |
| Errores en consola | Solo el 401 esperado al entrar con un nombre nuevo |

**Corregido tras la prueba**
- Al agotarse las 60 rondas decía «Todos cayeron» aunque hubiera héroes vivos.
- En la clasificación salía «4de oro», sin espacio.

**Sin probar todavía:** traiciones con datos reales y victorias reales: ninguna partida al azar derrotó al Celador. El reporte se comprobó con un ganador puesto a mano en una base de prueba.

## Prueba del cliente de la versión 3 (4 navegadores contra el servidor real)

Jugadores que caminan con intención a la escalera, atacan con paso y golpe, sabotean, empujan, votan y eligen puerta. Sirve para encontrar fallos, **no para medir balance ni diversión**.

| Comprobado | Resultado |
|---|---|
| Entrar solo con el nombre, crear sala con maldición | Bien; la maldición se ve en la sala, en la partida y al final |
| Elegir mejora y misión; recargar mientras se elige | Las dos ofertas vuelven |
| Paso marcado con vista previa, Esc, Enter, repetir flecha y botones | Bien; en móvil también, con la demo |
| Paso y golpe en la misma orden | 14 a 26 por partida |
| Sabotaje y empujón | El sabotaje sale sin autor y con la víctima marcada; solo su autor recibe el aviso |
| 4 interludios por partida, con voto y puerta | Condena culpable, inocente con multa y sin mayoría |
| Recargar antes y después de votar; perder la red 3 s | Vuelve la votación; el voto hecho se conserva |
| Recargar en ronda y con orden enviada; cerrar la pestaña y volver | Misma partida y mismo oro, en menos de 1,5 s |
| Sala inundada | El agua se ve y sube |
| Final, títulos y maldiciones | Bien |
| Revancha | Los otros reciben aviso en menos de 3 s y los cuatro llegan a la misma sala |
| Tres columnas a 1366×768 y 1920×1080 | Sin scroll de página |
| Errores de JavaScript o CSP | Ninguno; solo el 404 esperado de «sin sala actual» |
| `scripts/reporte-partidas.sql` | Corre con estas partidas |

**Corregido tras la prueba**
- Las ofertas nuevas del servidor no llegaban al cliente: faltaba registrarlas en `hub.js`.
- El daño de sabotaje perdía su resaltado en el registro.
- La revancha y el aviso quedaban debajo de la clasificación: ahora van arriba.

**Sin probar todavía:** una victoria real contra el Celador (estos bots llegan al límite de 60 rondas), si los sonidos se oyen bien (el navegador de prueba no tiene audio) y un celular físico.

## Prueba de victoria y salas (5 partidas, 4 navegadores con jugadores que van a la escalera y al Celador)

Datos revisados en la base. Los bots no usan habilidades, así que 1 victoria de 5 no mide la dificultad real.

| Comprobado | Resultado |
|---|---|
| Vencer al Celador | 1 de 5. Pantalla de victoria correcta para ganador y demás |
| Reparto del Celador | Daño 14, 9 y 1 dio +4, +2 y 0, más +1 al golpe final: correcto |
| Tesoro | 4 cofres y 1 monstruo más |
| Arena | 0 cofres, +1 o +2 monstruos y +2 de oro por golpe final |
| Santuario | 1 monstruo, 6 pinchos contra 3, altar abierto sin llave |
| Oscura | Visión 2 (La Escucha sigue en 1), 3 cofres |
| Errores de JS o CSP | Ninguno |

**A vigilar**
- Un Fantasma que murió antes ganó con 10 de oro, y quien hizo 14 de daño quedó último con 5.
- Escarcha congela pero no daña: un bot con ella congeló a un monstruo durante 26 rondas sin matarlo. Una persona que la elija debe pedir ayuda.

## Qué mirar en las partidas reales (versión 3)

Antes que nada: **todo lo de 06-version-3.md está sin probar con personas**. Los números salen de bots, y el mayor riesgo es que sea fácil.

- **Paso y acción:** ¿el Celador cae muy rápido? Si sí, primero una maldición (`WARDEN_PLUS`) y luego subir su vida.
- **El Juicio:** ¿alguien sabotea? ¿Acusan por sospecha o por venganza? ¿Alcanzan 20 s?
- **Puertas:** ¿alguien elige Inundada? ¿Cuál se evita?
- **Mejoras:** ¿algún precio nadie lo paga, o alguna nadie la quiere?

## Qué mirar en las partidas anteriores

1. **Dificultad:** ¿el Celador cae demasiado fácil? Es el mayor riesgo del balance actual. ¿Llegan al límite de 60 rondas?
2. **Errata y La Usurera:** ¿dominan como dice la simulación?
3. **Hilván y El Campanero:** los bots casi no usan sus habilidades, así que su dato no vale mucho. ¿Rinden más entre personas?
4. **Traición:** ¿alguien la usa? ¿La revelación final genera reacción?
5. **Tiempo:** ¿alcanzan 20 s por ronda? ¿Se siente lenta o apurada la partida?
6. **Claridad:** ¿qué tuvieron que preguntar? Cada pregunta es algo que la interfaz no explica.

## Plantilla por partida

Copia esto por cada partida; basta con tres minutos al terminar.

```
### Partida N (fecha y hora)
Jugadores y héroes:
Resultado y rondas:
Momento más divertido:
Qué confundió o hubo que preguntar:
Héroe que se sintió demasiado fuerte:
Héroe que se sintió inútil:
Errores o cosas raras:
```
