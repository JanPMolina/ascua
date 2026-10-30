# Carga: fijo96-despues

- Destino: `http://api:8080/` — inicio 2026-10-30 16:28:48 UTC
- Desfase de reloj servidor−cliente estimado (mediana): -0.5 ms
- Punto de quiebre: ya la primera etapa (96 partidas) incumple la meta

| Etapa | Activas | Rondas (por plazo) | p50 ms | p95 ms | p99 ms | máx ms | RTT orden p95 | Órdenes env/ACK | Tardías | Errores | Huecos seq | Perdidas | Dup. | Sin difusión | Tasa error | CPU % | RAM MiB | CPU %/partida | MiB/partida (cota) | Meta |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| reposo | 0 | 0 (0) | 0 | 0 | 0 | 0 | 0 | 0/0 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 0.68 | 291.29 | n/d | n/d | — |
| 96 partidas | 95.6 | 2902 (1) | 49.2 | 500 | 824.4 | 1129.1 | 179.7 | 11615/8719 | 1 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 50.26 | 210.43 | 0.52 | 2.2 | ❌ |
| 96 partidas | 95.8 | 3113 (0) | 14.1 | 200.2 | 213.2 | 310.1 | 83.6 | 12470/8548 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 47.83 | 201.5 | 0.49 | 2.1 | ❌ |
| 96 partidas | 95.9 | 3333 (0) | 11.4 | 89.1 | 177.2 | 293.6 | 52.9 | 13335/8559 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 48.36 | 203.68 | 0.5 | 2.12 | ✅ |
