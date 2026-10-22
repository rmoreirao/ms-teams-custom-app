[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$ParametersPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9.-]+\.azurecr\.io/[a-z0-9_./-]+@sha256:[a-f0-9]{64}$')][string]$Image,
    [switch]$EnableAdmission,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)) + [IO.Path]::DirectorySeparatorChar
$path = [IO.Path]::GetFullPath($ParametersPath)
if ($path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Resolved synthetic settings must remain private, outside the repository.' }
$settings = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
$live = $settings.liveConfig
if ($Image.Split('/')[0] -ne $settings.registryServer) { throw 'Use the existing approved private registry.' }
if ($settings.schemaVersion -ne 1 -or $live.authorityMode -ne 'DelegatedSynthetic' -or $live.readConsistency -cne 'Session' -or
    @($live.syntheticActorIds).Count -ne 3 -or @($live.syntheticActorIds | Sort-Object -Unique).Count -ne 3 -or
    @($live.resources).Count -lt 2 -or @($live.resources).Count -gt 5 -or -not $live.approvalReference) {
    throw 'Use the explicit three-user/two-to-five-resource synthetic Session profile.'
}
$subscription = $settings.subscriptionId
$group = $settings.resourceGroup
$directory = Split-Path $path -Parent
$targets = @(
    @{ name = $settings.apiAppName; role = 'Api'; hosted = @('Api','Receiver','Control') },
    @{ name = $settings.workerAppName; role = 'Projector'; hosted = @('Projector','Delivery') }
)
foreach ($target in $targets) {
    $raw = az containerapp show --subscription $subscription --resource-group $group --name $target.name --output json --only-show-errors
    if ($LASTEXITCODE) { throw 'Unable to read the selected existing app.' }
    $app = ($raw -join "`n") | ConvertFrom-Json
    if ($app.properties.provisioningState -ne 'Succeeded' -or $app.properties.configuration.activeRevisionsMode -ne 'Single' -or
        $app.properties.template.containers.Count -ne 1 -or $app.properties.managedEnvironmentId -ne $settings.environmentId) {
        throw 'Refusing an unstable, multi-container or unexpected existing target.'
    }
    $container = $app.properties.template.containers[0]
    $old = @($container.env)
    $currentClient = ($old | Where-Object name -eq 'AzureAd__ClientId').value
    if ($target.role -eq 'Api' -and $currentClient -ne $live.syncClientId) { throw 'The existing delegated web registration changed.' }
    Write-Output "Plan: update only $($target.name) to the synthetic live image/configuration; preserve identity, ingress, services, CPU, memory and replica limits."
    if (-not $Apply -or -not $PSCmdlet.ShouldProcess($app.id, 'Apply the bounded synthetic live image/configuration, preserving sizing and identities')) { continue }
    $snapshot = Join-Path $directory "$($target.name).before-synthetic-template.json"
    if (-not (Test-Path -LiteralPath $snapshot)) {
        $app.properties.template | ConvertTo-Json -Depth 45 | Set-Content -LiteralPath $snapshot -Encoding utf8
    }
    $copy = ($live | ConvertTo-Json -Depth 30 -Compress) | ConvertFrom-Json
    $copy | Add-Member -NotePropertyName hostedRoles -NotePropertyValue $target.hosted -Force
    $copy | Add-Member -NotePropertyName synchronizationEnabled -NotePropertyValue $true -Force
    $copy | Add-Member -NotePropertyName admissionEnabled -NotePropertyValue $EnableAdmission.IsPresent -Force
    $container.image = $Image
    $container.PSObject.Properties.Remove('imageType')
    $container.resources.PSObject.Properties.Remove('ephemeralStorage')
    $container.env = @($old | Where-Object { $_.name -notin @('App__GraphMode','App__Role','Live__Configuration','App__RealtimeAdmissionReady','App__Operation','GraphPilot__Configuration') -and
        -not $_.name.StartsWith('App__FixtureActorIds__') })
    $container.env += @(
        [pscustomobject]@{ name='App__GraphMode'; value='Live' }
        [pscustomobject]@{ name='App__Role'; value=$target.role }
        [pscustomobject]@{ name='Live__Configuration'; value=($copy | ConvertTo-Json -Depth 30 -Compress) }
        [pscustomobject]@{ name='App__RealtimeAdmissionReady'; value='true' }
    )
    $app.properties.template.scale.PSObject.Properties.Remove('cooldownPeriod')
    $app.properties.template.scale.PSObject.Properties.Remove('pollingInterval')
    $patch = Join-Path $directory "$($target.name).synthetic-patch.json"
    @{ properties = @{ template = $app.properties.template } } | ConvertTo-Json -Depth 50 |
        Set-Content -LiteralPath $patch -Encoding utf8
    az rest --subscription $subscription --method patch --url "https://management.azure.com$($app.id)?api-version=2024-03-01" `
        --body "@$patch" --output none --only-show-errors
    if ($LASTEXITCODE) { throw 'The targeted app patch failed; no successful rollout is claimed.' }
    $ready = $false
    for ($attempt = 0; $attempt -lt 36; $attempt++) {
        $raw = az containerapp show --subscription $subscription --resource-group $group --name $target.name --output json --only-show-errors
        if ($LASTEXITCODE) { throw 'Unable to observe the targeted revision.' }
        $observed = ($raw -join "`n") | ConvertFrom-Json
        if ($observed.properties.provisioningState -eq 'Succeeded' -and
            $observed.properties.template.containers[0].image -eq $Image -and
            $observed.properties.latestRevisionName -eq $observed.properties.latestReadyRevisionName) { $ready=$true; break }
        Start-Sleep -Seconds 5
    }
    if (-not $ready) { throw "The targeted $($target.name) revision did not become ready; leave admission fenced and inspect it." }
    Write-Output "Ready revision: $($observed.properties.latestReadyRevisionName); synthetic admission=$($EnableAdmission.IsPresent)."
}
