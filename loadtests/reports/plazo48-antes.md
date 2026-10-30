# Carga: plazo48-antes

- Destino: `http://api:8080/` — inicio 2026-10-30 16:47:08 UTC
- Desfase de reloj servidor−cliente estimado (mediana): -0.5 ms
- Punto de quiebre: ya la primera etapa (48 partidas) incumple la meta

| Etapa | Activas | Rondas (por plazo) | p50 ms | p95 ms | p99 ms | máx ms | p95 por plazo | RTT orden p95 | Órdenes env/ACK | Tardías | Errores | Huecos seq | Perdidas | Dup. | Sin difusión | Tasa error | CPU % | RAM MiB | CPU %/partida | MiB/partida (cota) | Meta |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| reposo | 0 | 0 (0) | 0 | 0 | 0 | 0 | 0 | 0 | 0/0 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 0.98 | 330 | n/d | n/d | — |
| 48 partidas | 48 | 1110 (447) | 18.5 | 266.7 | 390.4 | 486 | 312.7 | 56.1 | 3780/3031 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 35.32 | 330.12 | 0.72 | 6.88 | ❌ |
| 48 partidas | 48 | 1233 (392) | 10 | 260.3 | 306.3 | 347.8 | 266.2 | 31.2 | 4201/2743 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 23.36 | 170.69 | 0.47 | 3.56 | ❌ |
| 48 partidas | 48 | 1146 (409) | 11.8 | 261.5 | 273.4 | 313.9 | 268.4 | 4.2 | 3931/2826 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 23.58 | 172.26 | 0.47 | 3.59 | ❌ |
