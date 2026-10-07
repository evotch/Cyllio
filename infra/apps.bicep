targetScope = 'resourceGroup'
param location string = resourceGroup().location
@allowed(['dev', 'prod'])
param environment string
param projectName string = 'cyllio'
param registryServer string
param identityId string
param environmentId string
@description('Immutable image tag, normally the Git commit SHA.')
param imageTag string
var services = loadJsonContent('../services.json')
resource apps 'Microsoft.App/containerApps@2024-03-01' = [for service in services: {
  name: '${projectName}-${environment}-${service.name}'
  location: location
  tags: { project: projectName, environment: environment }
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identityId}': {} }
  }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: service.external, targetPort: 8080, allowInsecure: false }
      registries: [{ server: registryServer, identity: identityId }]
    }
    template: {
      containers: [{
        name: service.name
        image: '${registryServer}/${service.image}:${imageTag}'
        env: [{ name: 'ASPNETCORE_ENVIRONMENT', value: environment == 'prod' ? 'Production' : 'Development' }]
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: [
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, initialDelaySeconds: 10, periodSeconds: 10 }
          { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, initialDelaySeconds: 5, periodSeconds: 5 }
        ]
      }]
      scale: { minReplicas: environment == 'prod' ? 1 : 0, maxReplicas: 3 }
    }
  }
}]
output serviceUrls array = [for (service, index) in services: {
  name: service.name
  external: service.external
  url: 'https://${apps[index].properties.configuration.ingress.fqdn}'
}]
