param environment string
param location string
param tags object
param workspaceId string
param email string

resource group 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-ascua-${environment}'
  location: 'global'
  tags: tags
  properties: {
    enabled: true
    groupShortName: 'ascua-${environment}'
    emailReceivers: [{ name: 'operador', emailAddress: email, useCommonAlertSchema: true }]
  }
}
var rules = [
  {
    name: 'errores'
    description: 'Solicitudes fallidas > 1% durante 5 minutos'
    query: 'AppRequests | summarize total=sum(ItemCount), fallidas=sumif(ItemCount, Success == false) | where total > 0 | project valor=100.0 * fallidas / total'
    threshold: 1
  }
  {
    name: 'partidas-faulted'
    description: 'Al menos una partida aislada como FAULTED en los últimos 5 minutos'
    query: 'AppMetrics | where Name == "ascua.matches.faulted" | summarize valor=sum(Sum)'
    threshold: 0
  }
]
resource alerts 'Microsoft.Insights/scheduledQueryRules@2023-12-01' = [for rule in rules: {
  name: 'alert-ascua-${environment}-${rule.name}'
  location: location
  kind: 'LogAlert'
  tags: tags
  properties: {
    displayName: rule.description
    description: rule.description
    enabled: true
    severity: 2
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    scopes: [workspaceId]
    skipQueryValidation: true
    criteria: {
      allOf: [{
        query: rule.query
        timeAggregation: 'Maximum'
        metricMeasureColumn: 'valor'
        operator: 'GreaterThan'
        threshold: rule.threshold
        failingPeriods: { numberOfEvaluationPeriods: 1, minFailingPeriodsToAlert: 1 }
      }]
    }
    actions: { actionGroups: [group.id] }
    autoMitigate: true
  }
}]
resource workbook 'Microsoft.Insights/workbooks@2023-06-01' = {
  name: guid(resourceGroup().id, 'ascua-workbook')
  location: location
  kind: 'shared'
  tags: tags
  properties: {
    displayName: 'Ascua ${environment}: operación'
    category: 'workbook'
    sourceId: workspaceId
    serializedData: replace(loadTextContent('workbook.json'), '__WORKSPACE_ID__', workspaceId)
  }
}
