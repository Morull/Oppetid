targetScope = 'subscription'

@description('Miljønavn brukt i ressursnavn (dev, test, prod).')
param environmentName string

@description('Azure-region for alle ressurser.')
param location string

@description('Objekt-ID for brukeren som får KV-admin under provisjonering. Hentes automatisk av azd.')
param principalId string = ''

@description('Postgres-administrator-brukernavn.')
param postgresAdminUser string = 'kraftverkuptime'

@secure()
@description('Postgres-administrator-passord. Hentes fra azd-secret eller Key Vault.')
param postgresAdminPassword string

var abbrev = loadJsonContent('abbreviations.json')
var tags = {
  'azd-env-name': environmentName
  workload: 'kraftverkuptime'
  owner: 'kraftverkuptime'
  env: environmentName
}

var rgName = 'rg-${environmentName}-${abbrev.workload}'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: rgName
  location: location
  tags: tags
}

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  scope: rg
  params: {
    location: location
    environmentName: environmentName
    tags: tags
  }
}

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  scope: rg
  params: {
    location: location
    environmentName: environmentName
    tags: tags
    principalId: principalId
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    location: location
    environmentName: environmentName
    tags: tags
  }
}

module postgres 'modules/postgres.bicep' = {
  name: 'postgres'
  scope: rg
  params: {
    location: location
    environmentName: environmentName
    tags: tags
    administratorLogin: postgresAdminUser
    administratorLoginPassword: postgresAdminPassword
  }
}

module containerEnv 'modules/containerAppsEnv.bicep' = {
  name: 'containerAppsEnv'
  scope: rg
  params: {
    location: location
    environmentName: environmentName
    tags: tags
    logAnalyticsWorkspaceId: monitoring.outputs.logAnalyticsId
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
  }
}

output RESOURCE_GROUP_NAME string = rg.name
output AZURE_LOCATION string = location
output AZURE_KEY_VAULT_ENDPOINT string = keyvault.outputs.vaultUri
output AZURE_STORAGE_ACCOUNT_NAME string = storage.outputs.storageAccountName
output POSTGRES_HOSTNAME string = postgres.outputs.serverFqdn
output CONTAINER_APPS_ENVIRONMENT_NAME string = containerEnv.outputs.environmentName
output APPLICATIONINSIGHTS_CONNECTION_STRING string = monitoring.outputs.appInsightsConnectionString
