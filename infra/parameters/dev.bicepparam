using '../main.bicep'

param prefix = 'teamsrefdev'
param location = 'swedencentral'
param cosmosLocation = readEnvironmentVariable('TEAMS_COSMOS_LOCATION', 'swedencentral')
param redisSku = readEnvironmentVariable('TEAMS_REDIS_SKU', 'Balanced_B0')
param existingServiceBusName = readEnvironmentVariable('TEAMS_SERVICE_BUS_NAME', '')
param webClientId = readEnvironmentVariable('TEAMS_WEB_CLIENT_ID')
param eventsClientId = readEnvironmentVariable('TEAMS_EVENTS_CLIENT_ID')
