targetScope = 'resourceGroup'

@description('Existing private services only. No account, environment, Front Door, registry or tier is created or resized.')
param cosmosAccountName string
param serviceBusName string
param vaultName string
param pubsubName string
param apiPrincipalId string
param workerPrincipalId string
param createNotificationKey bool = false
param existingNotificationKeyUri string = ''
param notificationKeyName string = 'graph-synthetic-notification'
param provisionPublishAccess bool = true
param notificationKeyExpiry string = dateTimeAdd(utcNow(), 'P30D')

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = { name: cosmosAccountName }
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' existing = { parent: cosmos, name: 'teams' }
var names = ['live-state', 'live-authority', 'live-control', 'session-primary-validation-azurepath']
resource containers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = [for name in names: {
  parent: database
  name: name
  properties: {
    resource: {
      id: name
      partitionKey: { paths: ['/pk'], kind: 'Hash' }
      defaultTtl: -1
      indexingPolicy: loadJsonContent('cosmos/live-indexing.json')
    }
  }
}]
resource procedures 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/storedProcedures@2024-05-15' = [for (name, index) in names: {
  parent: containers[index]
  name: 'readCurrentV1'
  properties: { resource: { id: 'readCurrentV1', body: loadTextContent('cosmos/read-current-v1.js') } }
}]
resource leases 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'live-leases'
  properties: { resource: { id: 'live-leases', partitionKey: { paths: ['/id'], kind: 'Hash' } } }
}
resource readerRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, 'synthetic-live-primary-reader-v1')
  properties: {
    roleName: 'synthetic-live-primary-reader-v1'
    type: 'CustomRole'
    assignableScopes: [cosmos.id]
    permissions: [{
      dataActions: [
        'Microsoft.DocumentDB/databaseAccounts/readMetadata'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/executeQuery'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/readChangeFeed'
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/executeStoredProcedure'
      ]
    }]
  }
}
var access = [
  { principal: apiPrincipalId, container: 'live-state', readOnly: false }
  { principal: apiPrincipalId, container: 'live-authority', readOnly: false }
  { principal: apiPrincipalId, container: 'live-control', readOnly: false }
  { principal: apiPrincipalId, container: 'session-primary-validation-azurepath', readOnly: false }
  { principal: workerPrincipalId, container: 'live-state', readOnly: false }
  { principal: workerPrincipalId, container: 'live-authority', readOnly: true }
  { principal: workerPrincipalId, container: 'live-control', readOnly: false }
  { principal: workerPrincipalId, container: 'live-leases', readOnly: false }
]
resource dataAccess 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = [for item in access: {
  parent: cosmos
  name: guid(cosmos.id, item.principal, item.container, 'synthetic-live-v1')
  properties: {
    principalId: item.principal
    roleDefinitionId: item.readOnly ? readerRole.id : '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: '${cosmos.id}/dbs/teams/colls/${item.container}'
  }
  dependsOn: [containers, leases]
}]
resource bus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = { name: serviceBusName }
resource notificationQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
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
resource deliveryQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
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
var notificationPermissions = [
  { principal: apiPrincipalId, permission: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39' }
  { principal: workerPrincipalId, permission: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39' }
  { principal: workerPrincipalId, permission: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0' }
]
resource notificationAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for item in notificationPermissions: {
  name: guid(notificationQueue.id, item.principal, item.permission)
  scope: notificationQueue
  properties: {
    principalId: item.principal
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', item.permission)
    description: 'Synthetic live notification/delivery worker on the existing namespace'
  }
}]
resource deliveryAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for permission in [
  '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
]: {
  name: guid(deliveryQueue.id, workerPrincipalId, permission)
  scope: deliveryQueue
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', permission)
    description: 'Synthetic live delivery on the existing queue'
  }
}]
resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: vaultName }
resource key 'Microsoft.KeyVault/vaults/keys@2023-07-01' = if (createNotificationKey) {
  parent: vault
  name: notificationKeyName
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: ['encrypt', 'decrypt', 'sign', 'verify']
    attributes: { enabled: true, exp: dateTimeToEpoch(notificationKeyExpiry) }
  }
}
resource existingKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = { parent: vault, name: notificationKeyName }
resource decryptAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(existingKey.id, workerPrincipalId, 'synthetic-notification-crypto')
  scope: existingKey
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')
  }
  dependsOn: [key]
}
resource pubsub 'Microsoft.SignalRService/webPubSub@2024-03-01' existing = { name: pubsubName }
resource publishAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionPublishAccess) {
  name: guid(pubsub.id, workerPrincipalId, 'synthetic-live-delivery')
  scope: pubsub
  properties: {
    principalId: workerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12cf5a90-567b-43ae-8102-96cf46c7d9b4')
  }
}
output notificationKeyUri string = createNotificationKey ? key.properties.keyUriWithVersion : existingNotificationKeyUri
output validationContainer string = 'session-primary-validation-azurepath'
