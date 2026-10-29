@description('Additive definitions only. Provisioning and expanded source processing require a separate reviewed activation.')
param cosmosAccountName string
param serviceBusName string
param principals object
param primaryReaderRoleId string
param provisionAccess bool = false
param existingDataAssignmentIds object = {}
param existingRoleAssignmentIds object = {}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = { name: cosmosAccountName }
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' existing = { parent: cosmos, name: 'teams' }
resource registry 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'live-resources'
  properties: { resource: {
    id: 'live-resources'
    partitionKey: { paths: ['/pk'], kind: 'Hash' }
    defaultTtl: -1
    indexingPolicy: loadJsonContent('../cosmos/live-indexing.json')
  } }
}
resource currentRead 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/storedProcedures@2024-05-15' = {
  parent: registry
  name: 'readCurrentV1'
  properties: { resource: { id: 'readCurrentV1', body: loadTextContent('../cosmos/read-current-v1.js') } }
}
resource access 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = [for role in ['Api', 'Receiver', 'Projector', 'Delivery', 'Control']: if (provisionAccess) {
  parent: cosmos
  name: existingDataAssignmentIds[?'${role}:live-resources'] ?? guid(cosmos.id, principals[role], 'live-resources', 'channel-v1')
  properties: {
    principalId: principals[role]
    roleDefinitionId: role == 'Control' ? '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002' : primaryReaderRoleId
    scope: '${cosmos.id}/dbs/teams/colls/live-resources'
  }
  dependsOn: [registry]
}]
resource bus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = { name: serviceBusName }
resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: bus
  name: 'channel-work'
  properties: {
    defaultMessageTimeToLive: 'PT1H'
    lockDuration: 'PT1M'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1M'
    maxSizeInMegabytes: 1024
  }
}
var permissions = [
  { role: 'Api', operation: 'send', permission: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39' }
  { role: 'Control', operation: 'send', permission: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39' }
  { role: 'Control', operation: 'receive', permission: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0' }
]
resource workAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for item in permissions: if (provisionAccess) {
  name: existingRoleAssignmentIds[?'${item.role}:channel-work-${item.operation}'] ?? guid(queue.id, principals[item.role], item.permission, 'channel-work-v1')
  scope: queue
  properties: {
    principalId: principals[item.role]
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', item.permission)
  }
}]
