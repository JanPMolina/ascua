targetScope = 'subscription'
param location string = 'westus2'
var environments = ['dev', 'prod']
resource groups 'Microsoft.Resources/resourceGroups@2024-03-01' = [for env in environments: {
  name: 'rg-ascua-${env}'
  location: location
  tags: { project: 'ascua', environment: env, reviewUntil: '2026-11-22' }
}]
resource bootstrapGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-ascua-bootstrap'
  location: location
  tags: { project: 'ascua', purpose: 'OIDC GitHub Actions' }
}
module identity 'modules/github-identity.bicep' = {
  name: 'ascua-github-identity'
  scope: bootstrapGroup
  params: { location: location }
}
module permissions 'modules/pipeline-permissions.bicep' = [for (env, i) in environments: {
  name: 'ascua-${env}-permissions'
  scope: groups[i]
  params: { principalId: identity.outputs.principalId }
}]
output clientId string = identity.outputs.clientId
output principalId string = identity.outputs.principalId
output tenantId string = tenant().tenantId
output subscriptionId string = subscription().subscriptionId
