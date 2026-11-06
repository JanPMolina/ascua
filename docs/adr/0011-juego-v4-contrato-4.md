# ADR-0011: Versión 4 del juego, contrato 4.0.0 solo en la rama local

- **Estado:** aceptada
- **Fecha:** 2026-11-06
- **Autores:** Equipo Ascua

## Contexto

Tras jugar la versión 3 con amigos ([07-version-4.md](../../planteamiento/07-version-4.md)): la interfaz de flechas confundía, nadie usó las habilidades, la votación entre salas no aportaba y 20 s por ronda era mucho. Se pidió hasta 5 jugadores con una sala más grande, dificultad por jugadores, sin votación ni sabotaje y sin límite de rondas. El contrato pierde mensajes, estados y campos: no se puede extender sin romper.

## Decisión

1. **Contrato 4.0.0 y `protocolVersion=4`, sin periodo de transición**, como en [ADR-0010](0010-juego-v3-contrato-3.md). Todo queda en `feat/juego-v4`; no va a `main`.
2. **La orden en el cable no cambia.** Las fases con clics (1 Moverte → 2 Actuar → Listo) son del cliente: traduce cada clic a `step` y `direction`. El servidor sigue sin selección de objetivos.
3. **El lado de la sala viaja en la vista** (`view.room.size`) y es una propiedad de cada `Room` del dominio; ya no hay una constante de 9.
4. **El tipo de cada sala se sortea al crear la partida** y se anuncia al entrar. La vista solo trae la sala actual, así que nadie lo ve antes.
5. **Sin límite de rondas, con el frío** (daño creciente desde la ronda 80) para que toda partida termine. Las pruebas de integración usan `Ascua:Realtime:TestRoundLimit`, que no se configura en ningún entorno.
6. **No se borran partidas.** `SABOTAGE`, `RoundLimit`, `MostTrialGoldLost` y las misiones viejas quedan en los enums del dominio como obsoletas para leer el historial; el contrato ya no los anuncia.

## Consecuencias

- Una partida guardada con el interludio abierto (versión 3) no se puede reanudar: su estado ya no tiene esos campos.
- Las partidas duran más (77,7 rondas de media en la simulación), pero las rondas son de 11 s y se cierran en cuanto todos pulsan Listo.
