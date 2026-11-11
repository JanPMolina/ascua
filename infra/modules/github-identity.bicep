param location string
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-ascua-github'
  location: location
  tags: { project: 'ascua', purpose: 'OIDC GitHub Actions' }
}
resource federation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = [for env in ['dev', 'production']: {
  parent: identity
  name: 'github-${env}'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:JanPMolina/ascua:environment:${env}'
    audiences: ['api://AzureADTokenExchange']
  }
}]
output principalId string = identity.properties.principalId
output clientId string = identity.properties.clientId
