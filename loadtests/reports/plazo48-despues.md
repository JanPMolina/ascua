# Carga: plazo48-despues

- Destino: `http://api:8080/` — inicio 2026-10-30 17:02:56 UTC
- Desfase de reloj servidor−cliente estimado (mediana): -0.6 ms
- Punto de quiebre: ya la primera etapa (48 partidas) incumple la meta

| Etapa | Activas | Rondas (por plazo) | p50 ms | p95 ms | p99 ms | máx ms | p95 por plazo | RTT orden p95 | Órdenes env/ACK | Tardías | Errores | Huecos seq | Perdidas | Dup. | Sin difusión | Tasa error | CPU % | RAM MiB | CPU %/partida | MiB/partida (cota) | Meta |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| reposo | 0 | 0 (0) | 0 | 0 | 0 | 0 | 0 | 0 | 0/0 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 30.01 | 517.57 | n/d | n/d | — |
| 48 partidas | 47 | 1044 (417) | 64.2 | 447.5 | 594.4 | 917.5 | 425.9 | 195.1 | 3651/3147 | 13 | 0 | 0 | 0 | 0 | 0 | 0.17 % | 47.58 | 454.45 | 0.37 | 9.66 | ❌ |
| 48 partidas | 47.9 | 1138 (407) | 102.2 | 294.1 | 393.5 | 495.5 | 293.4 | 104.3 | 3874/2804 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 47.8 | 170.94 | 0.37 | 3.57 | ❌ |
| 48 partidas | 48 | 1155 (442) | 20.4 | 92.3 | 177 | 219.5 | 96.5 | 38.6 | 3938/2837 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 40.91 | 174.87 | 0.23 | 3.65 | ✅ |
