targetScope = 'resourceGroup'

@description('Five isolated workloads on the existing private Dev platform. Does not create services, grant access or change source data.')
param location string
param environmentId string
param registryServer string
param image string
@description('Reviewed Api/Receiver/Projector/Delivery/Control bindings: appName, containerName, identityResourceId, clientId, minReplicas, maxReplicas.')
param roleBindings object
param webClientId string
param syncClientId string
param eventsClientId string
param pubsubPrincipalId string
param publicOrigin string
param trustedProxyCidr string
param cosmosEndpoint string
param redisEndpoint string
param busEndpoint string
param pubsubEndpoint string
param protectionKey string
type LiveConfiguration = {
  readConsistency: 'Session'
}
param liveConfig LiveConfiguration
param deployWorkloads bool = false
@allowed(['Api', 'Receiver', 'Projector', 'Delivery', 'Control'])
@description('Stage new roles separately without replacing either running legacy workload before the controller fence.')
param deployRoles array = ['Api', 'Receiver', 'Projector', 'Delivery', 'Control']
param enableSynchronization bool = false
param enableAdmission bool = false
@secure()
param insightsConnectionString string = ''
param tags object = { environment: 'Development', application: 'teams-reference' }

var roles = deployRoles
var clients = {
  Api: roleBindings.Api.clientId
  Receiver: roleBindings.Receiver.clientId
  Projector: roleBindings.Projector.clientId
  Delivery: roleBindings.Delivery.clientId
  Control: roleBindings.Control.clientId
}
var configuration = union(liveConfig, {
  authorityMode: 'Policy'
  entitlementProvider: 'Graph'
  roleIdentityClientIds: clients
  syncClientId: syncClientId
  controlIdentityClientId: roleBindings.Control.clientId
  notificationUrl: '${publicOrigin}/callbacks/graph/notifications'
  lifecycleUrl: '${publicOrigin}/callbacks/graph/lifecycle'
  synchronizationEnabled: enableSynchronization
  admissionEnabled: enableAdmission
})
var common = [
  { name: 'App__Store', value: 'Cosmos' }
  { name: 'App__Auth', value: 'Entra' }
  { name: 'App__GraphMode', value: 'Live' }
  { name: 'App__TenantId', value: tenant().tenantId }
  { name: 'App__PublicOrigin', value: publicOrigin }
  { name: 'App__TrustedProxyNetworks__0', value: trustedProxyCidr }
  { name: 'App__RealtimeAdmissionReady', value: enableAdmission ? 'true' : 'false' }
  { name: 'AzureAd__TenantId', value: tenant().tenantId }
  { name: 'AzureAd__ClientId', value: webClientId }
  { name: 'Azure__CosmosEndpoint', value: cosmosEndpoint }
  { name: 'Azure__Redis', value: redisEndpoint }
  { name: 'Azure__ServiceBus', value: busEndpoint }
  { name: 'Azure__PubSub', value: pubsubEndpoint }
  { name: 'Azure__ProtectionKey', value: protectionKey }
  { name: 'Callbacks__Audience', value: 'api://${eventsClientId}' }
  { name: 'Callbacks__ClientId', value: eventsClientId }
  { name: 'Callbacks__PrincipalId', value: pubsubPrincipalId }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insightsConnectionString }
]
resource apps 'Microsoft.App/containerApps@2025-07-01' = [for role in roles: if (deployWorkloads) {
  name: roleBindings[role].appName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${roleBindings[role].identityResourceId}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    workloadProfileName: roleBindings[role].?workloadProfileName ?? 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: role == 'Api' || role == 'Receiver' ? {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
        traffic: [{ latestRevision: true, weight: 100 }]
      } : null
      registries: [{ server: registryServer, identity: roleBindings[role].identityResourceId }]
    }
    template: {
      containers: [{
        name: roleBindings[role].containerName
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: concat(common, [
          { name: 'App__Role', value: role }
          { name: 'AzureClientId', value: roleBindings[role].clientId }
          { name: 'AZURE_CLIENT_ID', value: roleBindings[role].clientId }
          { name: 'Live__Configuration', value: string(union(configuration, { hostedRoles: [role] })) }
        ], role == 'Api' ? [
          { name: 'AzureAd__ClientCredentials__0__SourceType', value: 'SignedAssertionFromManagedIdentity' }
          { name: 'AzureAd__ClientCredentials__0__ManagedIdentityClientId', value: roleBindings.Api.clientId }
        ] : [])
        probes: role == 'Api' || role == 'Receiver' ? [
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, initialDelaySeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, initialDelaySeconds: 15 }
        ] : []
      }]
      scale: {
        minReplicas: roleBindings[role].minReplicas
        maxReplicas: roleBindings[role].maxReplicas
        cooldownPeriod: roleBindings[role].?cooldownPeriod ?? 300
        pollingInterval: roleBindings[role].?pollingInterval ?? 30
      }
    }
  }
}]
output workloads array = [for role in roles: {
  role: role
  name: roleBindings[role].appName
  identityResourceId: roleBindings[role].identityResourceId
}]
