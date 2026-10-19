[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$AppName,
    [Parameter(Mandatory)][string]$ParametersPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9.-]+\.azurecr\.io/[a-z0-9_./-]+@sha256:[a-f0-9]{64}$')][string]$Image,
    [switch]$Apply
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))+[IO.Path]::DirectorySeparatorChar
$path=[IO.Path]::GetFullPath($ParametersPath)
if ($path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)) { throw 'Pilot parameter/snapshot files must remain outside the repository.' }
$settings=(Get-Content -Raw -LiteralPath $path | ConvertFrom-Json).parameters
$actors=@($settings.fixtureActorIds.value)
$pilot=$settings.graphPilotConfig.value
if ($actors.Count -ne 3 -or @($actors | Sort-Object -Unique).Count -ne 3 -or
    -not $pilot.notBefore -or $pilot.resources.Count -notin @(1,2)) { throw 'An explicit three-user dedicated Teams scope is required.' }
$app=az containerapp show --subscription $SubscriptionId --resource-group $ResourceGroup --name $AppName --output json | ConvertFrom-Json
if ($LASTEXITCODE -or $app.properties.provisioningState -ne 'Succeeded' -or $app.properties.template.containers.Count -ne 1 -or
    $app.properties.configuration.activeRevisionsMode -ne 'Single') {
    throw 'The selected existing API must be stable, use Single revision mode and have exactly one container.'
}
$envs=@($app.properties.template.containers[0].env)
if (($envs | Where-Object name -eq 'App__Role').value -ne 'Api' -or
    ($envs | Where-Object name -eq 'AzureAd__ClientId').value -ne $settings.webClientId.value) {
    throw 'The existing API role/application does not match the approved parameters.'
}
$origin=($envs | Where-Object name -eq 'App__PublicOrigin').value
$tenant=($envs | Where-Object name -eq 'App__TenantId').value
Write-Output "Targeted plan: update only the existing $AppName API image and GraphPilot/actor settings. Preserve identities, network, data services, replica counts and sizes. No seed job or infrastructure deployment."
if (-not $Apply -or -not $PSCmdlet.ShouldProcess("$SubscriptionId/$ResourceGroup/$AppName",'Update the existing API only to the approved GraphPilot image/configuration')) { return }
$directory=Split-Path $path -Parent
$app.properties.template | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $directory 'api-template.before-update.json') -Encoding utf8
$container=$app.properties.template.containers[0]
$container.image=$Image
$container.PSObject.Properties.Remove('imageType')
$container.env=@($envs | Where-Object {$_.name -notin @('App__GraphMode','GraphPilot__Configuration','App__RealtimeAdmissionReady') -and
    -not $_.name.StartsWith('App__FixtureActorIds__')})
$container.env+=@(
    [pscustomobject]@{name='App__GraphMode';value='GraphPilot'}
    [pscustomobject]@{name='GraphPilot__Configuration';value=($pilot | ConvertTo-Json -Depth 15 -Compress)}
    [pscustomobject]@{name='App__RealtimeAdmissionReady';value='false'}
)
for ($i=0;$i -lt $actors.Count;$i++) { $container.env += [pscustomobject]@{name="App__FixtureActorIds__$i";value=$actors[$i]} }
$app.properties.template.scale.PSObject.Properties.Remove('cooldownPeriod')
$app.properties.template.scale.PSObject.Properties.Remove('pollingInterval')
$patch=Join-Path $directory 'api-template.update.json'
@{properties=@{template=$app.properties.template}} | ConvertTo-Json -Depth 45 | Set-Content -LiteralPath $patch -Encoding utf8
az rest --subscription $SubscriptionId --method patch `
    --url "https://management.azure.com$($app.id)?api-version=2024-03-01" --body "@$patch" --output none
if ($LASTEXITCODE) { throw 'Targeted API update failed; inspect the existing revision before retrying.' }
$ready=$false
for ($attempt=0;$attempt -lt 24;$attempt++) {
    $observed=az containerapp show --subscription $SubscriptionId --resource-group $ResourceGroup --name $AppName --output json | ConvertFrom-Json
    if ($LASTEXITCODE) { throw 'Unable to verify the targeted API revision; no successful rollout is claimed.' }
    if ($observed.properties.provisioningState -eq 'Succeeded' -and
        $observed.properties.template.containers[0].image -eq $Image -and
        $observed.properties.latestRevisionName -and
        $observed.properties.latestReadyRevisionName -eq $observed.properties.latestRevisionName) {
        try {
            $health=Invoke-RestMethod "$origin/health/ready" -TimeoutSec 15
            if ($health.status -eq 'ready' -and $health.mode -eq 'graph-pilot' -and $health.actors -eq 3) { $ready=$true;break }
        } catch [Net.Http.HttpRequestException] { Write-Warning 'Waiting for targeted GraphPilot readiness.' }
    }
    Start-Sleep -Seconds 10
}
if (-not $ready) { throw 'Targeted GraphPilot readiness did not pass; no Teams journey is claimed.' }
& (Join-Path $PSScriptRoot 'Test-DeployedApp.ps1') -PublicOrigin $origin -TenantId $tenant `
    -WebClientId $settings.webClientId.value -ExpectedMode GraphPilot
Write-Output 'The existing API is GraphPilot-ready. Run the separate three-user browser/Graph read-back validation.'
