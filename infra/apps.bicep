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
@secure()
param mongoConnectionString string
@secure()
@minLength(32)
param authSigningKey string
@secure()
@minLength(32)
param authPhoneHashKey string
@secure()
param acsSmsConnectionString string = ''
param acsSmsSender string = ''
resource appEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: last(split(environmentId, '/'))
}
var accountServices = ['identity', 'device']
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
      secrets: concat(contains(accountServices, service.name) ? [
        { name: 'mongo-connection', value: mongoConnectionString }
        { name: 'auth-signing-key', value: authSigningKey }
        { name: 'auth-phone-hash-key', value: authPhoneHashKey }
      ] : [], service.name == 'identity' && !empty(acsSmsConnectionString) ? [
        { name: 'acs-sms-connection', value: acsSmsConnectionString }
      ] : [])
      ingress: { external: service.external, targetPort: 8080, allowInsecure: false }
      registries: [{ server: registryServer, identity: identityId }]
    }
    template: {
      containers: [{
        name: service.name
        image: '${registryServer}/${service.image}:${imageTag}'
        env: concat(
          [{ name: 'ASPNETCORE_ENVIRONMENT', value: environment == 'prod' ? 'Production' : 'Development' }],
          contains(accountServices, service.name) ? [
            { name: 'Mongo__ConnectionString', secretRef: 'mongo-connection' }
            { name: 'Auth__SigningKey', secretRef: 'auth-signing-key' }
            { name: 'Auth__PhoneHashKey', secretRef: 'auth-phone-hash-key' }
            { name: 'Auth__EnableDevelopmentOtp', value: 'false' }
          ] : [],
          service.name == 'identity' && !empty(acsSmsConnectionString) ? [
            { name: 'Auth__Sms__ConnectionString', secretRef: 'acs-sms-connection' }
            { name: 'Auth__Sms__Sender', value: acsSmsSender }
          ] : [],
          service.name == 'api' ? [
            { name: 'Services__Identity', value: 'https://${projectName}-${environment}-identity.internal.${appEnvironment.properties.defaultDomain}/' }
            { name: 'Services__Device', value: 'https://${projectName}-${environment}-device.internal.${appEnvironment.properties.defaultDomain}/' }
          ] : []
        )
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
