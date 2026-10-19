targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param prefix string = 'teamsrefdev'
param location string = 'swedencentral'
param cosmosLocation string = location
@allowed(['Balanced_B0', 'Balanced_B1', 'Balanced_B3', 'Balanced_B5'])
param redisSku string = 'Balanced_B0'
param webClientId string
param eventsClientId string
@maxLength(3)
param fixtureActorIds array = []
@description('Explicit one-time simulator membership migration. Preserve messages; clear after the seed job succeeds.')
param retiredFixtureActorId string = ''
param deployWorkloads bool = false
param provisionRbac bool = true
param enableRealtimeAdmission bool = false
@allowed(['Simulator', 'GraphPilot'])
param graphMode string = 'Simulator'
param graphPilotConfig object = {}
@description('Immutable registry image reference, including @sha256: digest. Required for workload deployment.')
param image string = ''
param tags object = {
  environment: 'dev'
  application: 'teams-reference'
  data: 'synthetic-only'
}

var suffix = uniqueString(resourceGroup().id, prefix)
var base = '${prefix}-${suffix}'
var tenantId = tenant().tenantId
var appSubnetName = 'container-apps-ready'
var appSubnetPrefix = '10.30.4.0/23'
var endpointSubnetName = 'private-endpoints'

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${base}-api'
  location: location
  tags: tags
}
resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${base}-worker'
  location: location
  tags: tags
}
resource network 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${base}-vnet'
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: ['10.30.0.0/16'] }
    subnets: [
      {
        // Retain the original subnet while Azure removes the incomplete first environment.
        name: 'container-apps'
        properties: {
          addressPrefix: '10.30.0.0/23'
          delegations: [{ name: 'container-apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
      {
        name: endpointSubnetName
        properties: { addressPrefix: '10.30.2.0/24', privateEndpointNetworkPolicies: 'Disabled' }
      }
      {
        name: appSubnetName
        properties: {
          addressPrefix: appSubnetPrefix
          delegations: [{ name: 'container-apps', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
    ]
  }
}
resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${base}-logs'
  location: location
  tags: tags
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30, workspaceCapping: { dailyQuotaGb: 1 } }
}
resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${base}-insights'
  location: location
  tags: tags
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
}
resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: 'acr${replace(prefix, '-', '')}${suffix}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: cosmosLocation == location ? '${base}-cosmos' : '${base}-cosmos-${cosmosLocation}'
  location: cosmosLocation
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true
    publicNetworkAccess: 'Disabled'
    minimalTlsVersion: 'Tls12'
    capabilities: [{ name: 'EnableServerless' }]
    locations: [{ locationName: cosmosLocation, failoverPriority: 0, isZoneRedundant: false }]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    backupPolicy: {
      type: 'Periodic'
      periodicModeProperties: { backupIntervalInMinutes: 240, backupRetentionIntervalInHours: 8, backupStorageRedundancy: 'Local' }
    }
  }
}
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmos
  name: 'teams'
  properties: { resource: { id: 'teams' } }
}
resource state 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'state'
  properties: {
    resource: {
      id: 'state'
      partitionKey: { paths: ['/pk'], kind: 'Hash' }
      defaultTtl: -1
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [{ path: '/kind/?' }, { path: '/tenantId/?' }]
        excludedPaths: [{ path: '/*' }]
      }
    }
  }
}
resource bus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: '${base}-bus'
  location: location
  tags: tags
  sku: { name: 'Premium', tier: 'Premium', capacity: 1 }
  properties: { disableLocalAuth: true, publicNetworkAccess: 'Disabled', minimumTlsVersion: '1.2', premiumMessagingPartitions: 1 }
}
resource notifications 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: bus
  name: 'notifications'
  properties: {
    defaultMessageTimeToLive: 'PT1H'
    lockDuration: 'PT1M'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    maxSizeInMegabytes: 1024
  }
}
resource cache 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: redisSku == 'Balanced_B0' ? '${base}-redis' : '${base}-redis-${toLower(replace(redisSku, 'Balanced_', ''))}'
  location: location
  tags: tags
  sku: { name: redisSku }
  properties: { minimumTlsVersion: '1.2', highAvailability: 'Disabled', publicNetworkAccess: 'Disabled' }
}
resource cacheDb 'Microsoft.Cache/redisEnterprise/databases@2025-04-01' = {
  parent: cache
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    port: 10000
    clusteringPolicy: 'EnterpriseCluster'
    evictionPolicy: 'NoEviction'
    accessKeysAuthentication: 'Disabled'
    persistence: { aofEnabled: false, rdbEnabled: false }
  }
}
resource redisAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-04-01' = if (provisionRbac) {
  parent: cacheDb
  name: 'api'
  properties: { accessPolicyName: 'default', user: { objectId: apiIdentity.properties.principalId } }
}
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv${replace(prefix, '-', '')}${take(suffix, 8)}'
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    accessPolicies: []
    networkAcls: { bypass: 'AzureServices', defaultAction: 'Deny' }
  }
}
resource protectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: 'session-protection'
  properties: { kty: 'RSA', keySize: 2048, keyOps: ['wrapKey', 'unwrapKey'], attributes: { enabled: true } }
}
resource pubsub 'Microsoft.SignalRService/webPubSub@2024-03-01' = {
  name: '${base}-pubsub'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  sku: { name: 'Standard_S1', tier: 'Standard', capacity: 1 }
  properties: {
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    tls: { clientCertEnabled: false }
    networkACLs: {
      defaultAction: 'Allow'
      publicNetwork: { deny: ['RESTAPI', 'ServerConnection', 'Trace'] }
      privateEndpoints: []
    }
  }
}
resource environment 'Microsoft.App/managedEnvironments@2025-07-01' = {
  name: '${base}-env-ready'
  location: location
  tags: tags
  properties: {
    publicNetworkAccess: 'Disabled'
    vnetConfiguration: { infrastructureSubnetId: '${network.id}/subnets/${appSubnetName}', internal: false }
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    appLogsConfiguration: { destination: 'log-analytics', logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey } }
  }
}
module privateLinks './modules/private-link.bicep' = [for item in [
  { name: 'cosmos', resourceId: cosmos.id, group: 'Sql', zone: 'privatelink.documents.azure.com' }
  { name: 'bus', resourceId: bus.id, group: 'namespace', zone: 'privatelink.servicebus.windows.net' }
  { name: 'redis', resourceId: cache.id, group: 'redisEnterprise', zone: 'privatelink.redis.azure.net' }
  { name: 'vault', resourceId: vault.id, group: 'vault', zone: 'privatelink.vaultcore.azure.net' }
  { name: 'pubsub', resourceId: pubsub.id, group: 'webpubsub', zone: 'privatelink.webpubsub.azure.com' }
] : {
  name: '${prefix}-${item.name}-private'
  params: {
    name: '${base}-${item.name}-pe'
    location: location
    subnetId: '${network.id}/subnets/${endpointSubnetName}'
    vnetId: network.id
    serviceId: item.resourceId
    groupId: item.group
    zoneName: item.zone
  }
}]
resource cosmosRoles 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = [for index in range(0, 2): if (provisionRbac) {
  parent: cosmos
  name: guid(cosmos.id, string(index), 'state-contributor')
  properties: {
    principalId: [apiIdentity.properties.principalId, workerIdentity.properties.principalId][index]
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: '${cosmos.id}/dbs/teams/colls/state'
  }
  dependsOn: [state]
}]
resource pullRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for index in range(0, 2): if (provisionRbac) {
  name: guid(registry.id, string(index), 'AcrPull')
  scope: registry
  properties: {
    principalId: [apiIdentity.properties.principalId, workerIdentity.properties.principalId][index]
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}]
resource busSend 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionRbac) {
  name: guid(notifications.id, apiIdentity.id, 'sender')
  scope: notifications
  properties: { principalId: apiIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39') }
}
resource busReceive 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionRbac) {
  name: guid(notifications.id, workerIdentity.id, 'receiver')
  scope: notifications
  properties: { principalId: workerIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0') }
}
resource keyAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionRbac) {
  name: guid(vault.id, apiIdentity.id, 'crypto')
  scope: vault
  properties: { principalId: apiIdentity.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '14b46e9e-c2b7-41b4-b07b-48a6ebf60603') }
}
resource realtimeRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for index in range(0, 2): if (provisionRbac) {
  name: guid(pubsub.id, string(index), 'pubsub-owner')
  scope: pubsub
  properties: { principalId: [apiIdentity.properties.principalId, workerIdentity.properties.principalId][index], principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12cf5a90-567b-43ae-8102-96cf46c7d9b4') }
}]
module workloads './modules/workloads.bicep' = if (deployWorkloads) {
  name: '${prefix}-workloads'
  params: {
    base: base
    location: location
    environmentId: environment.id
    trustedProxyCidr: appSubnetPrefix
    registry: registry.properties.loginServer
    image: image
    apiIdentity: apiIdentity.id
    workerIdentity: workerIdentity.id
    apiClientId: apiIdentity.properties.clientId
    tenantId: tenantId
    webClientId: webClientId
    eventsClientId: eventsClientId
    pubsubName: pubsub.name
    pubsubPrincipalId: pubsub.identity.principalId
    enableRealtimeAdmission: enableRealtimeAdmission
    graphMode: graphMode
    graphPilotConfig: graphPilotConfig
    insightsConnectionString: insights.properties.ConnectionString
    fixtureActorIds: fixtureActorIds
    retiredFixtureActorId: retiredFixtureActorId
    cosmosEndpoint: cosmos.properties.documentEndpoint
    redisEndpoint: '${cache.properties.hostName}:10000'
    busEndpoint: '${bus.name}.servicebus.windows.net'
    pubsubEndpoint: 'https://${pubsub.properties.hostName}'
    protectionKey: '${vault.properties.vaultUri}keys/${protectionKey.name}'
  }
  dependsOn: [privateLinks, cosmosRoles, pullRoles, busSend, busReceive, keyAccess, redisAccess, realtimeRoles]
}

output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output apiIdentityId string = apiIdentity.id
output apiIdentityClientId string = apiIdentity.properties.clientId
output apiIdentityPrincipalId string = apiIdentity.properties.principalId
output workerIdentityClientId string = workerIdentity.properties.clientId
output environmentId string = environment.id
output publicOrigin string = deployWorkloads ? workloads!.outputs.publicOrigin : ''
output apiName string = '${base}-api'
output seedJobName string = '${base}-seed'
output pubsubPrincipalId string = pubsub.identity.principalId
output pubsubId string = pubsub.id
