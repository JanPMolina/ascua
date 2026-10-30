# Carga: fijo64-antes

- Destino: `http://api:8080/` — inicio 2026-10-30 16:40:09 UTC
- Desfase de reloj servidor−cliente estimado (mediana): -0.5 ms
- Punto de quiebre: ya la primera etapa (64 partidas) incumple la meta

| Etapa | Activas | Rondas (por plazo) | p50 ms | p95 ms | p99 ms | máx ms | RTT orden p95 | Órdenes env/ACK | Tardías | Errores | Huecos seq | Perdidas | Dup. | Sin difusión | Tasa error | CPU % | RAM MiB | CPU %/partida | MiB/partida (cota) | Meta |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| reposo | 0 | 0 (0) | 0 | 0 | 0 | 0 | 0 | 0/0 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 7.47 | 499.79 | n/d | n/d | — |
| 64 partidas | 63.7 | 1969 (1) | 92.8 | 489.6 | 606.2 | 704.3 | 175.9 | 7841/5780 | 1 | 0 | 0 | 0 | 0 | 0 | 0.01 % | 49.73 | 193.48 | 0.66 | 3.04 | ❌ |
| 64 partidas | 63.7 | 2068 (0) | 14.5 | 287.7 | 403 | 504.1 | 91.9 | 8260/5657 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 48.13 | 181.05 | 0.64 | 2.84 | ❌ |
| 64 partidas | 64 | 2182 (0) | 10.4 | 58.1 | 98.5 | 187.2 | 44.8 | 8743/5721 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 46.25 | 182.48 | 0.61 | 2.85 | ✅ |
| 64 partidas | 64 | 2138 (0) | 10.4 | 51 | 84.8 | 115.3 | 43.9 | 8533/6239 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 46.47 | 182.54 | 0.61 | 2.85 | ✅ |
