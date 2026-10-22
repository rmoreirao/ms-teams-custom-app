$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("teams-live-deploy-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$parameterPath = Join-Path $temporary 'parameters.json'
$outputPath = Join-Path $temporary 'outputs.json'
$subscription = '11111111-1111-1111-1111-111111111111'
$group = 'rg-teams-live-test'
$state = @{ calls = [Collections.Generic.List[object]]::new(); writes = 0; consistency = 'Strong'; public = 'Disabled'; regions = 1; multiWrite = $false }
$parameters = @{
    parameters = @{
        prefix = @{ value = 'teamslive' }; location = @{ value = 'swedencentral' }
        environmentId = @{ value = "/subscriptions/$subscription/resourceGroups/$group/providers/Microsoft.App/managedEnvironments/private" }
        registryName = @{ value = 'approvedregistry' }; cosmosAccountName = @{ value = 'approvedcosmos' }
        serviceBusName = @{ value = 'approvedbus' }; redisName = @{ value = 'approvedredis' }
        pubsubName = @{ value = 'approvedpubsub' }; vaultName = @{ value = 'approvedvault' }
        notificationKeyNames = @{ value = @('graph-notification') }
        webClientId = @{ value = '22222222-2222-2222-2222-222222222222' }
        syncClientId = @{ value = '33333333-3333-3333-3333-333333333333' }
        eventsClientId = @{ value = '44444444-4444-4444-4444-444444444444' }
        trustedProxyCidr = @{ value = '10.40.0.0/23' }
        liveConfig = @{ value = @{ approvalReference = 'synthetic-test-approval'; writerEpoch = 1; readConsistency = 'Session' } }
    }
}
function az {
    $state.calls.Add(@($args))
    $global:LASTEXITCODE = 0
    $index = [Array]::IndexOf($args, '--subscription')
    if ($index -lt 0 -or $args[$index + 1] -ne $subscription) { throw 'Missing selected subscription.' }
    if ($args[0] -eq 'account') { return @{ tenantId = 'tenant' } | ConvertTo-Json }
    if ($args[0] -eq 'cosmosdb') {
        return @{
            consistencyPolicy = @{ defaultConsistencyLevel = $state.consistency }
            publicNetworkAccess = $state.public; disableLocalAuth = $true
            locations = @(@{ locationName = 'swedencentral' }) * $state.regions
            enableMultipleWriteLocations = $state.multiWrite
        } | ConvertTo-Json -Depth 4
    }
    if ($args[0] -eq 'containerapp') { return @{ properties = @{ publicNetworkAccess = 'Disabled' } } | ConvertTo-Json }
    if ($args[0] -eq 'deployment' -and $args[2] -eq 'what-if') {
        foreach ($flag in @('deployWorkloads=false', 'provisionRbac=false', 'enableSynchronization=false', 'enableAdmission=false', 'enableMonitoring=false')) {
            if ($flag -notin $args) { throw "Default deployment did not close gate $flag" }
        }
        return 'Synthetic what-if'
    }
    if ($args[0] -eq 'deployment' -and $args[2] -eq 'create') {
        $state.writes++
        return @{ properties = @{ outputs = @{ identityBindings = @{ value = @(@{ role = 'Api'; principalId = 'synthetic' }) } } } } | ConvertTo-Json -Depth 10
    }
    throw 'Unexpected Azure operation in isolated deployment test.'
}
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action }
    catch { if ($_.Exception.Message -notlike "*$Message*") { throw }; $failed = $true }
    if (-not $failed) { throw "Missing expected failure: $Message" }
}
try {
    $parameters | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $parameterPath
    $command = Join-Path $root 'scripts\Deploy-Live.ps1'
    $arguments = @{ SubscriptionId = $subscription; ResourceGroup = $group; ParametersPath = $parameterPath; OutputsPath = $outputPath }
    & $command @arguments
    if ($state.writes -or (Test-Path -LiteralPath $outputPath)) { throw 'What-if mutated deployment or inventory.' }
    $before = $state.calls.Count
    Expect-Failure { & $command @arguments -EnableAdmission } 'Admission requires'
    Expect-Failure { & $command @arguments -EnableSynchronization } 'Synchronization requires'
    Expect-Failure { & $command @arguments -DeployWorkloads } 'immutable image'
    Expect-Failure { & $command @arguments -EnableMonitoring } 'Monitoring requires'
    if ($state.calls.Count -ne $before) { throw 'Invalid admission contacted Azure.' }
    $state.consistency = 'Session'
    & $command @arguments
    $state.consistency = 'Strong'; $state.public = 'Enabled'
    Expect-Failure { & $command @arguments } 'disabled public access'
    $state.public = 'Disabled'
    $state.consistency = 'Session'
    & $command @arguments
    if ($state.writes) { throw 'Session preview mutated the account or deployment.' }
    $state.regions = 2
    Expect-Failure { & $command @arguments } 'one Cosmos region'
    $state.regions = 1; $state.multiWrite = $true
    Expect-Failure { & $command @arguments } 'single-region writes'
    $state.multiWrite = $false; $state.consistency = 'Eventual'
    Expect-Failure { & $command @arguments } 'Session or Strong'
    $state.consistency = 'Session'
    foreach ($invalid in @('Unsupported', 'Strong', 'SessionPrimary', '')) {
        $parameters.parameters.liveConfig.value.readConsistency = $invalid
        $parameters | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $parameterPath
        $before = $state.calls.Count
        Expect-Failure { & $command @arguments } 'explicitly select Session'
        if ($state.calls.Count -ne $before) { throw 'Unsupported storage profile contacted Azure.' }
    }
    $parameters.parameters.liveConfig.value.readConsistency = 'Session'
    $parameters | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $parameterPath
    $state.consistency = 'Strong'
    & $command @arguments -Apply -WhatIf
    if ($state.writes) { throw 'PowerShell WhatIf deployed infrastructure.' }
    & $command @arguments -Apply -Confirm:$false
    if ($state.writes -ne 1 -or -not (Test-Path -LiteralPath $outputPath)) { throw 'Approved apply did not export outputs.' }
    if ((Get-Content -Raw -LiteralPath $outputPath | ConvertFrom-Json).identityBindings.value[0].role -ne 'Api') {
        throw 'Identity outputs were not preserved.'
    }
    Write-Output 'Passed live deployment checks: closed default gates, no-write preview, explicit Session and legacy-profile refusal, single-region/single-writer refusal, early input/network guards, confirmed apply and private output export; no fixture/Graph operations.'
} finally {
    foreach ($path in @($parameterPath, $outputPath)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    Remove-Item -LiteralPath $temporary
}
