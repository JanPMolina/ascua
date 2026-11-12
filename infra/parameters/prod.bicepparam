using '../environment.bicep'
param environment = 'prod'
// Esta suscripción admite un Environment por región; dev ya ocupa West US 2.
param location = 'northcentralus'
param postgresAdminObjectId = readEnvironmentVariable('POSTGRES_ADMIN_OBJECT_ID')
param postgresAdminName = readEnvironmentVariable('POSTGRES_ADMIN_NAME')
param deploymentPrincipalId = readEnvironmentVariable('AZURE_PRINCIPAL_ID')
param alertEmail = readEnvironmentVariable('ALERT_EMAIL')
