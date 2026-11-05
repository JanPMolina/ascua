# ADR-0010: Versión 3 del juego, contrato 3.0.0 solo en la rama local

- **Estado:** aceptada
- **Fecha:** 2026-11-05
- **Autores:** Equipo Ascua

## Contexto

La revisión de reglas de [06-version-3.md](../../planteamiento/06-version-3.md) cambia la orden (paso y acción), añade una fase entre salas con votos secretos, cambia las misiones y las mejoras. El contrato no se puede extender sin romper: `pactId` pierde valores, `view.me.pact` puede ser `null`, `RoundResolved` deja de traer el cambio de sala y aparece un estado de partida.

Lo que se entrega y despliega el 12-nov es `main`. Este cambio es para jugar en red local y todavía no se ha probado con personas.

## Decisión

1. **Contrato 3.0.0 y `protocolVersion=3`, sin periodo de transición.** Como en [ADR-0009](0009-nueve-heroes-contrato-2.md): no hay clientes externos.
2. **Todo queda en `feat/juego-v3`.** No se fusiona a `main`.
3. **El interludio es una fase del dominio, no un estado nuevo de `matches`.** En la base la partida sigue `IN_PROGRESS`, el contrato la muestra como `INTERLUDE`. Así no se migran la restricción `ck_matches_status` ni el disparador de inmutabilidad, y `deadline` sirve de plazo durable igual que en el draft.
4. **Los votos viven en `GameState`.** Pasan por el mismo camino que todo: se confirman en Postgres, sobreviven a un reinicio y el proyector los filtra.
5. **La sala se genera al elegir la puerta.** Lo que no existe no se puede filtrar.
6. **Revancha con id derivado de la partida.** La atomicidad de crear la da `SaveRoom` en Redis (la sala no debe existir); no hace falta otro lock.
7. **No se borran partidas.** Las misiones eliminadas quedan en el enum como obsoletas, para que el historial y el registro de partidas sigan leyéndose.

## Consecuencias

- El cliente web queda atrás hasta que otra sesión lo adapte: conecta con el protocolo 3, pero no muestra lo nuevo.
- Quien lea `matches.status` en SQL no distingue el interludio; hay que mirar `state->>'Phase'`.
- Si esto algún día va a `main`, la política de 30 días con la versión anterior se debe revisar.
