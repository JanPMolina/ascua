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

## Qué mirar en las partidas reales

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
