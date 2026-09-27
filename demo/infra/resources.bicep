@description('Azure region for all resources.')
param location string

@description('Short unique token used to build globally-unique resource names.')
param resourceToken string

param tags object

// Consumption plan (Y1), Windows: .NET 10 isolated is supported on every Windows and Linux
// hosting plan except Linux Consumption, so plain old Windows Consumption is the cheapest
// option that still runs it — the right trade for a demo, not a production workload.
var storageAccountName = 'st${resourceToken}'
var hostingPlanName = 'plan-${resourceToken}'
var functionAppName = 'func-${resourceToken}'

// The azd service key this must be tagged with — see services.transform in ../azure.yaml —
// so `azd deploy` knows which resource to zip-deploy the compiled function into.
var serviceTag = 'transform'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

resource hostingPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: hostingPlanName
  location: location
  tags: tags
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {
    reserved: false
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: union(tags, { 'azd-service-name': serviceTag })
  kind: 'functionapp'
  properties: {
    serverFarmId: hostingPlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      // No auth, no allow-list — this is a stage demo the whole room hits from one HTML page.
      cors: {
        allowedOrigins: [
          '*'
        ]
      }
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${storageAccount.listKeys().keys[0].value}'
        }
        {
          name: 'WEBSITE_CONTENTSHARE'
          value: toLower(functionAppName)
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'WEBSITE_RUN_FROM_PACKAGE'
          value: '1'
        }
      ]
    }
  }
}

output functionAppName string = functionApp.name
output transformUrl string = 'https://${functionApp.properties.defaultHostName}/Transform'
