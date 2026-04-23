@description('Region.')
param location string
@description('Miljønavn.')
param environmentName string
@description('Tags.')
param tags object
@description('Log Analytics workspace resource ID.')
param logAnalyticsWorkspaceId string
@description('App Insights connection string.')
param appInsightsConnectionString string

var abbrev = loadJsonContent('../abbreviations.json')
var envName = '${abbrev.containerAppsEnvironment}-${environmentName}-${abbrev.workload}'

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: split(logAnalyticsWorkspaceId, '/')[8]
}

resource cae 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: envName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    zoneRedundant: false
  }
}

output environmentId string = cae.id
output environmentName string = cae.name
output appInsightsConnectionStringOut string = appInsightsConnectionString
