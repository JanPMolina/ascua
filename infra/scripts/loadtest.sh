#!/usr/bin/env bash
set -euo pipefail
# Exclusivamente dev. Conserva tráfico/configuración y restaura incluso si la carga falla.
rg=rg-ascua-dev
app=ca-ascua-dev
temp=$(mktemp -d)
umask 077
az containerapp show -g "$rg" -n "$app" -o json > "$temp/before.json"
public=$(jq -r '.properties.configuration.ingress.fqdn' "$temp/before.json")
old=$(az containerapp ingress traffic show -g "$rg" -n "$app" -o json | jq -r '[.[] | select(.weight > 0)] | sort_by(.weight) | last | .revisionName // empty')
[[ -n "$old" ]] || old=$(jq -r '.properties.latestReadyRevisionName' "$temp/before.json")
min=$(jq -r '.properties.template.scale.minReplicas' "$temp/before.json")
max=$(jq -r '.properties.template.scale.maxReplicas' "$temp/before.json")
names=(Ascua__Realtime__EnableDevEndpoints Ascua__Realtime__RoundDuration Ascua__RateLimiting__Auth__PermitLimit)
restore=() remove=()
for name in "${names[@]}"; do
  if jq -e --arg name "$name" '.properties.template.containers[0].env[] | select(.name == $name)' "$temp/before.json" >/dev/null; then
    value=$(jq -r --arg name "$name" '.properties.template.containers[0].env[] | select(.name == $name) | .value' "$temp/before.json")
    restore+=("$name=$value")
  else
    remove+=("$name")
  fi
done
changed=false
testRevision=''
cleanup() {
  result=$?
  trap - EXIT
  if [[ "$changed" == true ]]; then
    az containerapp ingress traffic set -g "$rg" -n "$app" --revision-weight "$old=100" --output none || result=1
    args=(--min-replicas "$min" --max-replicas "$max")
    [[ ${#restore[@]} == 0 ]] || args+=(--set-env-vars "${restore[@]}")
    [[ ${#remove[@]} == 0 ]] || args+=(--remove-env-vars "${remove[@]}")
    az containerapp update -g "$rg" -n "$app" "${args[@]}" --output none || result=1
    if [[ -n "$testRevision" ]]; then
      az containerapp revision deactivate -g "$rg" -n "$app" --revision "$testRevision" --output none || result=1
    fi
    curl --fail --retry 12 --retry-all-errors --retry-delay 5 "https://$public/health/ready" || result=1
  fi
  rm -rf -- "$temp"
  exit "$result"
}
trap cleanup EXIT
az containerapp ingress traffic set -g "$rg" -n "$app" --revision-weight "$old=100" --output none
changed=true
suffix="load-${GITHUB_RUN_ID:?}-${GITHUB_RUN_ATTEMPT:-1}"
testRevision="$app--$suffix"
az containerapp update -g "$rg" -n "$app" --revision-suffix "$suffix" --min-replicas 1 --max-replicas 1 \
  --set-env-vars Ascua__Realtime__EnableDevEndpoints=true Ascua__Realtime__RoundDuration=00:00:03 Ascua__RateLimiting__Auth__PermitLimit=100000 --output none
fqdn=$(az containerapp revision show -g "$rg" -n "$app" --revision "$testRevision" --query properties.fqdn -o tsv)
curl --fail --retry 20 --retry-all-errors --retry-delay 10 "https://$fqdn/health/ready"
az containerapp ingress traffic set -g "$rg" -n "$app" --revision-weight "$testRevision=100" --output none
mkdir -p loadtests/reports/azure-metrics
start=$(date -u +%Y-%m-%dT%H:%M:%SZ)
set +e
bash loadtests/run.sh --url "https://$public" --stages 16,32,64,96,112 --stage-seconds 120 --idle-seconds 15 --label azure
result=$?
set -e
end=$(date -u +%Y-%m-%dT%H:%M:%SZ)
appId=$(jq -r .id "$temp/before.json")
for metric in UsageNanoCores WorkingSetBytes; do
  az monitor metrics list --resource "$appId" --metric "$metric" --interval PT1M --aggregation Average Maximum \
    --filter "podName eq '*'" --start-time "$start" --end-time "$end" -o json > "loadtests/reports/azure-metrics/$metric.json" || result=1
done
printf '{"startUtc":"%s","endUtc":"%s","revision":"%s"}\n' "$start" "$end" "$testRevision" > loadtests/reports/azure-metrics/window.json
exit "$result"
