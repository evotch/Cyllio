targetScope = 'resourceGroup'
param location string = resourceGroup().location
@allowed(['dev', 'prod'])
param environment string
param projectName string = 'cyllio'
var suffix = uniqueString(resourceGroup().id)
var prefix = '${projectName}-${environment}'
var tags = { project: projectName, environment: environment }
resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'cr${replace(projectName, '-', '')}${environment}${suffix}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${prefix}-pull'
  location: location
  tags: tags
}
resource pullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, 'AcrPull')
  scope: registry
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}
resource appsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-apps'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}
output registryName string = registry.name
output registryServer string = registry.properties.loginServer
output identityId string = identity.id
output environmentId string = appsEnvironment.id
