# Carga: fijo96-antes

- Destino: `http://api:8080/` — inicio 2026-10-30 16:23:33 UTC
- Desfase de reloj servidor−cliente estimado (mediana): -0.3 ms
- Punto de quiebre: ya la primera etapa (96 partidas) incumple la meta

| Etapa | Activas | Rondas (por plazo) | p50 ms | p95 ms | p99 ms | máx ms | RTT orden p95 | Órdenes env/ACK | Tardías | Errores | Huecos seq | Perdidas | Dup. | Sin difusión | Tasa error | CPU % | RAM MiB | CPU %/partida | MiB/partida (cota) | Meta |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| reposo | 0 | 0 (0) | 0 | 0 | 0 | 0 | 0 | 0/0 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 0.8 | 374.63 | n/d | n/d | — |
| 96 partidas | 94.5 | 2533 (2) | 214.5 | 620 | 926.6 | 1211.9 | 299.9 | 10211/8145 | 2 | 0 | 0 | 0 | 0 | 0 | 0.01 % | 49.67 | 263.6 | 0.52 | 2.79 | ❌ |
| 96 partidas | 95.5 | 2940 (0) | 105 | 388.2 | 484.2 | 505.7 | 166 | 11783/7494 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 50.16 | 212.42 | 0.52 | 2.22 | ❌ |
| 96 partidas | 95.8 | 3245 (0) | 20 | 104.9 | 172.2 | 208.5 | 71.6 | 12986/8552 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 50.21 | 216.5 | 0.52 | 2.26 | ✅ |
