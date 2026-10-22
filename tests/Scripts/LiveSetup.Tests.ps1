$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("teams-live-setup-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$subscription = '11111111-1111-1111-1111-111111111111'
$tenant = '22222222-2222-2222-2222-222222222222'
$client = '33333333-3333-3333-3333-333333333333'
$control = '44444444-4444-4444-4444-444444444444'
$actors = @(
    '55555555-5555-5555-5555-555555555555',
    '66666666-6666-6666-6666-666666666666',
    '77777777-7777-7777-7777-777777777777'
)
$live = @{
    authorityMode = 'DelegatedSynthetic'; readConsistency = 'Session'; approvalReference = 'test-approved-scope'
    writerEpoch = 1; syntheticActorIds = $actors; syncClientId = $client; controlIdentityClientId = $control
    syntheticNotBefore = '2026-10-19T00:00:00Z'; notificationUrl = 'https://example.test/callbacks/graph/notifications'
    lifecycleUrl = 'https://example.test/callbacks/graph/lifecycle'; notificationCertificateId = 'test-key'
    notificationKeys = @{ 'test-key' = 'https://example.vault.azure.net/keys/approved/version' }
    resources = @(@{ id = 'graph-pilot-chat'; kind = 'chat'; title = 'Teams Reference Validation Chat' },
        @{ id = 'graph-pilot-channel'; kind = 'channel'; title = 'Teams Reference Validation Channel' })
    directChats = @()
}
$state = @{ mode = 'Live'; environment = 'approved-environment'; writes = 0; dotnet = 0 }
for ($first = 0; $first -lt 3; $first++) {
    for ($second = $first + 1; $second -lt 3; $second++) {
        $key = (@($actors[$first], $actors[$second]) | Sort-Object) -join ':'
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key))).ToLowerInvariant()
        $id = "graph-pilot-dm-$hash"
        $live.resources += @{ id = $id; kind = 'chat'; title = 'Approved pair' }
        $live.directChats += @{ resourceId = $id; firstActorId = $actors[$first]; secondActorId = $actors[$second] }
    }
}
function az {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'containerapp' -and $args[1] -eq 'show') {
        $name = $args[[array]::IndexOf($args, '--name') + 1]
        return @{
            id = "/subscriptions/$subscription/resourceGroups/rg/providers/Microsoft.App/containerApps/$name"
            identity = @{ userAssignedIdentities = @{ '/identities/approved' = @{ clientId = $control; principalId = 'principal' } } }
            properties = @{
                provisioningState = 'Succeeded'
                managedEnvironmentId = if ($name -eq 'api') { 'approved-environment' } else { $state.environment }
                template = @{ containers = @(@{
                    image = 'approved.azurecr.io/teams-reference@sha256:original'
                    env = @(
                        @{ name = 'App__GraphMode'; value = $state.mode }
                        @{ name = 'Live__Configuration'; value = ($live | ConvertTo-Json -Depth 15 -Compress) }
                        @{ name = 'App__TenantId'; value = $tenant }
                        @{ name = 'AzureAd__ClientId'; value = $client }
                        @{ name = 'App__PublicOrigin'; value = 'https://example.test' }
                        @{ name = 'Azure__ProtectionKey'; value = 'https://example.vault.azure.net/keys/session/version' }
                    )
                }) }
            }
        } | ConvertTo-Json -Depth 25
    }
    if ($args[0] -eq 'ad' -and $args[1] -eq 'user' -and $args[2] -eq 'show') {
        $id = $args[[array]::IndexOf($args, '--id') + 1]
        return @{ id = $id; userPrincipalName = "$id@example.test"; displayName = 'Approved test user' } | ConvertTo-Json
    }
    $state.writes++
    throw 'The Live settings generator attempted an unexpected cloud operation.'
}
function dotnet {
    $global:LASTEXITCODE = 0
    $state.dotnet++
    if ($env:TEAMS_CONFIGURATION_PATH -ne (Join-Path $temporary 'local.json') -or
        $env:ASPNETCORE_ENVIRONMENT -ne 'Development' -or $args -notcontains 'https://localhost:7266') {
        throw 'The real-service launcher changed the approved configuration/origin.'
    }
}
function Expect-Denial([scriptblock]$Action, [string]$Message) {
    $denied = $false
    try { & $Action } catch { if ($_.Exception.Message -notlike "*$Message*") { throw }; $denied = $true }
    if (-not $denied) { throw "Expected refusal: $Message" }
}
$previousConfiguration = $env:TEAMS_CONFIGURATION_PATH
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
try {
    $command = Join-Path $root 'scripts\New-SyntheticLiveSettings.ps1'
    $arguments = @{ PrivateDirectory = $temporary; SubscriptionId = $subscription; ResourceGroup = 'rg'
        ApiAppName = 'api'; WorkerAppName = 'worker'; OperationJobName = 'existing-historical-job' }
    & $command @arguments
    $settings = Get-Content -LiteralPath (Join-Path $temporary 'live-path.settings.json') -Raw | ConvertFrom-Json
    $metadata = Get-Content -LiteralPath (Join-Path $temporary 'authentication.metadata.json') -Raw | ConvertFrom-Json
    if ($settings.operationJobName -ne 'existing-historical-job' -or $metadata.schemaVersion -ne 2 -or
        $metadata.authProfile -ne 'live-delegated' -or $metadata.actors.Count -ne 3 -or
        $metadata.resources.Count -ne 5 -or $settings.liveConfig.directChats.Count -ne 3 -or $state.writes) {
        throw 'The Live generator changed its scope or emitted retired helper metadata.'
    }
    $state.mode = 'Simulator'
    Expect-Denial { & $command @arguments } 'explicit approved Live ScopePath'
    $state.mode = 'Live'; $state.environment = 'different-environment'
    Expect-Denial { & $command @arguments } 'distinct apps'
    $state.environment = 'approved-environment'
    $live.syntheticActorIds = @($actors[0], $actors[0], $actors[2])
    Expect-Denial { & $command @arguments } 'complete approved bounded'
    $live.syntheticActorIds = $actors
    $live.notificationUrl = 'https://wrong.example.test/callbacks/graph/notifications'
    Expect-Denial { & $command @arguments } 'callback origin'
    if ($state.writes) { throw 'Setup refusals mutated the cloud.' }
    $local = Join-Path $temporary 'local.json'
    $localSettings = @{ App = @{ GraphMode = 'Live'; Store = 'Cosmos'; Auth = 'Entra'; PublicOrigin = 'https://localhost:7266' } }
    $localSettings | ConvertTo-Json | Set-Content -LiteralPath $local
    $env:TEAMS_CONFIGURATION_PATH = 'previous-value'
    $env:ASPNETCORE_ENVIRONMENT = 'previous-environment'
    & (Join-Path $root 'scripts\Start-Local.ps1') -ConfigurationPath $local
    if ($state.dotnet -ne 1 -or $env:TEAMS_CONFIGURATION_PATH -ne 'previous-value' -or
        $env:ASPNETCORE_ENVIRONMENT -ne 'previous-environment') { throw 'The local launcher did not restore the calling environment.' }
    $localSettings.App.Auth = 'Local'
    $localSettings | ConvertTo-Json | Set-Content -LiteralPath $local
    Expect-Denial { & (Join-Path $root 'scripts\Start-Local.ps1') -ConfigurationPath $local } 'real-service'
    if ($state.dotnet -ne 1) { throw 'The local launcher accepted removed authentication.' }
    Write-Output 'Passed Live setup guards: direct Live metadata, explicit job, schema-v2 auth, no cloud writes, actor/origin/topology refusal and real-service-only local startup.'
} finally {
    $env:TEAMS_CONFIGURATION_PATH = $previousConfiguration
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    foreach ($name in @('live-path.settings.json', 'authentication.metadata.json', 'api.before.json', 'worker.before.json', 'local.json')) {
        $path = Join-Path $temporary $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
    }
    Remove-Item -LiteralPath $temporary
}
