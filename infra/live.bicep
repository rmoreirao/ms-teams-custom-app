targetScope = 'resourceGroup'

@description('Deploy into a dedicated approved platform; this does not change Entra or certify platform capacity.')
@minLength(3)
@maxLength(12)
param prefix string
param location string
param environmentId string
param registryName string
param cosmosAccountName string
param serviceBusName string
param redisName string
param pubsubName string
param vaultName string
param sessionKeyName string = 'session-protection'
param notificationKeyNames array
param webClientId string
param syncClientId string
param eventsClientId string
param trustedProxyCidr string
param insightsConnectionString string = ''
@description('Reviewed nonsecret LiveOptions configuration; never include credentials, private certificates, or tokens.')
param liveConfig object
param deployWorkloads bool = false
param provisionRbac bool = false
param enableSynchronization bool = false
param enableAdmission bool = false
param enableMonitoring bool = false
param logsWorkspaceId string = ''
param actionGroupId string = ''
param image string = ''
param tags object = { environment: 'live', application: 'teams-reference' }

var roles = ['Api', 'Receiver', 'Projector', 'Delivery', 'Control']
var base = '${prefix}-${uniqueString(resourceGroup().id, prefix)}'
resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for role in roles: {
  name: '${base}-${toLower(role)}'
  location: location
  tags: tags
}]
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = { name: cosmosAccountName }
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' existing = {
  parent: cosmos
  name: 'teams'
}
var containerNames = ['live-state', 'live-authority', 'live-control']
resource containers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = [for name in containerNames: {
  parent: database
  name: name
  properties: {
    resource: {
      id: name
      partitionKey: { paths: ['/pk'], kind: 'Hash' }
      defaultTtl: -1
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          { path: '/id/?' }
          { path: '/kind/?' }
          { path: '/tenantId/?' }
          { path: '/conversationId/?' }
          { path: '/actorId/?' }
          { path: '/parent/?' }
          { path: '/pending/?' }
          { path: '/sortTime/?' }
          { path: '/sourceId/?' }
          { path: '/revision/?' }
        ]
        excludedPaths: [{ path: '/*' }]
        compositeIndexes: [
          [{ path: '/sortTime', order: 'descending' }, { path: '/sourceId', order: 'ascending' }]
          [{ path: '/sortTime', order: 'ascending' }, { path: '/id', order: 'ascending' }]
        ]
      }
    }
  }
}]
resource leases 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'live-leases'
  properties: { resource: {
    id: 'live-leases'
    partitionKey: { paths: ['/id'], kind: 'Hash' }
  } }
}
resource bus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = { name: serviceBusName }
resource notifications 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: bus
  name: 'graph-notifications'
  properties: {
    defaultMessageTimeToLive: 'PT1H'
    lockDuration: 'PT1M'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    maxSizeInMegabytes: 5120
  }
}
resource delivery 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: bus
  name: 'graph-delivery'
  properties: {
    defaultMessageTimeToLive: 'PT1H'
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    deadLetteringOnMessageExpiration: true
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
    maxSizeInMegabytes: 5120
  }
}
resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = { name: registryName }
resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' existing = { name: redisName }
resource redisDb 'Microsoft.Cache/redisEnterprise/databases@2025-04-01' existing = { parent: redis, name: 'default' }
resource pubsub 'Microsoft.SignalRService/webPubSub@2024-03-01' existing = { name: pubsubName }
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: vaultName }
resource sessionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = { parent: vault, name: sessionKeyName }
resource notificationKeys 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = [for name in notificationKeyNames: {
  parent: vault
  name: name
}]

var cosmosAccess = [
  { role: 0, container: 0, permission: '00000000-0000-0000-0000-000000000002' }
  { role: 0, container: 1, permission: '00000000-0000-0000-0000-000000000001' }
  { role: 1, container: 2, permission: '00000000-0000-0000-0000-000000000001' }
  { role: 2, container: 0, permission: '00000000-0000-0000-0000-000000000002' }
  { role: 2, container: 2, permission: '00000000-0000-0000-0000-000000000002' }
  { role: 3, container: 0, permission: '00000000-0000-0000-0000-000000000002' }
  { role: 3, container: 1, permission: '00000000-0000-0000-0000-000000000001' }
  { role: 4, container: 0, permission: '00000000-0000-0000-0000-000000000002' }
  { role: 4, container: 1, permission: '00000000-0000-0000-0000-000000000002' }
  { role: 4, container: 2, permission: '00000000-0000-0000-0000-000000000002' }
]
resource dataRoles 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = [for access in cosmosAccess: if (provisionRbac) {
  parent: cosmos
  name: guid(cosmos.id, identities[access.role].id, containerNames[access.container], access.permission)
  properties: {
    principalId: identities[access.role].properties.principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${access.permission}'
    scope: '${cosmos.id}/dbs/teams/colls/${containerNames[access.container]}'
  }
  dependsOn: [containers]
}]
resource leaseAccess 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = if (provisionRbac) {
  parent: cosmos
  name: guid(cosmos.id, identities[3].id, 'live-leases')
  properties: {
    principalId: identities[3].properties.principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: '${cosmos.id}/dbs/teams/colls/live-leases'
  }
  dependsOn: [leases]
}
resource pulls 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (role, index) in roles: if (provisionRbac) {
  name: guid(registry.id, identities[index].id, 'pull')
  scope: registry
  properties: {
    principalId: identities[index].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}]
