#!/usr/bin/env bash
# Prueba de carga reproducible de Ascua (docs/rendimiento.md).
#
# Local (por defecto): levanta la API limitada a 0,5 vCPU / 1 GiB con docker compose, muestrea
# `docker stats` y corre el generador NBomber dentro de la red de compose.
#   loadtests/run.sh --stages 10,20,40,80 --stage-seconds 60 --label rampa
#
# Remoto (Azure u otra URL): solo corre el generador; CPU/RAM se leen en las métricas de la plataforma.
#   loadtests/run.sh --url https://ascua.example.azurecontainerapps.io --stages 20 --stage-seconds 120
#
# Otros parámetros que pasan tal cual al generador: --idle-seconds, --think-ms, --skip-rate, --clock sync|estimate.
# Resultados: loadtests/reports/<fecha>-<label>/ (resumen.md, resultados.json, stats.csv, nbomber/).
set -euo pipefail
cd "$(dirname "$0")/.."

url="" label="carga" args=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --url) url="$2"; shift 2 ;;
    --label) label="$2"; shift 2 ;;
    *) args+=("$1" "$2"); shift 2 ;;
  esac
done

run="$(date -u +%Y%m%d-%H%M%S)-${label}"
mkdir -p "loadtests/reports/${run}"
# Proyecto propio: no toca la base de desarrollo, y cada corrida parte de cero (sin partidas de corridas
# anteriores que el temporizador siga cerrando por plazo y ensucien la línea base).
compose=(docker compose -p ascua-carga -f docker-compose.yml -f loadtests/docker-compose.load.yml)
export MSYS_NO_PATHCONV=1  # Git Bash en Windows: no reescribir /reports/...

if [[ -z "$url" ]]; then
  "${compose[@]}" down -v --remove-orphans
  "${compose[@]}" up -d --build --wait api
  "${compose[@]}" --profile load build loadtest
  api_id="$("${compose[@]}" ps -q api)"
  stats="loadtests/reports/${run}/stats.csv"
  # Una muestra por ciclo de docker stats (~1-2 s): epochMs,CPU%,uso / límite
  ( while true; do
      line="$(docker stats --no-stream --format '{{.CPUPerc}},{{.MemUsage}}' "$api_id" 2>/dev/null)" || break
      echo "$(date -u +%s%3N),${line}" >> "$stats"
    done ) &
  sampler=$!
  trap 'kill $sampler 2>/dev/null || true' EXIT
  set +e
  "${compose[@]}" --profile load run --rm loadtest \
    --url http://api:8080 --label "$label" --out "/reports/${run}" --stats "/reports/${run}/stats.csv" "${args[@]}"
  code=$?
  set -e
  kill "$sampler" 2>/dev/null || true
else
  docker build -t ascua-loadtest -f loadtests/Dockerfile .
  set +e
  docker run --rm -v "$(pwd)/loadtests/reports:/reports" ascua-loadtest \
    --url "$url" --clock estimate --label "$label" --out "/reports/${run}" "${args[@]}"
  code=$?
  set -e
fi

echo "Resultados en loadtests/reports/${run}/resumen.md (código ${code}: 0 = cumple la meta en la primera etapa)"
exit "$code"
