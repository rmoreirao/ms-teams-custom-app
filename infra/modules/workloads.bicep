param base string
param location string
param environmentId string
param trustedProxyCidr string
param registry string
param image string
param apiIdentity string
param workerIdentity string
param apiClientId string
param tenantId string
param webClientId string
param eventsClientId string
param pubsubName string
param pubsubPrincipalId string
param enableRealtimeAdmission bool = false
param graphMode string = 'Simulator'
param graphPilotConfig object = {}
@secure()
param insightsConnectionString string
@minLength(3)
@maxLength(3)
param fixtureActorIds array
param retiredFixtureActorId string = ''
param cosmosEndpoint string
param redisEndpoint string
param busEndpoint string
param pubsubEndpoint string
param protectionKey string

resource edge 'Microsoft.Cdn/profiles@2024-02-01' = {
  name: '${base}-edge'
  location: 'global'
  sku: { name: 'Premium_AzureFrontDoor' }
}
resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: edge
  name: '${base}-web'
  location: 'global'
  properties: { enabledState: 'Enabled' }
}
var common = [
  { name: 'App__Store', value: 'Cosmos' }
  { name: 'App__Auth', value: 'Entra' }
  { name: 'App__TenantId', value: tenantId }
  { name: 'AzureAd__TenantId', value: tenantId }
  { name: 'AzureAd__ClientId', value: webClientId }
  { name: 'App__PublicOrigin', value: 'https://${endpoint.properties.hostName}' }
  { name: 'App__TrustedProxyNetworks__0', value: trustedProxyCidr }
  { name: 'Azure__CosmosEndpoint', value: cosmosEndpoint }
  { name: 'Azure__Redis', value: redisEndpoint }
  { name: 'Azure__ServiceBus', value: busEndpoint }
  { name: 'Azure__PubSub', value: pubsubEndpoint }
  { name: 'Azure__ProtectionKey', value: protectionKey }
  { name: 'Callbacks__Audience', value: 'api://${eventsClientId}' }
  { name: 'Callbacks__ClientId', value: eventsClientId }
  { name: 'Callbacks__PrincipalId', value: pubsubPrincipalId }
  { name: 'App__RealtimeAdmissionReady', value: string(enableRealtimeAdmission) }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insightsConnectionString }
]
var actors = [for (id, index) in fixtureActorIds: { name: 'App__FixtureActorIds__${index}', value: id }]
resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${base}-api'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${apiIdentity}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, targetPort: 8080, transport: 'http', allowInsecure: false }
      registries: [{ server: registry, identity: apiIdentity }]
    }
    template: {
      containers: [{
        name: 'api'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: concat(common, actors, [
          { name: 'App__Role', value: 'Api' }
          { name: 'App__GraphMode', value: graphMode }
          { name: 'GraphPilot__Configuration', value: string(graphPilotConfig) }
          { name: 'AzureClientId', value: apiClientId }
          { name: 'AZURE_CLIENT_ID', value: apiClientId }
          { name: 'AzureAd__ClientCredentials__0__SourceType', value: 'SignedAssertionFromManagedIdentity' }
          { name: 'AzureAd__ClientCredentials__0__ManagedIdentityClientId', value: apiClientId }
        ])
        probes: [
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, initialDelaySeconds: 30, periodSeconds: 20 }
          // The initial release must start its seed job before dependency readiness can pass.
          { type: 'Readiness', httpGet: { path: enableRealtimeAdmission ? '/health/ready' : '/health/live', port: 8080 }, initialDelaySeconds: 15, periodSeconds: 30 }
        ]
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [hub]
}
resource workerMi 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: last(split(workerIdentity, '/')) }
resource worker 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${base}-worker'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentity}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    configuration: { registries: [{ server: registry, identity: workerIdentity }] }
    template: {
      containers: [{
        name: 'worker'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: concat(common, actors, [
          { name: 'App__Role', value: 'Worker' }
          { name: 'App__GraphMode', value: 'Simulator' }
          { name: 'AzureClientId', value: workerMi.properties.clientId }
          { name: 'AZURE_CLIENT_ID', value: workerMi.properties.clientId }
        ])
      }]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}
resource seed 'Microsoft.App/jobs@2024-03-01' = {
  name: '${base}-seed'
  location: location
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentity}': {} } }
  properties: {
    environmentId: environmentId
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 300
      replicaRetryLimit: 0
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      registries: [{ server: registry, identity: workerIdentity }]
    }
    template: {
      containers: [{
        name: 'seed'
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: concat(common, actors, [
          { name: 'App__Role', value: 'Seed' }
          { name: 'App__GraphMode', value: 'Simulator' }
          { name: 'App__RetiredFixtureActorId', value: retiredFixtureActorId }
          { name: 'AzureClientId', value: workerMi.properties.clientId }
          { name: 'AZURE_CLIENT_ID', value: workerMi.properties.clientId }
        ])
      }]
    }
  }
}
resource originGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: edge
  name: 'api'
  properties: {
    loadBalancingSettings: { sampleSize: 4, successfulSamplesRequired: 3, additionalLatencyInMilliseconds: 50 }
    healthProbeSettings: { probePath: '/health/live', probeProtocol: 'Https', probeRequestType: 'GET', probeIntervalInSeconds: 60 }
  }
}
resource origin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: originGroup
  name: 'api'
  properties: {
    hostName: api.properties.configuration.ingress.fqdn
    originHostHeader: api.properties.configuration.ingress.fqdn
    httpsPort: 443
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
    sharedPrivateLinkResource: {
      privateLink: { id: environmentId }
      groupId: 'managedEnvironments'
      privateLinkLocation: location
      requestMessage: 'Teams reference dev Front Door origin'
    }
  }
}
resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: endpoint
  name: 'application'
  properties: {
    originGroup: { id: originGroup.id }
    supportedProtocols: ['Http', 'Https']
    patternsToMatch: ['/*']
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    enabledState: 'Enabled'
  }
  dependsOn: [origin]
}
resource waf 'Microsoft.Network/frontDoorWebApplicationFirewallPolicies@2024-02-01' = {
  name: '${replace(base, '-', '')}waf'
  location: 'global'
  sku: { name: 'Premium_AzureFrontDoor' }
  properties: {
    policySettings: { enabledState: 'Enabled', mode: 'Prevention', requestBodyCheck: 'Enabled' }
    managedRules: { managedRuleSets: [{ ruleSetType: 'Microsoft_DefaultRuleSet', ruleSetVersion: '2.1', ruleSetAction: 'Block' }] }
  }
}
resource security 'Microsoft.Cdn/profiles/securityPolicies@2024-02-01' = {
  parent: edge
  name: 'waf'
  properties: { parameters: {
    type: 'WebApplicationFirewall'
    wafPolicy: { id: waf.id }
    associations: [{ domains: [{ id: endpoint.id }], patternsToMatch: ['/*'] }]
  } }
}
resource pubsub 'Microsoft.SignalRService/webPubSub@2024-03-01' existing = { name: pubsubName }
resource hub 'Microsoft.SignalRService/webPubSub/hubs@2024-03-01' = if (enableRealtimeAdmission) {
  parent: pubsub
  name: 'messaging'
  properties: {
    eventHandlers: [{
      urlTemplate: 'https://${endpoint.properties.hostName}/callbacks/pubsub'
      systemEvents: ['connect']
      auth: { type: 'ManagedIdentity', managedIdentity: { resource: 'api://${eventsClientId}' } }
    }]
  }
}
output publicOrigin string = 'https://${endpoint.properties.hostName}'
