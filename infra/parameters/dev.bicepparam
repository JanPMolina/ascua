using '../environment.bicep'
param environment = 'dev'
param location = readEnvironmentVariable('AZURE_LOCATION', 'eastus2')
param postgresAdminObjectId = readEnvironmentVariable('POSTGRES_ADMIN_OBJECT_ID')
param postgresAdminName = readEnvironmentVariable('POSTGRES_ADMIN_NAME')
param deploymentPrincipalId = readEnvironmentVariable('AZURE_PRINCIPAL_ID')
param alertEmail = readEnvironmentVariable('ALERT_EMAIL')
