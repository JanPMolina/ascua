# Carga: fijo64-despues

- Destino: `http://api:8080/` — inicio 2026-10-30 16:34:25 UTC
- Desfase de reloj servidor−cliente estimado (mediana): -0.5 ms
- Punto de quiebre: no se alcanzó en las etapas probadas

| Etapa | Activas | Rondas (por plazo) | p50 ms | p95 ms | p99 ms | máx ms | RTT orden p95 | Órdenes env/ACK | Tardías | Errores | Huecos seq | Perdidas | Dup. | Sin difusión | Tasa error | CPU % | RAM MiB | CPU %/partida | MiB/partida (cota) | Meta |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| reposo | 0 | 0 (0) | 0 | 0 | 0 | 0 | 0 | 0/0 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 0.69 | 309.7 | n/d | n/d | — |
| 64 partidas | 64 | 2106 (0) | 12.4 | 162.9 | 217.3 | 302 | 74.5 | 8472/6048 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 44.41 | 227.99 | 0.68 | 3.56 | ✅ |
| 64 partidas | 63.9 | 2154 (0) | 8.9 | 94.4 | 109.7 | 201.1 | 59 | 8585/5993 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 40.71 | 178.66 | 0.63 | 2.79 | ✅ |
| 64 partidas | 64 | 2222 (0) | 8.3 | 16.4 | 45.9 | 83.1 | 5.2 | 8879/6089 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 35.45 | 180.13 | 0.54 | 2.82 | ✅ |
| 64 partidas | 64 | 2140 (0) | 8.2 | 15.2 | 38.6 | 83.7 | 4.3 | 8592/6290 | 0 | 0 | 0 | 0 | 0 | 0 | 0.00 % | 35.37 | 179.33 | 0.54 | 2.8 | ✅ |
