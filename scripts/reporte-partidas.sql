-- Estadísticas de las partidas terminadas, a partir de lo que ya guarda la base. Lo corre reporte-partidas.ps1.
-- Las partidas abandonadas o con rondas de prueba (distintas de 20 s) no cuentan.
\set ON_ERROR_STOP on
\pset format unaligned
\pset tuples_only on

CREATE TEMP VIEW partidas AS
SELECT m.id, m.finished_at, m.round AS rondas, m.result->>'Kind' AS resultado, m.result->>'Reason' AS razon,
       m.result->'Winners' AS ganadores, m.state,
       (SELECT count(*) FROM match_players x WHERE x.match_id = m.id) AS jugadores
FROM matches m
WHERE m.status = 'FINISHED' AND m.result IS NOT NULL AND m.round_duration_ms = 20000
  AND m.result->>'Kind' <> 'Abandoned';

CREATE TEMP VIEW filas AS
SELECT p.id AS partida, p.finished_at AS fecha, p.jugadores, p.rondas, p.resultado, p.razon,
       mp.display_name AS jugador, mp.hero AS heroe,
       p.ganadores @> jsonb_build_array(jsonb_build_object('Value', mp.user_id::text)) AS gano,
       p.resultado IN ('Victory', 'Draw') AS celador_cayo,
       h->>'Status' AS estado_final,
       (h->'Progress'->>'HasFallen')::boolean AS cayo,
       (h->>'Extinctions')::int AS muertes,
       h->>'PathChoice' AS camino,
       (h->>'PeakEmbers')::int AS oro_maximo,
       (SELECT (s->>'Total')::int FROM jsonb_array_elements(p.state->'Outcome'->'Scores') s
         WHERE s->'Player'->>'Value' = mp.user_id::text) AS puntos,
       (SELECT (s->>'PactFulfilled')::boolean FROM jsonb_array_elements(p.state->'Outcome'->'Scores') s
         WHERE s->'Player'->>'Value' = mp.user_id::text) AS mision_cumplida,
       (SELECT count(*) FROM match_events e WHERE e.match_id = p.id AND e.type = 'SkillUsed' AND e.payload->'Player'->>'Value' = mp.user_id::text) AS habilidades,
       (SELECT count(*) FROM match_events e WHERE e.match_id = p.id AND e.type = 'HeroAttacked' AND e.payload->'Player'->>'Value' = mp.user_id::text) AS ataques,
       (SELECT count(*) FROM match_events e WHERE e.match_id = p.id AND e.type = 'EnemyKilled' AND e.payload->'Killer'->>'Value' = mp.user_id::text) AS golpes_finales,
       (SELECT coalesce(sum((e.payload->>'Amount')::int), 0) FROM match_events e WHERE e.match_id = p.id AND e.type = 'EntityDamaged' AND e.payload->'Source'->'Hero'->>'Value' = mp.user_id::text AND e.payload->'Target'->>'EnemyId' IS NOT NULL) AS dano_a_enemigos,
       (SELECT coalesce(sum((e.payload->>'Amount')::int), 0) FROM match_events e WHERE e.match_id = p.id AND e.type = 'EntityDamaged' AND e.payload->'Target'->'Hero'->>'Value' = mp.user_id::text) AS dano_recibido,
       (SELECT count(*) FROM match_events e WHERE e.match_id = p.id AND e.type = 'HeroRevived' AND e.payload->'Reviver'->>'Value' = mp.user_id::text) AS levantados,
       (SELECT coalesce(sum((e.payload->>'Damage')::int), 0) FROM match_events e WHERE e.match_id = p.id AND e.type = 'BetrayalRevealed' AND e.payload->'Traitor'->>'Value' = mp.user_id::text) AS dano_por_traicion
FROM partidas p
JOIN match_players mp ON mp.match_id = p.id
LEFT JOIN LATERAL (SELECT h FROM jsonb_array_elements(p.state->'Heroes') h WHERE h->'Player'->>'Value' = mp.user_id::text) x ON true;

\copy (SELECT * FROM filas ORDER BY fecha, partida, jugador) TO '/tmp/jugadores.csv' WITH (FORMAT csv, HEADER)

\o /tmp/resumen.md
SELECT '# Partidas jugadas' || E'\n\n' ||
       format('**%s partidas** · el Celador cayó en **%s %%** · **%s rondas** de media (límite 60) · %s llegaron al límite de rondas.',
         count(*), coalesce(round(100.0 * count(*) FILTER (WHERE resultado IN ('Victory', 'Draw')) / nullif(count(*), 0)), 0),
         coalesce(round(avg(rondas), 1), 0), count(*) FILTER (WHERE razon = 'RoundLimit')) || E'\n\n' ||
       'Con pocas partidas esto es solo una pista: quién juega pesa tanto como el héroe. Se necesitan unas 10 partidas por héroe para sugerir cambios.' || E'\n'
