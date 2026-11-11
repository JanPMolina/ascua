param environment string
param location string
param tags object
param image string
param identityId string
param identityName string
param identityClientId string
param containerEnvironmentId string
param environmentDefaultDomain string
param registryServer string
param postgresHost string
param redisConnection string
param keyVaultUri string
param insightsConnection string
param workspaceId string
param publicBaseUrl string
param bootstrapUsername string
var appName = 'ca-ascua-${environment}'
var baseUrl = empty(publicBaseUrl) ? 'https://${appName}.${environmentDefaultDomain}' : publicBaseUrl

resource app 'Microsoft.App/containerApps@2025-01-01' = {
  name: appName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    managedEnvironmentId: containerEnvironmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Multiple'
      maxInactiveRevisions: 10
      ingress: {
        external: true
        allowInsecure: false
        targetPort: 8080
        transport: 'auto'
        traffic: [{ latestRevision: true, weight: 100 }]
      }
      registries: [{ server: registryServer, identity: identityId }]
    }
    template: {
      terminationGracePeriodSeconds: 30
      containers: [{
        name: 'api'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: [
          { name: 'ASPNETCORE_ENVIRONMENT', value: environment == 'dev' ? 'dev' : 'Production' }
          { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          { name: 'Ascua__Azure__ManagedIdentityClientId', value: identityClientId }
          { name: 'ConnectionStrings__Postgres', value: 'Host=${postgresHost};Database=ascua;Username=${identityName};SSL Mode=Require' }
          { name: 'Ascua__Database__UseEntraAuth', value: 'true' }
          { name: 'Ascua__Database__MigrateOnStartup', value: 'true' }
          { name: 'ConnectionStrings__Redis', value: redisConnection }
          { name: 'Ascua__Redis__UseEntraAuth', value: 'true' }
          { name: 'Ascua__KeyVaultUri', value: keyVaultUri }
          { name: 'Ascua__Jwt__KeyVaultSecretName', value: 'jwt-signing-key' }
          { name: 'Ascua__Jwt__AllowEphemeralKey', value: 'false' }
          { name: 'Ascua__TrustForwardedHeaders', value: 'true' }
          { name: 'Ascua__Cors__AllowedOrigins__0', value: baseUrl }
          { name: 'Ascua__Realtime__PublicBaseUrl', value: baseUrl }
          { name: 'Ascua__Realtime__EnableDevEndpoints', value: 'false' }
          { name: 'Ascua__Admin__BootstrapUsername', value: bootstrapUsername }
          { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insightsConnection }
        ]
        probes: [
          { type: 'Startup', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 5, failureThreshold: 24 }
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 10, failureThreshold: 3 }
          { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, periodSeconds: 10, failureThreshold: 3 }
        ]
      }]
      scale: {
        minReplicas: environment == 'prod' ? 2 : 0
        maxReplicas: environment == 'prod' ? 4 : 2
        rules: [{ name: 'http-concurrency', http: { metadata: { concurrentRequests: '100' } } }]
      }
    }
  }
}
output appUrl string = 'https://${app.properties.configuration.ingress.fqdn}'

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'metrics-to-workspace'
  scope: app
  properties: {
    workspaceId: workspaceId
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}
resource restarts 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-ascua-${environment}-reinicios'
  location: 'global'
  tags: tags
  properties: {
    description: 'Reinicios de réplicas de Ascua en los últimos cinco minutos'
    severity: 2
    enabled: true
    scopes: [app.id]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [{
        name: 'restarts'
        criterionType: 'StaticThresholdCriterion'
        metricNamespace: 'Microsoft.App/containerApps'
        metricName: 'RestartCount'
        operator: 'GreaterThan'
        threshold: 0
        timeAggregation: 'Total'
      }]
    }
    actions: [{ actionGroupId: resourceId('Microsoft.Insights/actionGroups', 'ag-ascua-${environment}') }]
    autoMitigate: true
  }
}