resource senders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for index in [1, 2]: if (provisionRbac) {
  name: guid(notifications.id, identities[index].id, 'send')
  scope: notifications
  properties: {
    principalId: identities[index].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  }
}]
resource receiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionRbac) {
  name: guid(notifications.id, identities[2].id, 'receive')
  scope: notifications
  properties: {
    principalId: identities[2].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
  }
}
resource deliveryAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for permission in [
  '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
]: if (provisionRbac) {
  name: guid(delivery.id, identities[3].id, permission)
  scope: delivery
  properties: {
    principalId: identities[3].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', permission)
  }
}]
resource redisAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-04-01' = [for index in [0, 4]: if (provisionRbac) {
  parent: redisDb
  name: 'live-${toLower(roles[index])}'
  properties: { accessPolicyName: 'default', user: { objectId: identities[index].properties.principalId } }
}]
resource realtime 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for index in [0, 3, 4]: if (provisionRbac) {
  name: guid(pubsub.id, identities[index].id, 'realtime')
  scope: pubsub
  properties: {
    principalId: identities[index].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12cf5a90-567b-43ae-8102-96cf46c7d9b4')
  }
}]
resource sessionProtection 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionRbac) {
  name: guid(sessionKey.id, identities[0].id, 'wrap')
  scope: sessionKey
  properties: {
    principalId: identities[0].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '14b46e9e-c2b7-41b4-b07b-48a6ebf60603')
  }
}
resource decryption 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, index) in notificationKeyNames: if (provisionRbac) {
  name: guid(notificationKeys[index].id, identities[2].id, 'decrypt')
  scope: notificationKeys[index]
  properties: {
    principalId: identities[2].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')
  }
}]
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
var origin = 'https://${endpoint.properties.hostName}'
var configuration = union(liveConfig, {
  syncClientId: syncClientId
  controlIdentityClientId: identities[4].properties.clientId
  notificationUrl: '${origin}/callbacks/graph/notifications'
  lifecycleUrl: '${origin}/callbacks/graph/lifecycle'
  synchronizationEnabled: enableSynchronization
  admissionEnabled: enableAdmission
})
var common = [
  { name: 'App__Store', value: 'Cosmos' }
  { name: 'App__Auth', value: 'Entra' }
  { name: 'App__GraphMode', value: 'Live' }
  { name: 'App__TenantId', value: tenant().tenantId }
  { name: 'App__PublicOrigin', value: origin }
  { name: 'App__TrustedProxyNetworks__0', value: trustedProxyCidr }
  { name: 'App__RealtimeAdmissionReady', value: string(enableAdmission) }
  { name: 'AzureAd__TenantId', value: tenant().tenantId }
  { name: 'AzureAd__ClientId', value: webClientId }
  { name: 'Live__Configuration', value: string(configuration) }
  { name: 'Azure__CosmosEndpoint', value: cosmos.properties.documentEndpoint }
  { name: 'Azure__Redis', value: '${redis.properties.hostName}:10000' }
  { name: 'Azure__ServiceBus', value: '${bus.name}.servicebus.windows.net' }
  { name: 'Azure__PubSub', value: 'https://${pubsub.properties.hostName}' }
  { name: 'Azure__ProtectionKey', value: '${vault.properties.vaultUri}keys/${sessionKeyName}' }
  { name: 'Callbacks__Audience', value: 'api://${eventsClientId}' }
  { name: 'Callbacks__ClientId', value: eventsClientId }
  { name: 'Callbacks__PrincipalId', value: pubsub.identity.principalId }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: insightsConnectionString }
]
resource apps 'Microsoft.App/containerApps@2025-07-01' = [for (role, index) in roles: if (deployWorkloads) {
  name: '${base}-${toLower(role)}'
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${identities[index].id}': {} } }
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: index < 2 ? { external: true, targetPort: 8080, transport: 'http', allowInsecure: false } : null
      registries: [{ server: registry.properties.loginServer, identity: identities[index].id }]
    }
    template: {
      containers: [{
        name: toLower(role)
        image: image
        resources: { cpu: json('0.5'), memory: '1Gi' }
        env: concat(common, [
          { name: 'App__Role', value: role }
          { name: 'AzureClientId', value: identities[index].properties.clientId }
          { name: 'AZURE_CLIENT_ID', value: identities[index].properties.clientId }
        ], index == 0 ? [
          { name: 'AzureAd__ClientCredentials__0__SourceType', value: 'SignedAssertionFromManagedIdentity' }
          { name: 'AzureAd__ClientCredentials__0__ManagedIdentityClientId', value: identities[index].properties.clientId }
        ] : [])
        probes: index < 2 ? [
          { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, initialDelaySeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/health/live', port: 8080 }, initialDelaySeconds: 15 }
        ] : []
      }]
      scale: {
        minReplicas: index < 2 ? 2 : 1
        maxReplicas: index == 4 ? 1 : 20
        rules: index < 2 ? [{ name: 'http', http: { metadata: { concurrentRequests: '50' } } }] : index == 2 ? [{
          name: 'graph-inbox'
          custom: {
            type: 'azure-servicebus'
            identity: identities[index].id
            metadata: { queueName: notifications.name, namespace: bus.name, messageCount: '50' }
          }
        }] : index == 3 ? [{
          name: 'delivery-inbox'
          custom: {
            type: 'azure-servicebus'
            identity: identities[index].id
            metadata: { queueName: delivery.name, namespace: bus.name, messageCount: '50' }
          }
        }] : []
      }
    }
  }
  dependsOn: [containers, leases, dataRoles, leaseAccess, pulls, senders, receiver, deliveryAccess, redisAccess, realtime, sessionProtection, decryption]
}]
resource groups 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = [for index in [0, 1]: if (deployWorkloads) {
  parent: edge
  name: toLower(roles[index])
  properties: {
    loadBalancingSettings: { sampleSize: 4, successfulSamplesRequired: 3 }
    healthProbeSettings: { probePath: '/health/live', probeProtocol: 'Https', probeRequestType: 'GET', probeIntervalInSeconds: 30 }
  }
}]
resource origins 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = [for index in [0, 1]: if (deployWorkloads) {
  name: '${edge.name}/${toLower(roles[index])}/private'
  properties: {
    hostName: apps[index]!.properties.configuration.ingress!.fqdn!
    originHostHeader: apps[index]!.properties.configuration.ingress!.fqdn!
    httpsPort: 443
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
    sharedPrivateLinkResource: {
      privateLink: { id: environmentId }
      groupId: 'managedEnvironments'
      privateLinkLocation: location
      requestMessage: 'Approved live Teams private origin'
    }
  }
  dependsOn: [groups]
}]
resource routes 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = [for index in [0, 1]: if (deployWorkloads) {
  parent: endpoint
  name: toLower(roles[index])
  properties: {
    originGroup: { id: groups[index].id }
    supportedProtocols: ['Http', 'Https']
    patternsToMatch: index == 0 ? ['/*'] : ['/callbacks/graph/*']
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    enabledState: 'Enabled'
  }
  dependsOn: [origins]
}]
resource hub 'Microsoft.SignalRService/webPubSub/hubs@2024-03-01' = if (deployWorkloads && enableAdmission) {
  parent: pubsub
  name: 'messaging'
  properties: {
    eventHandlers: [{
      urlTemplate: '${origin}/callbacks/pubsub'
      systemEvents: ['connect']
      auth: { type: 'ManagedIdentity', managedIdentity: { resource: 'api://${eventsClientId}' } }
    }]
  }
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
output publicOrigin string = origin
output identityBindings array = [for (role, index) in roles: {
  role: role
  resourceId: identities[index].id
  clientId: identities[index].properties.clientId
  principalId: identities[index].properties.principalId
}]
output pubsubPrincipalId string = pubsub.identity.principalId
module monitoring './modules/live-monitoring.bicep' = if (enableMonitoring) {
  name: '${prefix}-live-monitoring'
  params: {
    prefix: prefix
    location: location
    busNamespaceId: bus.id
    logsWorkspaceId: logsWorkspaceId
    actionGroupId: actionGroupId
  }
}
