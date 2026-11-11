#!/usr/bin/env bash
set -euo pipefail
environment=${1:?dev o prod}
[[ "$environment" == dev || "$environment" == prod ]] || exit 2
requested=${2:-}
rg="rg-ascua-$environment"
app="ca-ascua-$environment"
start=$SECONDS
current=$(az containerapp ingress traffic show -g "$rg" -n "$app" -o json |
  jq -r '[.[] | select(.weight > 0)] | sort_by(.weight) | last | .revisionName // empty')
if [[ -z "$current" ]]; then
  current=$(az containerapp show -g "$rg" -n "$app" --query properties.latestReadyRevisionName -o tsv)
fi
if [[ -z "$requested" ]]; then
  requested=$(az containerapp revision list -g "$rg" -n "$app" --all -o json |
    jq -r --arg current "$current" '[.[] | select(.name != $current and .properties.healthState != "Unhealthy")] | sort_by(.properties.createdTime) | last | .name // empty')
fi
[[ "$requested" == "$app--"* ]] || { echo 'Indica una revisión de esta app' >&2; exit 2; }
az containerapp revision show -g "$rg" -n "$app" --revision "$requested" --output none
az containerapp revision activate -g "$rg" -n "$app" --revision "$requested" --output none
fqdn=$(az containerapp revision show -g "$rg" -n "$app" --revision "$requested" --query properties.fqdn -o tsv)
curl --fail --retry 12 --retry-all-errors --retry-delay 5 "https://$fqdn/health/ready"
az containerapp ingress traffic set -g "$rg" -n "$app" --revision-weight "$requested=100" --output none
public=$(az containerapp show -g "$rg" -n "$app" --query properties.configuration.ingress.fqdn -o tsv)
curl --fail --retry 5 --retry-all-errors --retry-delay 5 "https://$public/health/ready"
elapsed=$((SECONDS - start))
printf 'Rollback a %s: %s segundos\n' "$requested" "$elapsed"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  printf '### Rollback %s\n\nRevisión: %s\n\nDuración: %s segundos\n\nURL: https://%s\n' "$environment" "$requested" "$elapsed" "$public" >> "$GITHUB_STEP_SUMMARY"
fi
[[ "$elapsed" -lt 300 ]]
