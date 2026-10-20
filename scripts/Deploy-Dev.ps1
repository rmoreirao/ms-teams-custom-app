[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][guid]$TenantId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.()-]+$')][string]$ResourceGroup,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{0,30}[a-z0-9]$')][string]$ApiAppName,
    [Parameter(Mandatory)][ValidatePattern('^[a-z][a-z0-9-]{0,30}[a-z0-9]$')][string]$WorkerAppName,
    [Parameter(Mandatory)][string]$EnvironmentId,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]{5,50}$')][string]$RegistryName,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9.-]+\.azurecr\.io/[a-z0-9_./-]+@sha256:[a-f0-9]{64}$')][string]$Image,
    [string]$Confirmation,
    [switch]$Apply,
    [ValidateRange(1, 120)][int]$RolloutAttempts = 36,
    [ValidateRange(0, 60)][int]$RolloutDelaySeconds = 5
)
$ErrorActionPreference = 'Stop'
if ($SubscriptionId -eq [guid]::Empty -or $TenantId -eq [guid]::Empty -or $ApiAppName -eq $WorkerAppName) {
    throw 'Explicit nonempty subscription/tenant and two distinct existing targets are required.'
}
$scope = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup"
if ($EnvironmentId -notmatch "^/subscriptions/$SubscriptionId/resourceGroups/[^/]+/providers/Microsoft\.App/managedEnvironments/[^/]+$") {
    throw 'The expected Container Apps environment must belong to the selected subscription.'
}
if ($Apply -and -not $WhatIfPreference -and $Confirmation -cne 'DEPLOY-DEV') {
    throw 'An applied Development release requires the explicit DEPLOY-DEV acknowledgement.'
}

