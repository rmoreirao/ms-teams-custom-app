targetScope = 'resourceGroup'

@description('Creates only the three reviewed identities missing from the existing two-workload Dev deployment.')
param base string
param location string
param tags object = { environment: 'Development', application: 'teams-reference' }

var roles = ['receiver', 'delivery', 'control']
resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for role in roles: {
  name: '${base}-${role}'
  location: location
  tags: tags
}]
output bindings array = [for (role, index) in roles: {
  role: role
  resourceId: identities[index].id
  clientId: identities[index].properties.clientId
  principalId: identities[index].properties.principalId
}]
