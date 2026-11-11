#!/usr/bin/env bash
set -euo pipefail
# Una invocación desde infra.yml crea plataforma, inicializa identidad/clave y publica la app.
environment=${1:?dev o prod}
[[ "$environment" == dev || "$environment" == prod ]] || exit 2
for variable in AZURE_SUBSCRIPTION_ID AZURE_PRINCIPAL_ID POSTGRES_ADMIN_OBJECT_ID POSTGRES_ADMIN_NAME ALERT_EMAIL; do
  [[ -n "${!variable:-}" ]] || { echo "Falta $variable" >&2; exit 2; }
done
deployment="ascua-$environment-${GITHUB_RUN_ID:-manual}"
rg="rg-ascua-$environment"
params="infra/parameters/$environment.bicepparam"
temp=$(mktemp -d)
firewall="github-${GITHUB_RUN_ID:-manual}"
firewallCreated=false
host=''
cleanup() {
  unset PGPASSWORD
  rm -rf -- "$temp"
  if [[ "$firewallCreated" == true ]]; then
    az postgres flexible-server firewall-rule delete -g "$rg" --server-name "${host%%.*}" --name "$firewall" --yes --output none || true
  fi
}
trap cleanup EXIT
umask 077
az account set --subscription "$AZURE_SUBSCRIPTION_ID"
az bicep build-params --file "$params" --outfile "$temp/parameters.json"
echo 'Desplegando plataforma y permisos; PostgreSQL se configura secuencialmente.'
deploy_group() {
  local attempt
  for attempt in {1..3}; do
    if az deployment group create --resource-group "$rg" --template-file infra/environment.bicep --parameters "$temp/parameters.json" "$@" --output none 2>"$temp/deployment-error"; then
      return 0
    fi
    cat "$temp/deployment-error" >&2
    if [[ "$attempt" == 3 ]] || ! grep -q 'AadAuthOperationCannotBePerformedWhenServerIsNotAccessible' "$temp/deployment-error"; then
      return 1
    fi
    echo 'PostgreSQL todavía está cambiando de estado; reintento limitado en 30 s.'
    sleep 30
  done
}
deploy_group --name "$deployment"
outputs=$(az deployment group show --resource-group "$rg" --name "$deployment" --query properties.outputs -o json)
acr=$(jq -r '.registryName.value' <<< "$outputs")
server=$(jq -r '.registryServer.value' <<< "$outputs")
vault=$(jq -r '.keyVaultName.value' <<< "$outputs")
host=$(jq -r '.postgresHost.value' <<< "$outputs")
identity=$(jq -r '.identityName.value' <<< "$outputs")
principal=$(jq -r '.identityPrincipalId.value' <<< "$outputs")
if az containerapp show -g "$rg" -n "ca-ascua-$environment" --output none 2>/dev/null; then
  echo 'Plataforma actualizada. La aplicación ya existe; usar deploy.yml para cambiar de revisión.'
  exit 0
fi
echo 'Inicializando la clave JWT persistente en Key Vault.'
# Consultar nombres, no valores. Reintentar la propagación de RBAC sin regenerar una clave existente.
secrets=''
for attempt in {1..30}; do
  if secrets=$(az keyvault secret list --vault-name "$vault" --query '[].name' -o tsv 2>/dev/null); then break; fi
  if [[ "$attempt" == 30 ]]; then echo 'Key Vault: permisos no disponibles tras 5 minutos' >&2; exit 1; fi
  sleep 10
done
if ! printf '%s\n' "$secrets" | grep -qx 'jwt-signing-key'; then
  openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$temp/jwt.pem"
  az keyvault secret set --vault-name "$vault" --name jwt-signing-key --file "$temp/jwt.pem" --content-type application/x-pem-file --output none
  rm -f "$temp/jwt.pem"
fi
echo 'Inicializando el rol Entra de la aplicación en PostgreSQL.'
# IP del runner solo durante el bootstrap, retirada mediante trap incluso si falla.
ip=$(curl --fail --silent --show-error https://api.ipify.org)
[[ "$ip" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || exit 1
az postgres flexible-server firewall-rule create -g "$rg" --server-name "${host%%.*}" --name "$firewall" --start-ip-address "$ip" --end-ip-address "$ip" --output none
firewallCreated=true
export PGPASSWORD
PGPASSWORD=$(az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv)
connection="host=$host dbname=postgres user=id-ascua-github sslmode=require"
for attempt in {1..30}; do
  if psql "$connection" -v ON_ERROR_STOP=1 -c 'select 1' >/dev/null 2>&1; then break; fi
  if [[ "$attempt" == 30 ]]; then echo 'No se pudo autenticar el pipeline en Postgres' >&2; exit 1; fi
  sleep 10
done
# Nombres controlados por Bicep; psql escapa literal/identificador en las sustituciones.
psql "$connection" -v ON_ERROR_STOP=1 -v role="$identity" -v oid="$principal" <<'SQL'
SELECT format('SELECT * FROM pgaadauth_create_principal_with_oid(%L,%L,%L,false,false)', :'role', :'oid', 'service')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'role') \gexec
SQL
psql "host=$host dbname=ascua user=id-ascua-github sslmode=require" -v ON_ERROR_STOP=1 -v role="$identity" <<'SQL'
GRANT ALL ON SCHEMA public TO :"role";
ALTER DATABASE ascua OWNER TO :"role";
SQL
unset PGPASSWORD
echo 'Construyendo y escaneando la imagen de la aplicación.'
revision=$(git rev-parse HEAD)
image="$server/ascua-api:$revision"
az acr login --name "$acr" --output none
docker build -t "$image" .
# Trivy lo ejecuta infra.yml antes del push.
trivy image --exit-code 1 --severity CRITICAL --ignore-unfixed "$image"
docker push "$image"
echo 'Desplegando Container App y comprobando readiness.'
deploy_group --name "${deployment}-app" --parameters deployApplication=true image="$image"
url=$(az containerapp show -g "$rg" -n "ca-ascua-$environment" --query properties.configuration.ingress.fqdn -o tsv)
curl --fail --retry 20 --retry-all-errors --retry-delay 10 "https://$url/health/ready"
printf '\nURL: https://%s\n' "$url"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  printf '### Ascua %s\n\nURL: https://%s\n\nImagen: %s\n' "$environment" "$url" "$image" >> "$GITHUB_STEP_SUMMARY"
fi
