targetScope = 'resourceGroup'

@description('Adds only the reviewed Graph callback route on the existing edge. Browser and PubSub routes are not replaced.')
param edgeProfileName string
param endpointName string
param environmentId string
param receiverFqdn string
param privateLinkLocation string
param configureCallbacks bool = false

resource edge 'Microsoft.Cdn/profiles@2024-02-01' existing = { name: edgeProfileName }
resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' existing = { parent: edge, name: endpointName }
resource group 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = if (configureCallbacks) {
  parent: edge
  name: 'service-receiver'
  properties: {
    loadBalancingSettings: { sampleSize: 4, successfulSamplesRequired: 3 }
    healthProbeSettings: { probePath: '/health/live', probeProtocol: 'Https', probeRequestType: 'GET', probeIntervalInSeconds: 60 }
  }
}
resource origin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = if (configureCallbacks) {
  parent: group
  name: 'receiver'
  properties: {
    hostName: receiverFqdn
    originHostHeader: receiverFqdn
    httpsPort: 443
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
    sharedPrivateLinkResource: {
      privateLink: { id: environmentId }
      groupId: 'managedEnvironments'
      privateLinkLocation: privateLinkLocation
      requestMessage: 'Reviewed service-identity Dev Graph callback receiver'
    }
  }
}
resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = if (configureCallbacks) {
  parent: endpoint
  name: 'graph-callbacks'
  properties: {
    originGroup: { id: group.id }
    supportedProtocols: ['Http', 'Https']
    patternsToMatch: ['/callbacks/graph/*']
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    enabledState: 'Enabled'
  }
  dependsOn: [origin]
}