FROM partidas;

SELECT E'## Por héroe\n\nLa columna «Simulado» es su victoria con 4 jugadores en docs/balance.md.\n\n| Héroe | Partidas | Victoria | Simulado | Puntos | Cayó | Golpes finales | Daño hecho | Daño recibido | Habilidad | Sugerencia |\n|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|';
SELECT format('| %s | %s | %s %% | %s %% | %s | %s %% | %s | %s | %s | %s | %s |',
         heroe, count(*), round(100.0 * count(*) FILTER (WHERE gano) / count(*)), sim.valor,
         round(avg(puntos), 1), round(100.0 * count(*) FILTER (WHERE cayo) / count(*)),
         round(avg(golpes_finales), 1), round(avg(dano_a_enemigos), 1), round(avg(dano_recibido), 1), round(avg(habilidades), 1),
         CASE WHEN count(*) < 10 THEN 'faltan partidas'
              WHEN 100.0 * count(*) FILTER (WHERE gano) / count(*) > 40 THEN 'revisar: gana demasiado'
              WHEN 100.0 * count(*) FILTER (WHERE gano) / count(*) < 12 THEN 'revisar: gana muy poco'
              WHEN avg(habilidades) < 0.5 THEN 'revisar: casi no usan su habilidad'
              ELSE 'en rango' END)
FROM filas f
LEFT JOIN (VALUES ('USURERA', 33.1), ('ERRATA', 32.9), ('AVARO', 28.8), ('ANTEAYER', 28.7), ('CAMPANERO', 26.5),
                  ('ESCUCHA', 23.6), ('MAMPOSTERO', 19.4), ('POLIZON', 15.8), ('HILVAN', 15.1)) sim(heroe_sim, valor) ON sim.heroe_sim = f.heroe
GROUP BY heroe, sim.valor
ORDER BY 100.0 * count(*) FILTER (WHERE gano) / count(*) DESC, heroe;

SELECT E'\n## Dificultad\n\n' || CASE
         WHEN count(*) < 5 THEN 'Faltan partidas para opinar (mínimo 5).'
         WHEN 100.0 * count(*) FILTER (WHERE resultado IN ('Victory', 'Draw')) / count(*) > 80 THEN 'Demasiado fácil: el Celador casi siempre cae. Subir `EnemyCatalog.WardenHpPerPlayer` (hoy 5) antes de tocar a un héroe.'
         WHEN 100.0 * count(*) FILTER (WHERE resultado IN ('Victory', 'Draw')) / count(*) < 25 THEN 'Demasiado difícil: el Celador casi nunca cae. Revisar la vida de los monstruos y las 60 rondas.'
         ELSE 'Dentro de lo esperado: el Celador cae entre 25 % y 80 % de las veces.' END || E'\n'
FROM partidas;

SELECT E'## Por jugador\n\nSirve para separar al héroe de quien lo juega.\n\n| Jugador | Partidas | Victoria | Puntos | Héroes usados |\n|---|---:|---:|---:|---|';
SELECT format('| %s | %s | %s %% | %s | %s |', jugador, count(*), round(100.0 * count(*) FILTER (WHERE gano) / count(*)),
         round(avg(puntos), 1), string_agg(DISTINCT heroe, ', '))
FROM filas GROUP BY jugador ORDER BY 1.0 * count(*) FILTER (WHERE gano) / count(*) DESC, jugador;

SELECT E'\n## Dúos en la misma partida\n\n| Dúo | Partidas | El Celador cayó | Alguno ganó |\n|---|---:|---:|---:|';
SELECT format('| %s + %s | %s | %s %% | %s %% |', a.heroe, b.heroe, count(*),
         round(100.0 * count(*) FILTER (WHERE a.celador_cayo) / count(*)),
         round(100.0 * count(*) FILTER (WHERE a.gano OR b.gano) / count(*)))
FROM filas a JOIN filas b ON a.partida = b.partida AND a.heroe < b.heroe
GROUP BY a.heroe, b.heroe ORDER BY count(*) DESC, a.heroe, b.heroe LIMIT 15;

SELECT E'\n## Partidas\n\n| Fecha (UTC) | Jugadores | Rondas | Resultado | Ganó |\n|---|---:|---:|---|---|';
SELECT format('| %s | %s | %s | %s | %s |', to_char(min(fecha), 'YYYY-MM-DD HH24:MI'), count(*), min(rondas),
         min(resultado) || CASE WHEN min(razon) IS NULL OR min(razon) = 'None' THEN '' ELSE ' (' || min(razon) || ')' END,
         coalesce(string_agg(jugador || ' (' || heroe || ')', ', ') FILTER (WHERE gano), '—'))
FROM filas GROUP BY partida ORDER BY min(fecha);
\o
