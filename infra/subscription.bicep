targetScope = 'subscription'

param resourceGroupName string = 'rg-teams-reference-dev'
param location string = 'westeurope'
param cosmosLocation string = location
@allowed(['Balanced_B0', 'Balanced_B1'])
param redisSku string = 'Balanced_B0'
param webClientId string
param eventsClientId string
param fixtureActorIds array

resource group 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: { application: 'teams-reference', environment: 'dev', data: 'synthetic-only' }
}
module platform './main.bicep' = {
  name: 'teams-reference-platform'
  scope: group
  params: {
    location: location
    cosmosLocation: cosmosLocation
    redisSku: redisSku
    webClientId: webClientId
    eventsClientId: eventsClientId
    fixtureActorIds: fixtureActorIds
  }
}
output registryLoginServer string = platform.outputs.registryLoginServer