function Invoke-AzureJson([string[]]$Arguments) {
    $raw = az @Arguments --subscription $SubscriptionId --output json --only-show-errors
    if ($LASTEXITCODE) { throw "Azure read failed ($($Arguments[0..1] -join ' ')); no successful release is claimed." }
    if (-not $raw) { throw 'Azure returned an empty response.' }
    return ($raw -join "`n") | ConvertFrom-Json -Depth 100
}
function Get-CanonicalValue($Value) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object)) { $result[$key] = Get-CanonicalValue $Value[$key] }
        return $result
    }
    if ($Value -is [array]) { return ,@($Value | ForEach-Object { Get-CanonicalValue $_ }) }
    $result = [ordered]@{}
    foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) {
        $result[$property.Name] = Get-CanonicalValue $property.Value
    }
    return $result
}
function Get-Fingerprint($Value) { Get-CanonicalValue $Value | ConvertTo-Json -Depth 100 -Compress }
function Get-PreservedState($App) {
    @{
        id = $App.id; location = $App.location; tags = $App.tags; identity = $App.identity
        configuration = $App.properties.configuration; template = $App.properties.template
        managedEnvironmentId = $App.properties.managedEnvironmentId
        environmentId = $App.properties.environmentId; workloadProfileName = $App.properties.workloadProfileName
    }
}
function Get-RequiredSetting($Settings, [string]$Name) {
    if (-not $Settings.ContainsKey($Name) -or [string]::IsNullOrWhiteSpace($Settings[$Name].value) -or $Settings[$Name].secretRef) {
        throw "Required non-secret target configuration is missing: $Name."
    }
    return [string]$Settings[$Name].value
}
function Test-NonemptyGuid([string]$Value) {
    $parsed = [guid]::Empty
    return [guid]::TryParseExact($Value, 'D', [ref]$parsed) -and $parsed -ne [guid]::Empty
}
function Test-PublicOrigin([string]$Value) {
    $uri = $null
    return [uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -and $uri.Scheme -eq 'https' -and
        $uri.Port -eq 443 -and -not $uri.IsLoopback -and $uri.AbsolutePath -eq '/' -and
        -not $uri.Query -and -not $uri.Fragment -and -not $uri.UserInfo
}
function Get-InvariantTimestamp($Value) {
    if ($Value -is [DateTime] -or $Value -is [DateTimeOffset]) {
        return $Value.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    }
    return [string]$Value
}
function Get-ApprovedResourcePath($Resource) {
    $path = if ($Resource.kind -eq 'chat' -and -not [string]::IsNullOrWhiteSpace($Resource.graphChatId)) {
        "chats/$([uri]::EscapeDataString($Resource.graphChatId))/messages"
    } elseif ($Resource.kind -eq 'channel' -and -not [string]::IsNullOrWhiteSpace($Resource.graphTeamId) -and
        -not [string]::IsNullOrWhiteSpace($Resource.graphChannelId)) {
        "teams/$([uri]::EscapeDataString($Resource.graphTeamId))/channels/$([uri]::EscapeDataString($Resource.graphChannelId))/messages"
    } else {
        throw 'Approved resource Graph mapping is incomplete.'
    }
    if ($Resource.PSObject.Properties['path'] -and $Resource.path -cne $path) {
        throw 'The declared Graph path differs from its approved resource mapping.'
    }
    return $path
}
function Get-Target([string]$Name, [string]$Role, [string[]]$HostedRoles) {
    $app = Invoke-AzureJson @('containerapp', 'show', '--resource-group', $ResourceGroup, '--name', $Name)
    if ($app.id -ne "$scope/providers/Microsoft.App/containerApps/$Name" -or
        $app.properties.managedEnvironmentId -ne $EnvironmentId -or
        $app.properties.provisioningState -ne 'Succeeded' -or
        $app.properties.configuration.activeRevisionsMode -ne 'Single' -or
        @($app.properties.template.containers).Count -ne 1 -or
        -not $app.properties.latestRevisionName -or
        $app.properties.latestRevisionName -ne $app.properties.latestReadyRevisionName) {
        throw "Refusing unstable or wrong existing target: $Name (identity, environment, topology or ready revision)."
    }
    $container = $app.properties.template.containers[0]
    if (-not $container.name -or $container.image -notmatch "^$([regex]::Escape($registry.loginServer))/[^@]+@sha256:[a-f0-9]{64}$" -or
        -not $container.resources.cpu -or -not $container.resources.memory -or
        $null -eq $app.properties.template.scale.minReplicas -or $app.properties.template.scale.minReplicas -lt 1 -or
        $null -eq $app.properties.template.scale.maxReplicas -or
        $app.properties.template.scale.maxReplicas -lt $app.properties.template.scale.minReplicas) {
        throw "Target $Name requires an immutable approved-registry image and explicit existing resources/scale."
    }
    if ($container.image.Split('@')[0] -cne $Image.Split('@')[0]) {
        throw "The release repository must match the existing approved image repository on $Name."
    }
    $settings = @{}
    foreach ($entry in @($container.env)) {
        if (-not $entry.name -or $entry.name.Contains(':') -or $settings.ContainsKey($entry.name)) {
            throw "Ambiguous environment configuration on $Name."
        }
        $settings[$entry.name] = $entry
        if ($entry.secretRef -and $entry.secretRef -notin @($app.properties.configuration.secrets.name)) {
            throw "An existing environment secret reference is unresolved on $Name."
        }
    }
    foreach ($pair in @(
        @('App__GraphMode', 'Live'), @('App__Role', $Role), @('App__Auth', 'Entra'),
        @('App__Store', 'Cosmos'), @('App__TenantId', $TenantId.ToString()), @('AzureAd__TenantId', $TenantId.ToString())
    )) {
        if ((Get-RequiredSetting $settings $pair[0]) -cne $pair[1]) { throw "Wrong Live mode/role/auth/store/tenant on $Name ($($pair[0]))."}
    }
    if ($settings['App__Operation'].value) { throw "A diagnostic operation is configured on release target $Name." }
    $origin = Get-RequiredSetting $settings 'App__PublicOrigin'
    if (-not (Test-PublicOrigin $origin)) { throw "Invalid canonical public origin on $Name." }
    $webClient = Get-RequiredSetting $settings 'AzureAd__ClientId'
    $runtimeClient = Get-RequiredSetting $settings 'AzureClientId'
    if (-not (Test-NonemptyGuid $webClient) -or -not (Test-NonemptyGuid $runtimeClient) -or
        (Get-RequiredSetting $settings 'AZURE_CLIENT_ID') -ne $runtimeClient) {
        throw "Explicit coherent Entra/managed identity client bindings are required on $Name."
    }
    $identities = @($app.identity.userAssignedIdentities.PSObject.Properties)
    $runtimeIdentity = @($identities | Where-Object { $_.Value.clientId -eq $runtimeClient -and (Test-NonemptyGuid $_.Value.principalId) })
    if ($app.identity.type -notin @('UserAssigned', 'SystemAssigned, UserAssigned') -or $runtimeIdentity.Count -ne 1 -or
        $runtimeIdentity[0].Name -notmatch "^/subscriptions/$SubscriptionId/resourceGroups/[^/]+/providers/Microsoft\.ManagedIdentity/userAssignedIdentities/[^/]+$") {
        throw "The configured runtime identity is not attached to $Name."
    }
    $bindings = @($app.properties.configuration.registries | Where-Object server -eq $registry.loginServer)
    if ($bindings.Count -ne 1 -or $bindings[0].identity -notin $identities.Name -or
        $bindings[0].username -or $bindings[0].passwordSecretRef) {
        throw "The approved registry must use an attached managed identity on $Name."
    }
    if ($Role -eq 'Api' -and (
        (Get-RequiredSetting $settings 'AzureAd__ClientCredentials__0__SourceType') -ne 'SignedAssertionFromManagedIdentity' -or
        (Get-RequiredSetting $settings 'AzureAd__ClientCredentials__0__ManagedIdentityClientId') -ne $runtimeClient -or
        $app.properties.configuration.ingress.external -ne $true -or
        $app.properties.configuration.ingress.allowInsecure -ne $false -or
        $app.properties.configuration.ingress.targetPort -ne 8080)) {
        throw 'The API Entra assertion identity or protected ingress is inconsistent.'
    }
    if ($Role -ne 'Api' -and $app.properties.configuration.ingress.external -eq $true) {
        throw 'The worker must not expose public ingress.'
    }
    foreach ($required in @('Azure__CosmosEndpoint', 'Azure__Redis', 'Azure__ServiceBus', 'Azure__PubSub', 'Azure__ProtectionKey')) {
        $null = Get-RequiredSetting $settings $required
    }
    $eventsClient = Get-RequiredSetting $settings 'Callbacks__ClientId'
    if (-not (Test-NonemptyGuid $eventsClient) -or
        (Get-RequiredSetting $settings 'Callbacks__Audience') -cne "api://$eventsClient" -or
        -not (Test-NonemptyGuid (Get-RequiredSetting $settings 'Callbacks__PrincipalId'))) {
        throw "The existing notification publisher identity/audience is inconsistent on $Name."
    }
    $null = Get-RequiredSetting $settings 'App__TrustedProxyNetworks__0'
    $live = (Get-RequiredSetting $settings 'Live__Configuration') | ConvertFrom-Json -Depth 100
    if ($live.authorityMode -cne 'DelegatedSynthetic' -or $live.readConsistency -cne 'SessionPrimary' -or
        -not $live.approvalReference -or $live.writerEpoch -lt 1 -or
        (@($live.hostedRoles | Sort-Object) -join ',') -cne (@($HostedRoles | Sort-Object) -join ',') -or
        -not (Test-NonemptyGuid $live.syncClientId) -or -not (Test-NonemptyGuid $live.controlIdentityClientId) -or
        $webClient -ne $live.syncClientId -or
        $live.synchronizationEnabled -isnot [bool] -or $live.admissionEnabled -isnot [bool] -or
        ($live.admissionEnabled -and -not $live.synchronizationEnabled) -or
        (Get-RequiredSetting $settings 'App__RealtimeAdmissionReady') -cne $live.admissionEnabled.ToString().ToLowerInvariant()) {
        throw "Missing or inconsistent approved Live topology/synchronization/admission on $Name."
    }
    if ($Role -eq 'Api' -and $live.controlIdentityClientId -ne $runtimeClient) {
        throw 'The compact API/Receiver/Control identity does not match Live configuration.'
    }
    $actors = @($live.syntheticActorIds)
    $resources = @($live.resources)
    $notBefore = [DateTimeOffset]::MinValue
    if ($actors.Count -ne 3 -or @($actors | Sort-Object -Unique).Count -ne 3 -or
        @($actors | Where-Object { -not (Test-NonemptyGuid $_) }).Count -or
        -not [DateTimeOffset]::TryParse((Get-InvariantTimestamp $live.syntheticNotBefore),
            [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$notBefore) -or
        $notBefore -gt [DateTimeOffset]::UtcNow -or
        $resources.Count -lt 2 -or $resources.Count -gt 5 -or
        @($resources.id | Sort-Object -Unique).Count -ne $resources.Count -or
        @($resources | Where-Object kind -eq 'channel').Count -ne 1) {
        throw "The bounded three-user/resource scope is missing or invalid on $Name."
    }
    $paths = @($resources | ForEach-Object { Get-ApprovedResourcePath $_ })
    if (@($paths | Sort-Object -Unique).Count -ne $resources.Count) {
        throw "Approved resource paths must be unique on $Name."
    }
    foreach ($resource in $resources) {
        $from = [DateTimeOffset]::MinValue
        $path = Get-ApprovedResourcePath $resource
        if (-not $resource.id -or $resource.kind -notin @('chat', 'channel') -or
            $live.syntheticResourceOwners.($resource.id) -notin $actors -or
            -not [DateTimeOffset]::TryParse((Get-InvariantTimestamp $live.syntheticHistoryFrom.($resource.id)),
                [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$from) -or
            $from -lt $notBefore -or $from -gt [DateTimeOffset]::UtcNow -or
            @($live.subscriptionScopes | Where-Object path -eq $path).Count -ne 1) {
            throw "Approved resource ownership/history/subscription scope is incoherent on $Name."
        }
    }
    if (@($live.subscriptionScopes).Count -ne $resources.Count -or
        $live.notificationUrl -cne "$($origin.TrimEnd('/'))/callbacks/graph/notifications" -or
        $live.lifecycleUrl -cne "$($origin.TrimEnd('/'))/callbacks/graph/lifecycle" -or
        -not $live.notificationCertificateId -or -not $live.notificationCertificate -or
        -not $live.notificationKeys.($live.notificationCertificateId)) {
        throw "Required encrypted notification configuration is missing or inconsistent on $Name."
    }
    foreach ($key in @($live.notificationKeys.PSObject.Properties.Value)) {
        if ($key -notmatch '^https://[a-zA-Z0-9-]+\.vault\.azure\.net/keys/[^/?#]+/[^/?#]+$') {
            throw "Versioned notification key references are required on $Name."
        }
    }
    $currentRevision = Invoke-AzureJson @('containerapp', 'revision', 'show', '--resource-group', $ResourceGroup,
        '--name', $Name, '--revision', $app.properties.latestReadyRevisionName)
    if ($currentRevision.name -cne $app.properties.latestReadyRevisionName -or $currentRevision.properties.active -ne $true -or
        $currentRevision.properties.provisioningState -notin @('Provisioned', 'Succeeded') -or $currentRevision.properties.healthState -ne 'Healthy' -or
        $currentRevision.properties.runningState -notin @('Running', 'RunningAtMaxScale') -or @($currentRevision.properties.template.containers).Count -ne 1 -or
        $currentRevision.properties.template.containers[0].image -cne $container.image) {
        throw "The existing ready revision is not healthy or does not match the exact current image on $Name."
    }
    [pscustomobject]@{
        name = $Name; app = $app; live = $live; settings = $settings; origin = $origin
        webClient = $webClient; fingerprint = Get-Fingerprint (Get-PreservedState $app)
    }
}

$account = Invoke-AzureJson @('account', 'show')
if ($account.id -ne $SubscriptionId.ToString() -or $account.tenantId -ne $TenantId.ToString()) {
    throw 'The selected Azure subscription/tenant does not match this Development release.'
}
$registry = Invoke-AzureJson @('acr', 'show', '--name', $RegistryName)
if ($registry.id -notmatch "^/subscriptions/$SubscriptionId/resourceGroups/[^/]+/providers/Microsoft\.ContainerRegistry/registries/$RegistryName$" -or
    $registry.loginServer -cne "$RegistryName.azurecr.io" -or
    $Image.Split('/')[0] -cne $registry.loginServer) {
    throw 'The immutable image must belong to the explicitly selected existing registry/subscription.'
}
$manifest = Invoke-AzureJson @('acr', 'repository', 'show', '--name', $RegistryName,
    '--image', $Image.Substring($registry.loginServer.Length + 1))
if ($manifest.digest -cne $Image.Split('@')[1]) {
    throw 'The requested immutable image digest could not be verified in the approved registry.'
}
# Complete both target preflights before considering any write.
$targets = @(
    Get-Target $ApiAppName 'Api' @('Api', 'Receiver', 'Control')
    Get-Target $WorkerAppName 'Projector' @('Projector', 'Delivery')
)
$apiLive = $targets[0].live | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100
$workerLive = $targets[1].live | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100
$apiLive.PSObject.Properties.Remove('hostedRoles')
$workerLive.PSObject.Properties.Remove('hostedRoles')
if ((Get-Fingerprint $apiLive) -cne (Get-Fingerprint $workerLive) -or
    $targets[0].origin -cne $targets[1].origin -or
    $targets[0].webClient -ne $targets[1].webClient) {
    throw 'Both Live targets must agree on the existing authority, resources, synchronization, admission and public origin.'
}
if ($targets[0].settings['AzureClientId'].value -eq $targets[1].settings['AzureClientId'].value) {
    throw 'The compact API and worker must retain distinct role-isolated managed identities.'
}
foreach ($setting in @('Azure__CosmosEndpoint', 'Azure__Redis', 'Azure__ServiceBus', 'Azure__PubSub', 'Azure__ProtectionKey',
    'Callbacks__ClientId', 'Callbacks__Audience', 'Callbacks__PrincipalId')) {
    if ($targets[0].settings[$setting].value -cne $targets[1].settings[$setting].value) {
        throw 'Both Live targets must use the same existing data/notification services.'
    }
}
foreach ($target in $targets) {
    Write-Output "Recovery: app=$($target.name); priorImage=$($target.app.properties.template.containers[0].image); priorReadyRevision=$($target.app.properties.latestReadyRevisionName)"
}
Write-Output "Plan: image-only Development release to $ApiAppName and $WorkerAppName. Existing Live configuration, admission, identities, secrets, ingress, services, resources and scale remain unchanged."
Write-Output 'Recovery requires a separately approved release of a known-good compatible Live digest. No automatic rollback, bootstrap, data reset or Teams resend is performed.'
if (-not $Apply -or -not $PSCmdlet.ShouldProcess("$ApiAppName, $WorkerAppName", "Release immutable Live image $Image in Development")) { return }

$touched = [Collections.Generic.List[string]]::new()
$completed = [Collections.Generic.List[string]]::new()
$directory = $null
try {
    # Detect drift of either target since the all-target preflight before the first write.
    foreach ($target in $targets) {
        $current = Invoke-AzureJson @('containerapp', 'show', '--resource-group', $ResourceGroup, '--name', $target.name)
        if ((Get-Fingerprint (Get-PreservedState $current)) -cne $target.fingerprint -or
            $current.properties.latestRevisionName -ne $target.app.properties.latestRevisionName -or
            $current.properties.latestReadyRevisionName -ne $target.app.properties.latestReadyRevisionName) {
            throw "Target $($target.name) changed after preflight; repeat the release plan."
        }
    }
    foreach ($target in $targets) {
        $current = Invoke-AzureJson @('containerapp', 'show', '--resource-group', $ResourceGroup, '--name', $target.name)
        if ((Get-Fingerprint (Get-PreservedState $current)) -cne $target.fingerprint -or
            $current.properties.latestRevisionName -ne $target.app.properties.latestRevisionName -or
            $current.properties.latestReadyRevisionName -ne $target.app.properties.latestReadyRevisionName) {
            throw "Target $($target.name) changed before its image update; repeat preflight."
        }
        $expected = $target.app | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100
        $revision = $expected.properties.latestReadyRevisionName
        if ($expected.properties.template.containers[0].image -cne $Image) {
            $suffix = 'dev-' + [guid]::NewGuid().ToString('N').Substring(0, 16)
            $revision = "$($target.name)--$suffix"
            $expected.properties.template.containers[0].image = $Image
            $expected.properties.template | Add-Member -NotePropertyName revisionSuffix -NotePropertyValue $suffix -Force
            if (-not $directory) {
                $directory = Join-Path (Get-Location).Path ('.dev-release-' + [guid]::NewGuid().ToString('N'))
                $null = New-Item -ItemType Directory -Path $directory
            }
            $patch = Join-Path $directory "$($target.name).json"
            $writable = $expected.properties.template | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100
            foreach ($container in @($writable.containers) + @($writable.initContainers)) {
                if ($null -eq $container) { continue }
                $container.PSObject.Properties.Remove('imageType')
                if ($null -ne $container.resources) { $container.resources.PSObject.Properties.Remove('ephemeralStorage') }
            }
            @{ properties = @{ template = $writable } } | ConvertTo-Json -Depth 100 |
                Set-Content -LiteralPath $patch -Encoding utf8
            $arguments = @('rest', '--method', 'patch', '--url',
                "https://management.azure.com$($target.app.id)?api-version=2025-07-01", '--body', "@$patch",
                '--subscription', $SubscriptionId.ToString(), '--output', 'none', '--only-show-errors')
            if ($target.app.etag) { $arguments += @('--headers', "If-Match=$($target.app.etag)") }
            $touched.Add($target.name)
            az @arguments
            if ($LASTEXITCODE) { throw "Image update failed for $($target.name)." }
            Remove-Item -LiteralPath $patch
        }
        $ready = $false
        for ($attempt = 0; $attempt -lt $RolloutAttempts; $attempt++) {
            $observed = Invoke-AzureJson @('containerapp', 'show', '--resource-group', $ResourceGroup, '--name', $target.name)
            if ($observed.properties.provisioningState -in @('Failed', 'Canceled', 'Cancelled')) {
                throw "The $($target.name) rollout failed provisioning."
            }
            if ($observed.properties.provisioningState -eq 'Succeeded' -and
                $observed.properties.latestRevisionName -ceq $revision -and
                $observed.properties.latestReadyRevisionName -ceq $revision -and
                $observed.properties.template.containers[0].image -ceq $Image) {
                if ((Get-Fingerprint (Get-PreservedState $observed)) -cne (Get-Fingerprint (Get-PreservedState $expected))) {
                    throw "Preserved configuration changed during the $($target.name) rollout; inspect before retrying."
                }
                $detail = Invoke-AzureJson @('containerapp', 'revision', 'show', '--resource-group', $ResourceGroup,
                    '--name', $target.name, '--revision', $revision)
                if ($detail.properties.provisioningState -in @('Failed', 'Canceled', 'Cancelled') -or
                    $detail.properties.healthState -eq 'Unhealthy' -or $detail.properties.runningState -in @('Failed', 'Degraded')) {
                    throw "The requested $($target.name) revision failed health/provisioning."
                }
                if ($detail.name -ceq $revision -and $detail.properties.active -eq $true -and
                    $detail.properties.provisioningState -in @('Provisioned', 'Succeeded') -and $detail.properties.healthState -eq 'Healthy' -and
                    $detail.properties.runningState -in @('Running', 'RunningAtMaxScale') -and @($detail.properties.template.containers).Count -eq 1 -and
                    $detail.properties.template.containers[0].image -ceq $Image) {
                    $ready = $true
                    break
                }
            }
            if ($attempt + 1 -lt $RolloutAttempts) { Start-Sleep -Seconds $RolloutDelaySeconds }
        }
        if (-not $ready) { throw "Exact image/latest ready revision verification timed out for $($target.name); stale readiness is not accepted." }
        $completed.Add($target.name)
        $target | Add-Member -NotePropertyName releasedRevision -NotePropertyValue $revision
        $target | Add-Member -NotePropertyName releasedState -NotePropertyValue (Get-Fingerprint (Get-PreservedState $expected))
        Write-Output "Verified: app=$($target.name); image=$Image; readyRevision=$revision"
    }
    foreach ($target in $targets) {
        $current = Invoke-AzureJson @('containerapp', 'show', '--resource-group', $ResourceGroup, '--name', $target.name)
        if ((Get-Fingerprint (Get-PreservedState $current)) -cne $target.releasedState -or
            $current.properties.provisioningState -ne 'Succeeded' -or
            $current.properties.latestRevisionName -cne $target.releasedRevision -or
            $current.properties.latestReadyRevisionName -cne $target.releasedRevision) {
            throw "The verified $($target.name) revision/configuration changed before both-target acceptance."
        }
    }
    & (Join-Path $PSScriptRoot 'Test-DeployedApp.ps1') -PublicOrigin $targets[0].origin -TenantId $TenantId `
        -WebClientId $targets[0].webClient -ExpectAdmission:$targets[0].live.admissionEnabled
    Write-Output 'Both exact Live revisions and read-only public Entra/anonymous smoke checks passed. Authenticated Teams journeys were not tested.'
} catch {
    throw "Development release failed; partial rollout is possible. Attempted targets=[$($touched -join ',')]; verified targets=[$($completed -join ',')]. $($_.Exception.Message) Inspect both revisions and the recovery record; no automatic rollback or Teams operation was attempted."
} finally {
    if ($directory -and (Test-Path -LiteralPath $directory)) { Remove-Item -LiteralPath $directory -Recurse -Force }
}
