@description('Region.')
param location string
@description('Miljønavn.')
param environmentName string
@description('Tags.')
param tags object
@description('Azure-objekt-ID som skal få KV Administrator under provisjonering.')
param principalId string

var abbrev = loadJsonContent('../abbreviations.json')
var kvName = '${abbrev.keyVault}-${environmentName}-${abbrev.workload}'

resource kv 'Microsoft.KeyVault/vaults@2024-04-01-preview' = {
  name: kvName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'standard'
      family: 'A'
    }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 30
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

var kvAdminRoleId = '00482a5a-887f-4fb3-b363-3b7fe8e74483' // Key Vault Administrator

resource assign 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(principalId)) {
  name: guid(kv.id, principalId, kvAdminRoleId)
  scope: kv
  properties: {
    principalId: principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', kvAdminRoleId)
    principalType: 'User'
  }
}

output vaultUri string = kv.properties.vaultUri
output keyVaultName string = kv.name
