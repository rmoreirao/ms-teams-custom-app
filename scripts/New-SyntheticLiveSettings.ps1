[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PrivateDirectory,
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{0,30}[a-z0-9]$')][string]$ApiAppName,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{0,30}[a-z0-9]$')][string]$WorkerAppName,
    [string]$ScopePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{0,30}[a-z0-9]$')][string]$OperationJobName
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)) + [IO.Path]::DirectorySeparatorChar
$directory = [IO.Path]::GetFullPath($PrivateDirectory)
if ($directory.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep resolved Live metadata outside the repository.' }
function Read-App([string]$Name) {
    $raw = az containerapp show --subscription $SubscriptionId --resource-group $ResourceGroup --name $Name --output json --only-show-errors
    if ($LASTEXITCODE) { throw "Unable to inspect the approved existing app $Name." }
    $app = ($raw -join "`n") | ConvertFrom-Json
    if ($app.properties.template.containers.Count -ne 1 -or $app.properties.provisioningState -ne 'Succeeded') {
        throw 'The selected existing app must be stable and have one container.'
    }
    return $app
}
function Read-Identity($App) {
    $entries = @($App.identity.userAssignedIdentities.PSObject.Properties)
    if ($entries.Count -ne 1) { throw 'Each selected app must have one explicit user-assigned identity.' }
    return @{ resourceId = $entries[0].Name; principalId = $entries[0].Value.principalId; clientId = $entries[0].Value.clientId }
}
$api = Read-App $ApiAppName
$worker = Read-App $WorkerAppName
if ($api.id -eq $worker.id -or $api.properties.managedEnvironmentId -ne $worker.properties.managedEnvironmentId) {
    throw 'Select two distinct apps in the same approved Development managed environment.'
}
$envs = @($api.properties.template.containers[0].env)
$source = if ($ScopePath) {
    $scope = [IO.Path]::GetFullPath($ScopePath)
    if ($scope.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Approved resolved resource inventory must remain private.' }
    Get-Content -LiteralPath $scope -Raw | ConvertFrom-Json
} else {
    if (($envs | Where-Object name -eq 'App__GraphMode').value -ne 'Live') {
        throw 'Supply an explicit approved Live ScopePath for initial setup. Retired application modes cannot be used as a source.'
    }
    ($envs | Where-Object name -eq 'Live__Configuration').value | ConvertFrom-Json
}
if ($source.authorityMode -ne 'DelegatedSynthetic' -or $source.readConsistency -cne 'Session' -or
    -not $source.approvalReference -or $source.writerEpoch -lt 1 -or
    @($source.syntheticActorIds).Count -ne 3 -or @($source.syntheticActorIds | Sort-Object -Unique).Count -ne 3 -or
    @($source.resources).Count -ne 5 -or @($source.directChats).Count -ne 3) {
    throw 'Supply the complete approved bounded three-user/five-resource Live validation profile with its three existing pair mappings.'
}
$apiIdentity = Read-Identity $api
$workerIdentity = Read-Identity $worker
$tenant = ($envs | Where-Object name -eq 'App__TenantId').value
$client = ($envs | Where-Object name -eq 'AzureAd__ClientId').value
$origin = ($envs | Where-Object name -eq 'App__PublicOrigin').value
if (-not $tenant -or $client -ne $source.syncClientId -or $apiIdentity.clientId -ne $source.controlIdentityClientId -or
    $source.notificationUrl -ne "$origin/callbacks/graph/notifications" -or $source.lifecycleUrl -ne "$origin/callbacks/graph/lifecycle") {
    throw 'The approved Live scope differs from the existing application, identity or callback origin.'
}
$settings = [ordered]@{
    schemaVersion = 1; subscriptionId = $SubscriptionId.ToString(); resourceGroup = $ResourceGroup
    environmentId = $api.properties.managedEnvironmentId; apiAppName = $ApiAppName; workerAppName = $WorkerAppName
    operationJobName = $OperationJobName; apiIdentity = $apiIdentity; workerIdentity = $workerIdentity
    vaultName = ([uri](($envs | Where-Object name -eq 'Azure__ProtectionKey').value)).Host.Split('.')[0]
    notificationKeyUri = $source.notificationKeys.($source.notificationCertificateId)
    registryServer = $api.properties.template.containers[0].image.Split('/')[0]
    probeActorId = $source.syntheticActorIds[0]; directChatsNotBefore = $source.syntheticNotBefore; liveConfig = $source
}
$people = @()
$personas = @('alex', 'sam', 'jordan')
for ($index = 0; $index -lt 3; $index++) {
    $raw = az ad user show --subscription $SubscriptionId --id $source.syntheticActorIds[$index] --output json --only-show-errors
    if ($LASTEXITCODE) { throw 'Unable to verify a selected approved identity.' }
    $person = ($raw -join "`n") | ConvertFrom-Json
    $people += @{ persona = $personas[$index]; id = $person.id; upn = $person.userPrincipalName; name = $person.displayName }
}
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$settings | ConvertTo-Json -Depth 35 | Set-Content -LiteralPath (Join-Path $directory 'live-path.settings.json') -Encoding utf8
foreach ($entry in @(@{ name = $ApiAppName; app = $api }, @{ name = $WorkerAppName; app = $worker })) {
    $entry.app | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath (Join-Path $directory "$($entry.name).before.json") -Encoding utf8
}
@{ schemaVersion = 2; origin = $origin; tenantId = $tenant; webClientId = $client; actors = $people
    resources = $source.resources; directChatsEnabled = @($source.directChats).Count -gt 0; authProfile = 'live-delegated' } |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $directory 'authentication.metadata.json') -Encoding utf8
Write-Output 'Private Live settings and current app snapshots prepared. No cloud resources, permissions or Teams content changed.'
