targetScope = 'resourceGroup'

@allowed(['dev', 'prod'])
param environment string
param location string = 'westus2'
@description('West US 2 está restringido para PostgreSQL en Azure for Students; North Central US admite PostgreSQL 17/B1ms.')
param postgresLocation string = 'northcentralus'
@description('Object ID del administrador humano de PostgreSQL en el tenant de la suscripción.')
param postgresAdminObjectId string
param postgresAdminName string
param alertEmail string
@description('Identidad federada del pipeline; se añade como administrador Entra de PostgreSQL para el bootstrap.')
param deploymentPrincipalId string
param image string = ''
param deployApplication bool = false
param publicBaseUrl string = ''
param bootstrapUsername string = ''
@allowed(['0.1', '0.5', '1'])
param dailyQuotaGb string = '0.1'
param reviewUntil string = '2026-11-22'

var tags = { project: 'ascua', environment: environment, owner: 'Juan Pablo', managedBy: 'bicep', reviewUntil: reviewUntil }
module platform 'modules/platform.bicep' = {
  name: 'ascua-${environment}-platform'
  params: {
    environment: environment
    location: location
    postgresLocation: postgresLocation
    tags: tags
    postgresAdminObjectId: postgresAdminObjectId
    postgresAdminName: postgresAdminName
    deploymentPrincipalId: deploymentPrincipalId
    alertEmail: alertEmail
    dailyQuotaGb: dailyQuotaGb
  }
}
module application 'modules/containerapp.bicep' = if (deployApplication) {
  name: 'ascua-${environment}-application'
  params: {
    environment: environment
    location: location
    tags: tags
    image: image
    identityId: platform.outputs.identityId
    identityName: platform.outputs.identityName
    identityClientId: platform.outputs.identityClientId
    containerEnvironmentId: platform.outputs.containerEnvironmentId
    environmentDefaultDomain: platform.outputs.environmentDefaultDomain
    registryServer: platform.outputs.registryServer
    postgresHost: platform.outputs.postgresHost
    redisConnection: platform.outputs.redisConnection
    keyVaultUri: platform.outputs.keyVaultUri
    insightsConnection: platform.outputs.insightsConnection
    workspaceId: platform.outputs.workspaceId
    publicBaseUrl: publicBaseUrl
    bootstrapUsername: bootstrapUsername
  }
}
output resourceGroup string = resourceGroup().name
output identityId string = platform.outputs.identityId
output identityName string = platform.outputs.identityName
output identityPrincipalId string = platform.outputs.identityPrincipalId
output registryName string = platform.outputs.registryName
output registryServer string = platform.outputs.registryServer
output keyVaultName string = platform.outputs.keyVaultName
output postgresHost string = platform.outputs.postgresHost
output appName string = 'ca-ascua-${environment}'
output appUrl string = deployApplication ? application!.outputs.appUrl : ''
