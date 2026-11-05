# Saca de la base las partidas jugadas en red local y calcula estimaciones de balance: ./reporte-partidas.ps1
# Deja en partidas/ (fuera de git): resumen.md, jugadores.csv y un respaldo de las partidas con fecha.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$db = 'ascua-red-postgres-1'
if (-not (docker ps --filter "name=^$db$" --format '{{.Names}}')) {
    throw 'El servidor está apagado: corre ./jugar-en-red.ps1 y vuelve a intentarlo.'
}

New-Item -ItemType Directory -Force partidas | Out-Null
docker cp scripts/reporte-partidas.sql "${db}:/tmp/reporte.sql" | Out-Null
docker exec $db psql -q -U ascua -d ascua -f /tmp/reporte.sql
if ($LASTEXITCODE -ne 0) { throw 'No se pudo calcular el reporte.' }
docker cp "${db}:/tmp/resumen.md" partidas/resumen.md | Out-Null
docker cp "${db}:/tmp/jugadores.csv" partidas/jugadores.csv | Out-Null

$fecha = Get-Date -Format 'yyyyMMdd-HHmm'
docker exec $db pg_dump -U ascua -d ascua -t matches -t match_players -t match_events -f /tmp/respaldo.sql
docker cp "${db}:/tmp/respaldo.sql" "partidas/respaldo-$fecha.sql" | Out-Null

Write-Host 'Reporte listo en partidas/resumen.md (datos en partidas/jugadores.csv).' -ForegroundColor Green
