$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$command = Join-Path $root 'scripts\Deploy-Dev.ps1'
$modulePath = Join-Path $root 'scripts\DeploymentContract.psm1'
Import-Module $modulePath -Force
$subscription = '11111111-1111-1111-1111-111111111111'
$tenant = '22222222-2222-2222-2222-222222222222'
$webClient = '33333333-3333-3333-3333-333333333333'
$apiClient = '44444444-4444-4444-4444-444444444444'
$workerClient = '55555555-5555-5555-5555-555555555555'
$scope = "/subscriptions/$subscription/resourceGroups/rg-live"
$environment = "$scope/providers/Microsoft.App/managedEnvironments/existing"
$registryServer = 'approvedregistry.azurecr.io'
$oldImage = "$registryServer/teams-reference@sha256:" + ('a' * 64)
$image = "$registryServer/teams-reference@sha256:" + ('b' * 64)
$origin = 'https://example.test'
$arguments = @{
    SubscriptionId = $subscription; TenantId = $tenant; ResourceGroup = 'rg-live'
    ApiAppName = 'existing-api'; WorkerAppName = 'existing-worker'; EnvironmentId = $environment
    RegistryName = 'approvedregistry'; Image = $image; RolloutAttempts = 2; RolloutDelaySeconds = 0
}
function Copy-Object($Value) { $Value | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100 }
function Sort-ObjectProperties($Value) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [array]) { return ,@($Value | ForEach-Object { Sort-ObjectProperties $_ }) }
    $sorted = [ordered]@{}
    if ($Value -is [Collections.IDictionary]) {
        foreach ($key in @($Value.Keys | Sort-Object)) { $sorted[$key] = Sort-ObjectProperties $Value[$key] }
    } else {
        foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) { $sorted[$property.Name] = Sort-ObjectProperties $property.Value }
    }
    return $sorted
}
function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ((Sort-ObjectProperties $Actual | ConvertTo-Json -Depth 100 -Compress) -cne
        (Sort-ObjectProperties $Expected | ConvertTo-Json -Depth 100 -Compress)) { throw $Message }
}
function New-App([string]$Name, [string]$Role, [string[]]$Roles, [string]$Client) {
    $identity = "$scope/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$Name"
    $actors = @(
        '66666666-6666-6666-6666-666666666666', '77777777-7777-7777-7777-777777777777', '88888888-8888-8888-8888-888888888888'
    )
    $live = @{
        authorityMode = 'DelegatedSynthetic'; readConsistency = 'SessionPrimary'; approvalReference = 'test-only-approval'
        writerEpoch = 7; hostedRoles = $Roles; syncClientId = $webClient; controlIdentityClientId = $apiClient
        synchronizationEnabled = $true; admissionEnabled = $true
        syntheticActorIds = $actors; syntheticNotBefore = '2026-01-01T00:00:00Z'
        syntheticResourceOwners = @{ 'graph-pilot-chat' = $actors[0]; 'graph-pilot-channel' = $actors[1] }
        syntheticHistoryFrom = @{ 'graph-pilot-chat' = '2026-01-02T00:00:00Z'; 'graph-pilot-channel' = '2026-01-03T00:00:00Z' }
        resources = @(
            @{ id = 'graph-pilot-chat'; path = 'chats/test/messages'; graphChatId = 'test'; kind = 'chat'; title = 'Existing chat' }
            @{ id = 'graph-pilot-channel'; path = 'teams/test/channels/test/messages'; graphTeamId = 'test'; graphChannelId = 'test'; kind = 'channel'; title = 'Existing channel' }
        )
        directChats = @(@{ resourceId = 'graph-pilot-chat'; firstActorId = $actors[0]; secondActorId = $actors[1] })
        subscriptionScopes = @(
            @{ id = 'chat-scope'; path = 'chats/test/messages' }
            @{ id = 'channel-scope'; path = 'teams/test/channels/test/messages' }
        )
        notificationUrl = "$origin/callbacks/graph/notifications"; lifecycleUrl = "$origin/callbacks/graph/lifecycle"
        notificationCertificateId = 'existing-certificate'; notificationCertificate = 'test-only-public-certificate'
        notificationKeys = @{ 'existing-certificate' = 'https://existing.vault.azure.net/keys/notification/version' }
    }
    $envs = @(
        @{ name = 'App__GraphMode'; value = 'Live' }; @{ name = 'App__Role'; value = $Role }
        @{ name = 'App__Auth'; value = 'Entra' }; @{ name = 'App__Store'; value = 'Cosmos' }
        @{ name = 'App__TenantId'; value = $tenant }; @{ name = 'AzureAd__TenantId'; value = $tenant }
        @{ name = 'App__PublicOrigin'; value = $origin }; @{ name = 'AzureAd__ClientId'; value = $webClient }
        @{ name = 'AzureClientId'; value = $Client }; @{ name = 'AZURE_CLIENT_ID'; value = $Client }
        @{ name = 'App__RealtimeAdmissionReady'; value = 'true' }
        @{ name = 'App__TrustedProxyNetworks__0'; value = '10.0.0.0/24' }
        @{ name = 'Callbacks__ClientId'; value = $tenant }
        @{ name = 'Callbacks__Audience'; value = "api://$tenant" }
        @{ name = 'Callbacks__PrincipalId'; value = $webClient }
        @{ name = 'Live__Configuration'; value = ($live | ConvertTo-Json -Depth 100 -Compress) }
        @{ name = 'Azure__CosmosEndpoint'; value = 'https://existing.documents.azure.com:443/' }
        @{ name = 'Azure__Redis'; value = 'existing.redis.azure.net:10000' }
        @{ name = 'Azure__ServiceBus'; value = 'existing.servicebus.windows.net' }
        @{ name = 'Azure__PubSub'; value = 'https://existing.webpubsub.azure.com' }
        @{ name = 'Azure__ProtectionKey'; value = 'https://existing.vault.azure.net/keys/session/version' }
        @{ name = 'ExistingSecret'; secretRef = 'existing-secret' }
        @{ name = 'UnrelatedSetting'; value = 'must remain exactly unchanged' }
    )
    if ($Role -eq 'Api') {
        $envs += @(
            @{ name = 'AzureAd__ClientCredentials__0__SourceType'; value = 'SignedAssertionFromManagedIdentity' }
            @{ name = 'AzureAd__ClientCredentials__0__ManagedIdentityClientId'; value = $Client }
        )
    }
    Copy-Object @{
        id = "$scope/providers/Microsoft.App/containerApps/$Name"; name = $Name; location = 'swedencentral'
        etag = 'test-etag'; tags = @{ purpose = 'existing-development' }
        identity = @{
            type = 'UserAssigned'
            userAssignedIdentities = @{ $identity = @{ clientId = $Client; principalId = $Client } }
        }
        properties = @{
            managedEnvironmentId = $environment; workloadProfileName = 'Consumption'; provisioningState = 'Succeeded'
            latestRevisionName = "$Name--previous"; latestReadyRevisionName = "$Name--previous"
            configuration = @{
                activeRevisionsMode = 'Single'; maxInactiveRevisions = 10
                registries = @(@{ server = $registryServer; identity = $identity })
                secrets = @(@{ name = 'existing-secret'; keyVaultUrl = 'https://existing.vault.azure.net/secrets/existing/version'; identity = $identity })
                ingress = if ($Role -eq 'Api') {
                    @{ external = $true; targetPort = 8080; allowInsecure = $false; transport = 'http'; traffic = @(@{ latestRevision = $true; weight = 100 }) }
                } else { $null }
                dapr = @{ enabled = $false }
            }
            template = @{
                revisionSuffix = 'previous'; terminationGracePeriodSeconds = 37
                volumes = @(@{ name = 'existing'; storageType = 'EmptyDir' })
                containers = @(@{
                    name = $Name; image = $oldImage; imageType = 'ContainerImage'
                    env = $envs; resources = @{ cpu = 0.25; memory = '0.5Gi'; ephemeralStorage = '1Gi' }
                    probes = @(@{ type = 'Liveness'; httpGet = @{ path = '/health/live'; port = 8080 }; initialDelaySeconds = 15 })
                    volumeMounts = @(@{ volumeName = 'existing'; mountPath = '/existing' })
                    command = @('dotnet'); args = @('Teams.Host.dll')
                })
                scale = @{ minReplicas = 1; maxReplicas = 2; cooldownPeriod = 300; pollingInterval = 30; rules = @() }
            }
        }
    }
}
function Reset-State {
    $script:state = @{
        apps = @{
            'existing-api' = New-App 'existing-api' 'Api' @('Api', 'Receiver', 'Control') $apiClient
            'existing-worker' = New-App 'existing-worker' 'Projector' @('Projector', 'Delivery') $workerClient
        }
        calls = [Collections.Generic.List[object]]::new(); writes = [Collections.Generic.List[string]]::new()
        bodies = @{}; readCounts = @{}; rollout = 'ready'; failName = ''; smoke = 0; drift = $false
        preflightRevision = $false; workerDrift = $false; finalDrift = $false
        tenant = $tenant; registry = $registryServer; httpFailure = $false; webClient = $webClient; origin = $origin
        manifestDigest = $image.Split('@')[1]
        expectedWrites = 2; admission = $true
        runningState = 'Running'
    }
    $global:DevReleaseHttpState = $script:state
}
function Set-Setting([string]$Name, [string]$Key, $Value) {
    ($state.apps[$Name].properties.template.containers[0].env | Where-Object name -eq $Key).value = $Value
}
function Change-Live([string]$Name, [scriptblock]$Change) {
    $entry = $state.apps[$Name].properties.template.containers[0].env | Where-Object name -eq 'Live__Configuration'
    $value = $entry.value | ConvertFrom-Json -Depth 100
    & $Change $value
    $entry.value = $value | ConvertTo-Json -Depth 100 -Compress
}
function az {
    $global:LASTEXITCODE = 0
    $state.calls.Add(@($args))
    $sub = [array]::IndexOf($args, '--subscription')
    if ($sub -lt 0 -or $args[$sub + 1] -ne $subscription) { throw 'Missing explicit selected subscription.' }
    if ($args[0] -eq 'account' -and $args[1] -eq 'show') {
        return @{ id = $subscription; tenantId = $state.tenant } | ConvertTo-Json
    }
    if ($args[0] -eq 'acr' -and $args[1] -eq 'show') {
        return @{ id = "$scope/providers/Microsoft.ContainerRegistry/registries/approvedregistry"; loginServer = $state.registry } | ConvertTo-Json
    }
    if ($args[0] -eq 'acr' -and $args[1] -eq 'repository' -and $args[2] -eq 'show') {
        return @{ digest = $state.manifestDigest } | ConvertTo-Json
    }
    if ($args[0] -eq 'containerapp' -and $args[1] -eq 'show') {
        $name = $args[[array]::IndexOf($args, '--name') + 1]
        $state.readCounts[$name]++
        $app = Copy-Object $state.apps[$name]
        if ($state.drift -and $name -eq 'existing-worker' -and $state.readCounts[$name] -gt 1) {
            $app.properties.template.scale.maxReplicas = 3
        }
        if (($state.workerDrift -and $name -eq 'existing-worker' -and $state.writes.Count -eq 1) -or
            ($state.finalDrift -and $name -eq 'existing-api' -and $state.writes.Count -eq 2)) {
            $app.properties.template.scale.maxReplicas = 3
        }
        if ($name -in $state.writes -and $name -eq $state.failName) {
            switch ($state.rollout) {
                'stale' { $app.properties.latestReadyRevisionName = "$name--previous" }
                'wrong-image' { $app.properties.template.containers[0].image = $oldImage }
                'failed' { $app.properties.provisioningState = 'Failed' }
                'changed-config' { $app.properties.configuration.maxInactiveRevisions = 99 }
                'read-error' { $global:LASTEXITCODE = 1; return '{}' }
            }
        }
        return $app | ConvertTo-Json -Depth 100
    }
    if ($args[0] -eq 'containerapp' -and $args[1] -eq 'revision' -and $args[2] -eq 'show') {
        $name = $args[[array]::IndexOf($args, '--name') + 1]
        $revision = $args[[array]::IndexOf($args, '--revision') + 1]
        $detail = @{
            name = $revision; properties = @{
                active = $true; provisioningState = 'Provisioned'; healthState = 'Healthy'; runningState = $state.runningState
                template = $state.apps[$name].properties.template
            }
        }
        if ($name -eq $state.failName -and ($name -in $state.writes -or $state.preflightRevision)) {
            switch ($state.rollout) {
                'unhealthy' { $detail.properties.healthState = 'Unhealthy' }
                'inactive' { $detail.properties.active = $false }
                'stopped' { $detail.properties.runningState = 'Stopped' }
                'wrong-revision' { $detail.name = "$name--previous" }
                'wrong-revision-image' { $detail.properties.template = Copy-Object $detail.properties.template; $detail.properties.template.containers[0].image = $oldImage }
            }
        }
        return $detail | ConvertTo-Json -Depth 100
    }
    if ($args[0] -eq 'rest' -and $args -contains 'patch') {
        if ($state.readCounts['existing-api'] -lt 2 -or $state.readCounts['existing-worker'] -lt 2) {
            throw 'A write happened before both targets completed preflight and drift checks.'
        }
        $url = $args[[array]::IndexOf($args, '--url') + 1]
        $name = if ($url -match '/containerApps/(existing-[a-z]+)\?api-version=2025-07-01$') { $Matches[1] } else { throw 'Wrong PATCH target/API.' }
        $path = $args[[array]::IndexOf($args, '--body') + 1].Substring(1)
        $body = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100
        if (@($body.PSObject.Properties.Name) -cne 'properties' -or @($body.properties.PSObject.Properties.Name) -cne 'template') {
            throw 'An image release attempted to patch outside the existing template.'
        }
        if ('If-Match=test-etag' -notin $args) { throw 'Available optimistic concurrency guard was not used.' }
        foreach ($container in @($body.properties.template.containers) + @($body.properties.template.initContainers)) {
            if ($null -eq $container) { continue }
            if ($container.PSObject.Properties['imageType'] -or $container.resources.PSObject.Properties['ephemeralStorage']) {
                throw 'Read-only container metadata cannot be submitted in the image update.'
            }
        }
        $state.writes.Add($name); $state.bodies[$name] = $body
        if ($state.rollout -eq 'patch-error' -and $name -eq $state.failName) { $global:LASTEXITCODE = 1; return }
        $previous = $state.apps[$name].properties.template
        $state.apps[$name].properties.template = Copy-Object $body.properties.template
        foreach ($container in $state.apps[$name].properties.template.containers) {
            $prior = $previous.containers | Where-Object name -eq $container.name
            $container | Add-Member -NotePropertyName imageType -NotePropertyValue $prior.imageType
            $container.resources | Add-Member -NotePropertyName ephemeralStorage -NotePropertyValue $prior.resources.ephemeralStorage
        }
        $revision = "$name--$($body.properties.template.revisionSuffix)"
        $state.apps[$name].properties.latestRevisionName = $revision
        $state.apps[$name].properties.latestReadyRevisionName = $revision
        return
    }
    throw "Unexpected Azure command (no infrastructure/Graph/permission writes are permitted): $($args[0..2] -join ' ')"
}
function Expect-Denial([scriptblock]$Action, [string]$Message, [int]$Writes = 0) {
    $failed = $false
    try { & $Action | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        $failed = $true
    }
    if (-not $failed -or $state.writes.Count -ne $Writes) { throw "Missing denial or unexpected write count: $Message" }
    if (Get-ChildItem -LiteralPath $root -Directory -Filter '.dev-release-*') { throw 'Release payload cleanup failed.' }
}
$global:DevReleaseHttpMock = { param($Path)
    $state = $global:DevReleaseHttpState
    if ($state.writes.Count -ne $state.expectedWrites) { throw 'Public smoke ran before both exact revisions were ready.' }
    $state.smoke++
    if ($state.httpFailure) { throw 'Mocked public readiness failed.' }
    switch ($Path) {
        '/' { return @{ status = 200; body = '<div id="root"></div><script src="/assets/index.js"></script>' } }
        '/assets/index.js' { return @{ status = 200 } }
        '/health/ready' { return @{ status = 200; body = (@{ status = 'ready'; mode = 'live'; role = 'Api'; admission = $state.admission } | ConvertTo-Json) } }
        '/api/session' { return @{ status = 401 } }
        '/auth/signin' { return @{ status = 302; location = [uri]"https://login.microsoftonline.com/$($state.tenant)/oauth2/v2.0/authorize?client_id=$($state.webClient)&redirect_uri=$([uri]::EscapeDataString("$($state.origin)/auth/callback"))" } }
        default { throw "Unexpected public write/capability request: $Path" }
    }
}
& (Get-Module DeploymentContract) {
    function script:Get-AppDeploymentResponse($Client, [string]$Path) { & $global:DevReleaseHttpMock $Path }
}
try {
    $workflow = Get-Content -LiteralPath (Join-Path $root '.github\workflows\deploy-dev.yml') -Raw
    foreach ($required in @('workflow_dispatch:', "github.ref == 'refs/heads/main'", 'environment: dev',
        'cancel-in-progress: false', 'id-token: write', 'DEPLOY-DEV', 'Verify release source',
        'AZURE_API_APP_NAME', 'AZURE_WORKER_APP_NAME', 'AZURE_CONTAINER_APP_ENVIRONMENT_ID', 'AZURE_DEPLOY_CLIENT_ID')) {
        if (-not $workflow.Contains($required)) { throw "Manual protected Live workflow contract is missing: $required" }
    }
    if ($workflow -match 'DEPLOY-SYNTHETIC|TEAMS_FIXTURE|Bicep|EnableRealtimeAdmission|ParametersPath|GraphMode|deployment group|--password') {
        throw 'The release workflow contains a retired runtime, configuration/bootstrap mutation or credential path.'
    }
    $actions = [regex]::Matches($workflow, '(?m)^\s*- uses: [^@\r\n]+@[a-f0-9]{40}(?:\s|$)')
    if ($actions.Count -ne 4 -or [regex]::Matches($workflow, '(?m)^\s*- uses:').Count -ne $actions.Count) {
        throw 'All release workflow actions must remain pinned to full commit SHAs.'
    }
    Reset-State
    $baseline = Copy-Object $state.apps
    & $command @arguments | Out-Null
    & $command @arguments -Apply -WhatIf | Out-Null
    if ($state.writes.Count -or $state.smoke -or (Get-ChildItem -LiteralPath $root -Directory -Filter '.dev-release-*')) {
        throw 'Plan/WhatIf wrote state, performed HTTP smoke or created private payloads.'
    }
    Assert-Equal $state.apps $baseline 'Planning changed the existing Live configuration.'
    Expect-Denial { & $command @arguments -Apply -Confirm:$false } 'DEPLOY-DEV'
    Expect-Denial { & $command @arguments -GraphMode Simulator } 'parameter'
    Expect-Denial { & $command @arguments -Image "$registryServer/teams-reference:latest" } 'does not match'
    Expect-Denial { & $command @arguments -WorkerAppName 'existing-api' } 'distinct'
    Expect-Denial { & $command @arguments -EnvironmentId '/subscriptions/wrong/resourceGroups/wrong/providers/Microsoft.App/managedEnvironments/wrong' } 'environment'
    Expect-Denial { & $command @arguments -Image ('wrongregistry.azurecr.io/teams-reference@sha256:' + ('b' * 64)) } 'registry'
    Expect-Denial { & $command @arguments -Image ("$registryServer/wrong-image@sha256:" + ('b' * 64)) } 'repository'
    $invalidCases = @(
        @{ message = 'subscription/tenant'; change = { $state.tenant = $subscription } }
        @{ message = 'registry'; change = { $state.registry = 'wrong.azurecr.io' } }
        @{ message = 'immutable image digest'; change = { $state.manifestDigest = 'sha256:' + ('c' * 64) } }
        @{ message = 'wrong existing target'; change = { $state.apps['existing-worker'].id = "$scope/providers/Microsoft.App/containerApps/not-the-worker" } }
        @{ message = 'wrong existing target'; change = { $state.apps['existing-worker'].properties.managedEnvironmentId = "$environment-wrong" } }
        @{ message = 'wrong existing target'; change = { $state.apps['existing-worker'].properties.configuration.activeRevisionsMode = 'Multiple' } }
        @{ message = 'wrong existing target'; change = { $state.apps['existing-worker'].properties.latestReadyRevisionName = 'stale' } }
        @{ message = 'wrong existing target'; change = { $state.apps['existing-worker'].properties.template.containers += Copy-Object $state.apps['existing-worker'].properties.template.containers[0] } }
        @{ message = 'mode/role'; change = { Set-Setting 'existing-worker' 'App__Role' 'Worker' } }
        @{ message = 'mode/role'; change = { Set-Setting 'existing-api' 'App__GraphMode' 'Simulator' } }
        @{ message = 'mode/role'; change = { Set-Setting 'existing-worker' 'App__GraphMode' 'GraphPilot' } }
        @{ message = 'mode/role'; change = { Set-Setting 'existing-worker' 'App__Auth' 'Local' } }
        @{ message = 'mode/role'; change = { Set-Setting 'existing-worker' 'App__Store' 'Memory' } }
        @{ message = 'required'; change = { Set-Setting 'existing-worker' 'Live__Configuration' '' } }
        @{ message = 'Ambiguous'; change = { $state.apps['existing-worker'].properties.template.containers[0].env += @{ name = 'App__Role'; value = 'Projector' } } }
        @{ message = 'Ambiguous'; change = { $state.apps['existing-worker'].properties.template.containers[0].env += @{ name = 'App:Role'; value = 'Simulator' } } }
        @{ message = 'secret reference'; change = { $state.apps['existing-worker'].properties.configuration.secrets = @() } }
        @{ message = 'approved-registry'; change = { $state.apps['existing-worker'].properties.template.containers[0].image = 'other.azurecr.io/image@sha256:' + ('a' * 64) } }
        @{ message = 'identity'; change = { $state.apps['existing-worker'].identity.userAssignedIdentities.PSObject.Properties.Value.clientId = $webClient } }
        @{ message = 'registry'; change = { $state.apps['existing-worker'].properties.configuration.registries[0].identity = 'unattached' } }
        @{ message = 'identity'; change = { Set-Setting 'existing-api' 'AzureAd__ClientCredentials__0__ManagedIdentityClientId' $workerClient } }
        @{ message = 'canonical'; change = { Set-Setting 'existing-worker' 'App__PublicOrigin' 'https://localhost' } }
        @{ message = 'publisher identity'; change = { Set-Setting 'existing-worker' 'Callbacks__Audience' 'api://wrong' } }
        @{ message = 'synchronization'; change = { Change-Live 'existing-worker' { param($v) $v.synchronizationEnabled = $false } } }
        @{ message = 'topology'; change = { Change-Live 'existing-worker' { param($v) $v.hostedRoles = @('Projector') } } }
        @{ message = 'topology'; change = { Change-Live 'existing-worker' { param($v) $v.readConsistency = 'Strong' } } }
        @{ message = 'scope'; change = { Change-Live 'existing-worker' { param($v) $v.syntheticActorIds = @($v.syntheticActorIds[0]) } } }
        @{ message = 'Graph mapping'; change = { Change-Live 'existing-worker' { param($v) $v.resources[0].graphChatId = '' } } }
        @{ message = 'declared Graph path'; change = { Change-Live 'existing-worker' { param($v) $v.resources[0].path = 'chats/not-approved/messages' } } }
        @{ message = 'ownership/history'; change = { Change-Live 'existing-worker' { param($v) $v.syntheticHistoryFrom.'graph-pilot-chat' = '2020-01-01T00:00:00Z' } } }
        @{ message = 'must agree'; change = { Change-Live 'existing-worker' { param($v) $v.writerEpoch = 8 } } }
        @{ message = 'services'; change = { Set-Setting 'existing-worker' 'Azure__ServiceBus' 'wrong.servicebus.windows.net' } }
        @{ message = 'existing ready revision'; change = { $state.preflightRevision = $true; $state.rollout = 'unhealthy'; $state.failName = 'existing-worker' } }
        @{ message = 'existing ready revision'; change = { $state.preflightRevision = $true; $state.rollout = 'inactive'; $state.failName = 'existing-api' } }
        @{ message = 'existing ready revision'; change = { $state.runningState = 'Stopped' } }
    )
    foreach ($case in $invalidCases) {
        Reset-State
        & $case.change
        Expect-Denial { & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false } $case.message
    }
    Reset-State
    $state.drift = $true
    Expect-Denial { & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false } 'changed after preflight'
    Reset-State
    $state.workerDrift = $true
    Expect-Denial { & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false } 'changed before its image update' 1
    Reset-State
    $state.finalDrift = $true
    Expect-Denial { & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false } 'before both-target acceptance' 2

    Reset-State
    $baseline = Copy-Object $state.apps
    & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false | Out-Null
    Assert-Equal @($state.writes) @('existing-api', 'existing-worker') 'Release did not update exactly both existing apps.'
    foreach ($name in @('existing-api', 'existing-worker')) {
        $expected = $baseline.$name
        $expected.properties.template.containers[0].image = $image
        $expected.properties.template.revisionSuffix = $state.apps[$name].properties.template.revisionSuffix
        $expected.properties.latestRevisionName = $state.apps[$name].properties.latestRevisionName
        $expected.properties.latestReadyRevisionName = $state.apps[$name].properties.latestReadyRevisionName
        Assert-Equal $state.apps[$name] $expected "Release changed env/secrets/identities/resources/scale/config on $name."
    }
    if ($state.smoke -ne 5) { throw 'Successful release did not run the read-only public smoke.' }

    Reset-State
    foreach ($name in @('existing-api', 'existing-worker')) {
        Change-Live $name {
            param($v)
            $v.syntheticNotBefore = '2026-01-19T00:00:00+00:00'
            $v.syntheticHistoryFrom.'graph-pilot-chat' = '2026-01-20T00:00:00+00:00'
            $v.syntheticHistoryFrom.'graph-pilot-channel' = '2026-01-21T00:00:00+00:00'
            foreach ($resource in $v.resources) { $resource.PSObject.Properties.Remove('path') }
        }
    }
    $baseline = Copy-Object $state.apps
    & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false | Out-Null
    foreach ($name in @('existing-api', 'existing-worker')) {
        Assert-Equal $state.apps[$name].properties.template.containers[0].env $baseline.$name.properties.template.containers[0].env `
            "Release changed existing date strings or omitted computed resource paths on $name."
    }
    if ($state.writes.Count -ne 2 -or $state.smoke -ne 5) { throw 'Existing settings without computed paths did not pass image-only release.' }

    Reset-State
    $state.runningState = 'RunningAtMaxScale'
    & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false | Out-Null
    if ($state.writes.Count -ne 2 -or $state.smoke -ne 5) { throw 'Healthy max-scale revisions did not pass exact release acceptance.' }

    foreach ($rollout in @('stale', 'wrong-image', 'failed', 'changed-config', 'read-error', 'unhealthy', 'inactive', 'stopped', 'wrong-revision', 'wrong-revision-image', 'patch-error')) {
        foreach ($name in @('existing-api', 'existing-worker')) {
            Reset-State
            $state.rollout = $rollout; $state.failName = $name
            $writes = if ($name -eq 'existing-api') { 1 } else { 2 }
            Expect-Denial { & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false } 'partial rollout is possible' $writes
            if ($state.smoke) { throw 'A stale/failed/partial rollout reached public smoke.' }
        }
    }
    Reset-State
    $state.httpFailure = $true
    Expect-Denial { & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false } 'Mocked public readiness failed' 2

    Reset-State
    foreach ($app in $state.apps.Values) { $app.properties.template.containers[0].image = $image }
    $state.rollout = 'noop'
    $state.expectedWrites = 0
    & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false | Out-Null
    if ($state.writes.Count -or $state.smoke -ne 5) { throw 'An already-ready exact image unnecessarily changed revisions or skipped smoke.' }
    foreach ($unchanged in @('existing-api', 'existing-worker')) {
        Reset-State
        $state.apps[$unchanged].properties.template.containers[0].image = $image
        $state.expectedWrites = 1
        & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false | Out-Null
        if ($state.writes.Count -ne 1 -or $unchanged -in $state.writes -or $state.smoke -ne 5) {
            throw 'A mixed already-ready/new-image release mutated the unchanged app or skipped exact acceptance.'
        }
    }
    Reset-State
    $state.admission = $false
    foreach ($name in @('existing-api', 'existing-worker')) {
        Change-Live $name { param($v) $v.synchronizationEnabled = $false; $v.admissionEnabled = $false }
        Set-Setting $name 'App__RealtimeAdmissionReady' 'false'
    }
    & $command @arguments -Apply -Confirmation DEPLOY-DEV -Confirm:$false | Out-Null
    foreach ($name in @('existing-api', 'existing-worker')) {
        $entries = $state.apps[$name].properties.template.containers[0].env
        $live = ($entries | Where-Object name -eq 'Live__Configuration').value | ConvertFrom-Json
        if ($live.synchronizationEnabled -ne $false -or $live.admissionEnabled -ne $false -or
            ($entries | Where-Object name -eq 'App__RealtimeAdmissionReady').value -cne 'false') {
            throw 'An image release changed the existing closed synchronization/admission gates.'
        }
    }
    if (Get-ChildItem -LiteralPath $root -Directory -Filter '.dev-release-*') { throw 'Release payloads were not removed.' }
    Write-Output "Passed mocked Live Development release: no-write plans, $($invalidCases.Count) preflight refusals, exact preservation, drift, idempotency, 22 failed/partial rollout cases and read-only smoke. No cloud calls."
} finally {
    Remove-Variable -Name DevReleaseHttpMock -Scope Global
    Remove-Variable -Name DevReleaseHttpState -Scope Global
    Import-Module $modulePath -Force
}
