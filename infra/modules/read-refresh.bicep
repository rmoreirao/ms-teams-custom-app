param serviceBusName string
param apiPrincipalId string
param controlPrincipalId string
param provisionAccess bool = false
param senderAssignmentName string = ''
param receiverAssignmentName string = ''

resource bus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = { name: serviceBusName }
resource queue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: bus
  name: 'graph-read-refresh'
  properties: {
    defaultMessageTimeToLive: 'PT5M'
    lockDuration: 'PT1M'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1M'
    maxSizeInMegabytes: 1024
  }
}
resource sender 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionAccess) {
  name: empty(senderAssignmentName) ? guid(queue.id, apiPrincipalId, 'read-refresh-send-v1') : senderAssignmentName
  scope: queue
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  }
}
resource receiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (provisionAccess) {
  name: empty(receiverAssignmentName) ? guid(queue.id, controlPrincipalId, 'read-refresh-receive-v1') : receiverAssignmentName
  scope: queue
  properties: {
    principalId: controlPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
  }
}
output queueId string = queue.id
