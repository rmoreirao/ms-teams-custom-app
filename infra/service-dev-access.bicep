targetScope = 'resourceGroup'

@description('Reviewed role grants and the bounded read-refresh queue only. Incremental deployment does not remove obsolete compact-role assignments; retirement must be separately reviewed and verified.')
param principals object
param registryName string
param cosmosAccountName string
param primaryReaderRoleId string
param serviceBusName string
param redisName string
param pubsubName string
param vaultName string
param sessionKeyName string
param notificationKeyNames array
param provisionAccess bool = false
@description('Existing exact role/scope/principal assignment names verified by inventory; avoids duplicating approved assignments.')
param existingDataAssignmentIds object = {}
param existingRoleAssignmentIds object = {}
param existingRedisAssignmentIds object = {}

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = { name: registryName }
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = { name: cosmosAccountName }
resource bus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = { name: serviceBusName }
module readRefresh 'modules/read-refresh.bicep' = if (provisionAccess) {
  name: 'service-dev-read-refresh'
  params: {
    serviceBusName: serviceBusName
    apiPrincipalId: principals.Api
    controlPrincipalId: principals.Control
    provisionAccess: true
    senderAssignmentName: existingRoleAssignmentIds[?'Api:read-refresh-send'] ?? ''
    receiverAssignmentName: existingRoleAssignmentIds[?'Control:read-refresh-receive'] ?? ''
  }
}
resource notifications 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' existing = { parent: bus, name: 'graph-notifications' }
resource delivery 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' existing = { parent: bus, name: 'graph-delivery' }
resource redis 'Microsoft.Cache/redisEnterprise@2025-07-01' existing = { name: redisName }
resource redisDb 'Microsoft.Cache/redisEnterprise/databases@2025-04-01' existing = { parent: redis, name: 'default' }
resource pubsub 'Microsoft.SignalRService/webPubSub@2024-03-01' existing = { name: pubsubName }
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: vaultName }
resource sessionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = { parent: vault, name: sessionKeyName }
resource notificationKeys 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = [for name in notificationKeyNames: { parent: vault, name: name }]
var roles = ['Api', 'Receiver', 'Projector', 'Delivery', 'Control']
var data = [
  { role: 'Api', container: 'live-state', write: true }
  { role: 'Api', container: 'live-authority', write: false }
  { role: 'Receiver', container: 'live-control', write: false }
  { role: 'Projector', container: 'live-state', write: true }
  { role: 'Projector', container: 'live-control', write: true }
  { role: 'Delivery', container: 'live-state', write: true }
  { role: 'Delivery', container: 'live-authority', write: false }
  { role: 'Delivery', container: 'live-leases', write: true }
  { role: 'Control', container: 'live-state', write: true }
  { role: 'Control', container: 'live-control', write: true }
  { role: 'Control', container: 'live-authority', write: true }
]
resource dataRoles 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = [for item in data: if (provisionAccess) {
  parent: cosmos
  name: existingDataAssignmentIds[?'${item.role}:${item.container}'] ?? guid(cosmos.id, principals[item.role], item.container, 'service-dev-v1')
  properties: {
    principalId: principals[item.role]
    roleDefinitionId: item.write ? '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002' : primaryReaderRoleId
    scope: '${cosmos.id}/dbs/teams/colls/${item.container}'
  }
}]
resource pulls 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in roles: if (provisionAccess) {
  name: existingRoleAssignmentIds[?'${role}:registry-pull'] ?? guid(registry.id, principals[role], 'service-dev-pull')
  scope: registry
  properties: {
    principalId: principals[role]
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}]
var notificationSenders = ['Receiver', 'Projector']
resource notificationSend 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in notificationSenders: if (provisionAccess) {
  name: existingRoleAssignmentIds[?'${role}:notification-send'] ?? guid(notifications.id, principals[role], 'service-dev-send')
  scope: notifications
  properties: {
    principalId: principals[role]
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  }
}]
resource notificationReceive 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionAccess) {
  name: existingRoleAssignmentIds[?'Projector:notification-receive'] ?? guid(notifications.id, principals.Projector, 'service-dev-receive')
  scope: notifications
  properties: {
    principalId: principals.Projector
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
  }
}
var deliveryPermissions = ['69a216fc-b8fb-44d8-bc22-1f3c2cd27a39', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0']
resource deliveryRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for permission in deliveryPermissions: if (provisionAccess) {
  name: existingRoleAssignmentIds[?'Delivery:${permission}'] ?? guid(delivery.id, principals.Delivery, permission, 'service-dev-v1')
  scope: delivery
  properties: {
    principalId: principals.Delivery
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', permission)
  }
}]
var redisRoles = ['Api', 'Control']
resource redisAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-04-01' = [for role in redisRoles: if (provisionAccess) {
  parent: redisDb
  name: existingRedisAssignmentIds[?role] ?? 'service-${toLower(role)}'
  properties: { accessPolicyName: 'default', user: { objectId: principals[role] } }
}]
var realtimeRoles = ['Api', 'Delivery', 'Control']
resource realtime 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for role in realtimeRoles: if (provisionAccess) {
  name: existingRoleAssignmentIds[?'${role}:realtime'] ?? guid(pubsub.id, principals[role], 'service-dev-realtime')
  scope: pubsub
  properties: {
    principalId: principals[role]
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12cf5a90-567b-43ae-8102-96cf46c7d9b4')
  }
}]
resource sessionAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionAccess) {
  name: existingRoleAssignmentIds[?'Api:session-protection'] ?? guid(sessionKey.id, principals.Api, 'service-dev-session')
  scope: sessionKey
  properties: {
    principalId: principals.Api
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'e147488a-f6f5-4113-8e2d-b22465e65bf6')
  }
}
resource notificationAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (name, index) in notificationKeyNames: if (provisionAccess) {
  name: existingRoleAssignmentIds[?'Projector:notification-key:${name}'] ?? guid(notificationKeys[index].id, principals.Projector, 'service-dev-decrypt')
  scope: notificationKeys[index]
  properties: {
    principalId: principals.Projector
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')
  }
}]
