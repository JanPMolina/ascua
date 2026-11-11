targetScope = 'subscription'
@allowed(['dev', 'prod'])
param environment string
param location string = 'westus2'
@description('West US 2 está restringido para PostgreSQL en Azure for Students; North Central US admite PostgreSQL 17/B1ms.')
param postgresLocation string = 'northcentralus'
param postgresAdminObjectId string
param postgresAdminName string
param alertEmail string
param deploymentPrincipalId string
param image string = ''
param deployApplication bool = false
param publicBaseUrl string = ''
param bootstrapUsername string = ''
@allowed(['0.1', '0.5', '1'])
param dailyQuotaGb string = '0.1'
param reviewUntil string = '2026-11-22'
resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-ascua-${environment}'
  location: location
  tags: { project: 'ascua', environment: environment, reviewUntil: reviewUntil, managedBy: 'bicep' }
}
module resources 'environment.bicep' = {
  name: 'ascua-${environment}'
  scope: rg
  params: {
    environment: environment
    location: location
    postgresLocation: postgresLocation
    postgresAdminObjectId: postgresAdminObjectId
    postgresAdminName: postgresAdminName
    alertEmail: alertEmail
    deploymentPrincipalId: deploymentPrincipalId
    image: image
    deployApplication: deployApplication
    publicBaseUrl: publicBaseUrl
    bootstrapUsername: bootstrapUsername
    dailyQuotaGb: dailyQuotaGb
    reviewUntil: reviewUntil
  }
}
output appUrl string = resources.outputs.appUrl
output resourceGroup string = rg.name
