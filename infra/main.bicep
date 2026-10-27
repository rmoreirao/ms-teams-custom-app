targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param prefix string = 'teamsrefdev'
param location string = 'swedencentral'
param cosmosLocation string = location
@allowed(['Balanced_B0', 'Balanced_B1', 'Balanced_B3', 'Balanced_B5'])
param redisSku string = 'Balanced_B0'
@description('Reviewed existing Development namespace. When set, do not provision the default Premium namespace or its private endpoint.')
@maxLength(50)
param existingServiceBusName string = ''
param webClientId string
param eventsClientId string
param deployWorkloads bool = false
param deployOperations bool = false
param provisionRbac bool = true
@description('Reviewed bounded Development Live configuration. Provision scoped Live storage/access before workloads.')
param liveConfig object = {}
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
var selectedServiceBusName = empty(existingServiceBusName) ? '${base}-bus' : existingServiceBusName

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
resource waf 'Microsoft.Network/frontDoorWebApplicationFirewallPolicies@2024-02-01' = {
  name: '${replace(base, '-', '')}waf'
  location: 'global'
  sku: { name: 'Premium_AzureFrontDoor' }
  properties: {
    policySettings: { enabledState: 'Enabled', mode: 'Prevention', requestBodyCheck: 'Enabled' }
    managedRules: loadJsonContent('waf-managed-rules.json')
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
resource bus 'Microsoft.ServiceBus/namespaces@2024-01-01' = if (empty(existingServiceBusName)) {
  name: selectedServiceBusName
  location: location
  tags: tags
  sku: { name: 'Premium', tier: 'Premium', capacity: 1 }
  properties: { disableLocalAuth: true, publicNetworkAccess: 'Disabled', minimumTlsVersion: '1.2', premiumMessagingPartitions: 1 }
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
module privateLinks './modules/private-link.bicep' = [for item in concat([
  { name: 'cosmos', resourceId: cosmos.id, group: 'Sql', zone: 'privatelink.documents.azure.com' }
  { name: 'redis', resourceId: cache.id, group: 'redisEnterprise', zone: 'privatelink.redis.azure.net' }
  { name: 'vault', resourceId: vault.id, group: 'vault', zone: 'privatelink.vaultcore.azure.net' }
  { name: 'pubsub', resourceId: pubsub.id, group: 'webpubsub', zone: 'privatelink.webpubsub.azure.com' }
], empty(existingServiceBusName) ? [
  { name: 'bus', resourceId: bus!.id, group: 'namespace', zone: 'privatelink.servicebus.windows.net' }
] : []) : {
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
resource pullRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for index in range(0, 2): if (provisionRbac) {
  name: guid(registry.id, string(index), 'AcrPull')
  scope: registry
  properties: {
    principalId: [apiIdentity.properties.principalId, workerIdentity.properties.principalId][index]
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}]
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
    liveConfig: liveConfig
    deployOperations: deployOperations
    insightsConnectionString: insights.properties.ConnectionString
    cosmosEndpoint: cosmos.properties.documentEndpoint
    redisEndpoint: '${cache.properties.hostName}:10000'
    busEndpoint: '${selectedServiceBusName}.servicebus.windows.net'
    pubsubEndpoint: 'https://${pubsub.properties.hostName}'
    protectionKey: '${vault.properties.vaultUri}keys/${protectionKey.name}'
  }
  dependsOn: [privateLinks, pullRoles, keyAccess, redisAccess, realtimeRoles, endpoint, security]
}

output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output apiIdentityId string = apiIdentity.id
output apiIdentityClientId string = apiIdentity.properties.clientId
output apiIdentityPrincipalId string = apiIdentity.properties.principalId
output workerIdentityClientId string = workerIdentity.properties.clientId
output workerIdentityPrincipalId string = workerIdentity.properties.principalId
output environmentId string = environment.id
output publicOrigin string = 'https://${endpoint.properties.hostName}'
output apiName string = '${base}-api'
output pubsubPrincipalId string = pubsub.identity.principalId
output pubsubId string = pubsub.id
output cosmosAccountName string = cosmos.name
output serviceBusName string = selectedServiceBusName
output redisName string = cache.name
output vaultName string = vault.name
output pubsubName string = pubsub.name
