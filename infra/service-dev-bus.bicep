targetScope = 'resourceGroup'

@description('Development PoC only: public TLS/Entra Standard messaging. Does not resize or remove the existing Premium namespace.')
@minLength(6)
@maxLength(50)
param namespaceName string
param location string = 'swedencentral'
param principals object
param provisionAccess bool = false
param tags object = { environment: 'Development', application: 'teams-reference' }

resource bus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: namespaceName
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    minimumTlsVersion: '1.2'
  }
}
resource notifications 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: bus
  name: 'graph-notifications'
  properties: {
    defaultMessageTimeToLive: 'PT1H'
    lockDuration: 'PT1M'
    maxDeliveryCount: 5
    deadLetteringOnMessageExpiration: true
    enablePartitioning: false
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
    enablePartitioning: false
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
    maxSizeInMegabytes: 5120
  }
}
module readRefresh './modules/read-refresh.bicep' = {
  name: 'service-dev-standard-read-refresh'
  params: {
    serviceBusName: bus.name
    apiPrincipalId: principals.Api
    controlPrincipalId: principals.Control
    provisionAccess: provisionAccess
  }
}
var notificationAccess = [
  { role: 'Receiver', permission: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39' }
  { role: 'Projector', permission: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39' }
  { role: 'Projector', permission: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0' }
]
resource notificationRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for access in notificationAccess: if (provisionAccess) {
  name: guid(notifications.id, principals[access.role], access.permission, 'service-dev-standard-v1')
  scope: notifications
  properties: {
    principalId: principals[access.role]
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', access.permission)
  }
}]
var deliveryPermissions = ['69a216fc-b8fb-44d8-bc22-1f3c2cd27a39', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0']
resource deliveryRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for permission in deliveryPermissions: if (provisionAccess) {
  name: guid(delivery.id, principals.Delivery, permission, 'service-dev-standard-v1')
  scope: delivery
  properties: {
    principalId: principals.Delivery
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', permission)
  }
}]

output serviceBusName string = bus.name
output serviceBusEndpoint string = '${bus.name}.servicebus.windows.net'
