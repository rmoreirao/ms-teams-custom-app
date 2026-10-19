[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$InventoryPath,
    [Parameter(Mandatory)][string]$ScopePath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$ResourceGroup = 'rg-teams-reference-dev-swc'
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))+[IO.Path]::DirectorySeparatorChar
$directory=[IO.Path]::GetFullPath($OutputDirectory)
if ($directory.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { throw 'Private pilot configuration must be outside the repository.' }
$inventory=Get-Content -Raw -LiteralPath $InventoryPath | ConvertFrom-Json
$scope=Get-Content -Raw -LiteralPath $ScopePath | ConvertFrom-Json
if (-not $scope.complete -or $scope.tenantId -ne $inventory.tenantId -or $inventory.users.Count -ne 3 -or
    @($inventory.users.id | Where-Object {$_ -notin $scope.actorIds}).Count) { throw 'The approved three-user Teams scope is not complete.' }
$deployment=az deployment group show --subscription $SubscriptionId --resource-group $ResourceGroup `
    --name teams-reference-release --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Unable to read the current deployment.' }
$origin=$deployment.properties.outputs.publicOrigin.value
if (-not $origin -or $deployment.properties.parameters.webClientId.value -ne $inventory.applications.web.clientId) {
    throw 'Deployment origin/application does not match the identity inventory.'
}
$token=az account get-access-token --subscription $SubscriptionId --resource-type ms-graph --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Graph authentication failed.' }
$headers=@{Authorization="Bearer $($token.accessToken)"}
try {
    $fics=Invoke-RestMethod "https://graph.microsoft.com/v1.0/applications/$($inventory.applications.web.objectId)/federatedIdentityCredentials" -Headers $headers
    if ($fics.'@odata.nextLink') { throw 'Federation inventory requires explicit paging review.' }
    $scopes=@('openid','profile','offline_access','Chat.Read','ChatMessage.Send','Channel.ReadBasic.All','ChannelMessage.Read.All','ChannelMessage.Send')
    $profile=@{
        schemaVersion=1;tenantId=$inventory.tenantId;subscriptionId=$SubscriptionId.ToString();prefix=$inventory.prefix
        applications=@(@{
            purpose='web';assignmentRequired=$true;basicGraphValidation=$true
            redirectUris=@("$origin/auth/callback");logoutUrl="$origin/";assignedUsers=@($inventory.users.id)
            applicationPermissions=@();assignedGroups=@()
            delegatedGrants=@($inventory.users | ForEach-Object {@{consentType='Principal';principalId=$_.id;scopes=$scopes}})
            federatedCredentials=@($fics.value | Select-Object name,issuer,subject,audiences)
        })
    }
    $resources=@(
        @{id='graph-pilot-chat';kind='chat';title=$scope.chatTitle;team=$null;graphChatId=$scope.chatId;graphTeamId=$null;graphChannelId=$null}
        @{id='graph-pilot-channel';kind='channel';title=$scope.channelTitle;team=$scope.teamTitle;graphChatId=$null;graphTeamId=$scope.teamId;graphChannelId=$scope.channelId}
    )
    $parameters=@{
        webClientId=@{value=$inventory.applications.web.clientId};eventsClientId=@{value=$inventory.applications.events.clientId}
        fixtureActorIds=@{value=@($inventory.users | Sort-Object @{Expression={ [array]::IndexOf(@('alex','sam','jordan'),$_.persona) }} | ForEach-Object id)}
        location=@{value='swedencentral'};cosmosLocation=@{value='swedencentral'};redisSku=@{value='Balanced_B0'}
        retiredFixtureActorId=@{value=''}
        graphPilotConfig=@{value=@{notBefore=$scope.notBefore;resources=$resources}}
    }
    $metadata=@{
        schemaVersion=1;tenantId=$inventory.tenantId;subscriptionId=$SubscriptionId.ToString()
        webClientId=$inventory.applications.web.clientId;origin=$origin;resources=$resources
        actors=@($inventory.users | Select-Object persona,id,upn)
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $profile | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $directory 'identity.profile.json') -Encoding utf8
    @{
        '$schema'='https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion='1.0.0.0';parameters=$parameters
    } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $directory 'workloads.parameters.json') -Encoding utf8
    $metadata | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $directory 'validation.settings.json') -Encoding utf8
    Write-Output 'Private pilot identity profile, deployment parameters and validation metadata prepared. No permissions changed.'
} finally { $headers.Clear();$token=$null }
