#!/usr/bin/env bash
set -euo pipefail
environment=${1:?dev o prod}
[[ "$environment" == dev || "$environment" == prod ]] || exit 2
rg="rg-ascua-$environment"
app="ca-ascua-$environment"
acr=$(az acr list -g "$rg" --query '[0].name' -o tsv)
[[ -n "$acr" ]] || { echo 'Ejecutar infra.yml primero' >&2; exit 1; }
server=$(az acr show -n "$acr" --query loginServer -o tsv)
sha=$(git rev-parse HEAD)
image="$server/ascua-api:$sha"
az acr login -n "$acr" --output none
docker build -t "$image" .
trivy image --exit-code 1 --severity CRITICAL --ignore-unfixed "$image"
docker push "$image"
# Fijar tráfico a una revisión explícita antes de crear otra; nunca usar latestRevision durante el canario.
traffic=$(az containerapp ingress traffic show -g "$rg" -n "$app" -o json)
old=$(jq -r '[.[] | select(.weight > 0)] | sort_by(.weight) | last | .revisionName // empty' <<< "$traffic")
if [[ -z "$old" ]]; then
  old=$(az containerapp show -g "$rg" -n "$app" --query properties.latestReadyRevisionName -o tsv)
fi
[[ -n "$old" ]] || { echo 'No hay una revisión sana para conservar el tráfico' >&2; exit 1; }
az containerapp ingress traffic set -g "$rg" -n "$app" --revision-weight "$old=100" --output none
suffix="r-${sha:0:10}-${GITHUB_RUN_NUMBER:-0}-${GITHUB_RUN_ATTEMPT:-1}"
new="$app--$suffix"
promoted=false
recover() {
  result=$?
  if [[ "$result" != 0 && "$promoted" == false ]]; then
    az containerapp ingress traffic set -g "$rg" -n "$app" --revision-weight "$old=100" --output none || true
  fi
  exit "$result"
}
trap recover EXIT
az containerapp update -g "$rg" -n "$app" --image "$image" --revision-suffix "$suffix" --output none
fqdn=$(az containerapp revision show -g "$rg" -n "$app" --revision "$new" --query properties.fqdn -o tsv)
[[ -n "$fqdn" ]] || exit 1
curl --fail --retry 30 --retry-all-errors --retry-delay 10 "https://$fqdn/health/ready"
if [[ "$environment" == prod ]]; then
  az containerapp ingress traffic set -g "$rg" -n "$app" --revision-weight "$old=90" "$new=10" --output none
  for _ in {1..6}; do
    curl --fail --silent --show-error "https://$fqdn/health/ready" >/dev/null
    sleep 10
  done
fi
az containerapp ingress traffic set -g "$rg" -n "$app" --revision-weight "$new=100" --output none
public=$(az containerapp show -g "$rg" -n "$app" --query properties.configuration.ingress.fqdn -o tsv)
curl --fail --retry 5 --retry-all-errors --retry-delay 5 "https://$public/health/ready"
# Conservar la revisión antigua inactiva para rollback. SIGTERM permite el drenado del juego.
sleep 30
az containerapp revision deactivate -g "$rg" -n "$app" --revision "$old" --output none
promoted=true
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  printf '### Ascua %s\n\nURL: https://%s\n\nRevisión: %s\n\nRollback: %s\n' "$environment" "$public" "$new" "$old" >> "$GITHUB_STEP_SUMMARY"
fi
