[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$ParametersPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9.-]+\.azurecr\.io/[a-z0-9_./-]+@sha256:[a-f0-9]{64}$')][string]$Image,
    [switch]$EnableRealtimeAdmission,
    [ValidateSet('Simulator', 'GraphPilot')][string]$GraphMode = 'Simulator',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$template = Join-Path $root 'infra\main.bicep'
$settings = Get-Content -Raw -LiteralPath $ParametersPath | ConvertFrom-Json
$actorIds = @($settings.parameters.fixtureActorIds.value)
if ($actorIds.Count -ne 3 -or @($actorIds | Sort-Object -Unique).Count -ne 3 -or
    @($actorIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count) {
    throw 'Three distinct approved Entra fixture actor IDs are required before deploying application workloads.'
}
if ($GraphMode -eq 'GraphPilot' -and (-not $settings.parameters.graphPilotConfig.value.resources -or
    -not $settings.parameters.graphPilotConfig.value.notBefore)) {
    throw 'GraphPilot requires an explicit dedicated-resource configuration.'
}
$retiredActorId = $settings.parameters.retiredFixtureActorId.value
if ($retiredActorId) {
    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($retiredActorId, [ref]$parsed) -or $parsed -eq [guid]::Empty -or $retiredActorId -in $actorIds) {
        throw 'The explicitly retired fixture actor must be a valid ID distinct from the active actors.'
    }
}
$parameters = '@' + [IO.Path]::GetFullPath($ParametersPath)
$admission = $EnableRealtimeAdmission.IsPresent.ToString().ToLowerInvariant()
$arguments = @('--subscription', $SubscriptionId.ToString(), '--resource-group', $ResourceGroup,
    '--template-file', $template, '--parameters', $parameters, 'deployWorkloads=true', 'provisionRbac=false',
    "enableRealtimeAdmission=$admission", "graphMode=$GraphMode", "image=$Image")
az deployment group what-if @arguments --no-pretty-print
if ($LASTEXITCODE) { throw 'Deployment what-if failed; nothing was deployed.' }
if (-not $Apply) { Write-Output 'What-if only. Use -Apply after approving billable resources and privileged bootstrap.'; return }
if (-not $PSCmdlet.ShouldProcess("$SubscriptionId/$ResourceGroup", 'Deploy billable synthetic dev workloads')) { return }
$deployment = az deployment group create @arguments --name teams-reference-release --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Workload deployment failed.' }
$outputs = $deployment.properties.outputs
$origin = $outputs.publicOrigin.value
$seed = $outputs.seedJobName.value
if ($GraphMode -eq 'Simulator') {
    & (Join-Path $PSScriptRoot 'test-data\Invoke-Fixtures.ps1') -SubscriptionId $SubscriptionId -ResourceGroup $ResourceGroup -JobName $seed
}
if (-not $EnableRealtimeAdmission -and $GraphMode -eq 'Simulator') {
    Write-Output "Initial workload deployed at $origin. Complete Entra binding and approve the Front Door private origin, then enable real-time admission."
    return
}
$ready = $false
for ($attempt = 0; $attempt -lt 24; $attempt++) {
    try {
        $health = Invoke-RestMethod "$origin/health/ready" -TimeoutSec 10
        $expected = if ($GraphMode -eq 'GraphPilot') { 'graph-pilot' } else { 'simulator' }
        if ($health.status -eq 'ready' -and $health.mode -eq $expected) { $ready = $true; break }
    } catch [Net.Http.HttpRequestException] { Write-Warning "Front Door readiness attempt $($attempt + 1) failed." }
    Start-Sleep -Seconds 10
}
if (-not $ready) { throw 'Public dev readiness did not pass. Check private-origin approval, DNS, identity and runtime logs.' }
$tenant = az account show --subscription $SubscriptionId --query tenantId --output tsv
if ($LASTEXITCODE) { throw 'Unable to verify the deployment tenant for public smoke checks.' }
& (Join-Path $PSScriptRoot 'Test-DeployedApp.ps1') -PublicOrigin $origin -TenantId $tenant `
    -WebClientId $settings.parameters.webClientId.value -ExpectedMode $GraphMode
Write-Output "$GraphMode service is ready at $origin. Entra and real Teams journeys require separate authenticated validation."
