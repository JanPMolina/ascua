param environment string
param location string
param tags object
param postgresAdminObjectId string
param postgresAdminName string
param deploymentPrincipalId string
param alertEmail string
param dailyQuotaGb string
var suffix = uniqueString(resourceGroup().id)
var identityName = 'id-ascua-${environment}'

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}
resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-ascua-${environment}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    workspaceCapping: { dailyQuotaGb: json(dailyQuotaGb) }
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
  }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-ascua-${environment}'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'acrascua${environment}${suffix}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}
resource pull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, 'AcrPull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-ascua-${environment}-${take(suffix, 7)}'
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
  }
}
resource readSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, identity.id, 'KeyVaultSecretsUser')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}
resource writeSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, deploymentPrincipalId, 'KeyVaultSecretsOfficer')
  scope: vault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
    principalId: deploymentPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Basic/Standard bloqueados para nuevos clientes desde abril de 2026.
// NoCluster mantiene los scripts Lua de varias claves sin CROSSSLOT.
resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: 'redis-ascua-${environment}-${suffix}'
  location: location
  tags: tags
  sku: { name: 'Balanced_B0' }
  properties: {
    highAvailability: environment == 'prod' ? 'Enabled' : 'Disabled'
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}
resource redisDatabase 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  parent: redis
  name: 'default'
  properties: {
    accessKeysAuthentication: 'Disabled'
    clientProtocol: 'Encrypted'
    clusteringPolicy: 'NoCluster'
    evictionPolicy: 'NoEviction'
    port: 10000
  }
}
resource redisAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-07-01' = {
  parent: redisDatabase
  name: 'ascua'
  properties: {
    accessPolicyName: 'default'
    user: { objectId: identity.properties.principalId }
  }
}

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: 'psql-ascua-${environment}-${suffix}'
  location: location
  tags: tags
  sku: { name: 'Standard_B1ms', tier: 'Burstable' }
  properties: {
    version: '17'
    createMode: 'Default'
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
      tenantId: tenant().tenantId
    }
    storage: { storageSizeGB: 32, autoGrow: 'Disabled' }
    backup: { backupRetentionDays: 7, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    network: { publicNetworkAccess: 'Enabled' }
  }
}
resource postgresAdmin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = {
  parent: postgres
  name: postgresAdminObjectId
  properties: {
    principalName: postgresAdminName
    principalType: 'User'
    tenantId: tenant().tenantId
  }
}
resource pipelineAdmin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01' = {
  parent: postgres
  name: deploymentPrincipalId
  properties: {
    principalName: 'id-ascua-github'
    principalType: 'ServicePrincipal'
    tenantId: tenant().tenantId
  }
  dependsOn: [postgresAdmin]
}
resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'ascua'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}
// Azure services only. Entra-only authentication still applies.
resource firewall 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}
resource containerEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: 'cae-ascua-${environment}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: workspace.properties.customerId
        sharedKey: workspace.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
  }
}
module monitoring 'monitoring.bicep' = {
  name: 'ascua-alerts'
  params: {
    environment: environment
    location: location
    tags: tags
    workspaceId: workspace.id
    email: alertEmail
  }
}
output identityId string = identity.id
output identityName string = identity.name
output identityClientId string = identity.properties.clientId
output identityPrincipalId string = identity.properties.principalId
output registryName string = registry.name
output registryServer string = registry.properties.loginServer
output keyVaultName string = vault.name
output keyVaultUri string = vault.properties.vaultUri
output postgresHost string = postgres.properties.fullyQualifiedDomainName
output redisConnection string = '${redis.properties.hostName}:10000,ssl=True,abortConnect=False'
output containerEnvironmentId string = containerEnvironment.id
output environmentDefaultDomain string = containerEnvironment.properties.defaultDomain
output insightsConnection string = insights.properties.ConnectionString
output workspaceId string = workspace.id
