@description('Region.')
param location string
@description('Miljønavn.')
param environmentName string
@description('Tags.')
param tags object
@description('Admin-bruker.')
param administratorLogin string
@secure()
@description('Admin-passord.')
param administratorLoginPassword string

var abbrev = loadJsonContent('../abbreviations.json')
var serverName = '${abbrev.postgres}-${environmentName}-${abbrev.workload}'

resource server 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: serverName
  location: location
  tags: tags
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorLoginPassword
    storage: {
      storageSizeGB: 32
      autoGrow: 'Enabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

resource db 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: server
  name: 'kraftverk'
  properties: {
    charset: 'UTF8'
    collation: 'nb_NO.UTF-8'
  }
}

// Tillat Azure-tjenester (inkl. Container Apps) i v1. Strammes inn med private endpoint i v2.
resource fwAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: server
  name: 'AllowAllAzureIPs'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverFqdn string = server.properties.fullyQualifiedDomainName
output databaseName string = db.name
