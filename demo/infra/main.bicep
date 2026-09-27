targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the azd environment. Used to generate a short, unique resource token.')
param environmentName string

@minLength(1)
@description('Azure region for all resources.')
param location string

@description('Resource group to deploy into. Defaults to rg-<environmentName> when empty.')
param resourceGroupName string = ''

var resourceToken = toLower(uniqueString(subscription().id, environmentName, location))
var tags = {
  'azd-env-name': environmentName
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: !empty(resourceGroupName) ? resourceGroupName : 'rg-${environmentName}'
  location: location
  tags: tags
}

module demo 'resources.bicep' = {
  name: 'tlio-azure-demo-resources'
  scope: rg
  params: {
    location: location
    resourceToken: resourceToken
    tags: tags
  }
}

output AZURE_FUNCTION_NAME string = demo.outputs.functionAppName
output AZURE_FUNCTION_TRANSFORM_URL string = demo.outputs.transformUrl
