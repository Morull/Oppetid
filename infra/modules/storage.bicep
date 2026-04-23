@description('Region.')
param location string
@description('Miljønavn.')
param environmentName string
@description('Tags.')
param tags object

var abbrev = loadJsonContent('../abbreviations.json')
// Storage-navn: 3-24 tegn, små bokstaver og sifre. uniqueString er 13 tegn; vi padder/trimmer til 24.
var saNameRaw = toLower('${abbrev.storageAccount}${abbrev.workload}${uniqueString(resourceGroup().id, environmentName)}')
var saNameCleaned = replace(replace(saNameRaw, '-', ''), '_', '')
var saNameSafe = length(saNameCleaned) > 24 ? substring(saNameCleaned, 0, 24) : saNameCleaned

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: saNameSafe
  location: location
  tags: tags
  sku: {
    name: 'Standard_ZRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
    containerDeleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource container 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'kraftverkuptime'
  properties: {
    publicAccess: 'None'
  }
}

output storageAccountName string = storage.name
output blobEndpoint string = storage.properties.primaryEndpoints.blob
