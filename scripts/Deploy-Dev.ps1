[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$ParametersPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9.-]+\.azurecr\.io/[a-z0-9_./-]+@sha256:[a-f0-9]{64}$')][string]$Image,
    [switch]$EnableRealtimeAdmission,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$template = Join-Path $root 'infra\main.bicep'
$settings = Get-Content -Raw -LiteralPath $ParametersPath | ConvertFrom-Json
$actorIds = @($settings.parameters.fixtureActorIds.value)
if ($actorIds.Count -ne 4 -or @($actorIds | Sort-Object -Unique).Count -ne 4 -or
    @($actorIds | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count) {
    throw 'Four distinct approved Entra fixture actor IDs are required before deploying application workloads.'
}
$parameters = '@' + [IO.Path]::GetFullPath($ParametersPath)
$admission = $EnableRealtimeAdmission.IsPresent.ToString().ToLowerInvariant()
$arguments = @('--subscription', $SubscriptionId.ToString(), '--resource-group', $ResourceGroup,
    '--template-file', $template, '--parameters', $parameters, 'deployWorkloads=true', 'provisionRbac=false',
    "enableRealtimeAdmission=$admission", "image=$Image")
az deployment group what-if @arguments --no-pretty-print
if ($LASTEXITCODE) { throw 'Deployment what-if failed; nothing was deployed.' }
if (-not $Apply) { Write-Output 'What-if only. Use -Apply after approving billable resources and privileged bootstrap.'; return }
if (-not $PSCmdlet.ShouldProcess("$SubscriptionId/$ResourceGroup", 'Deploy billable synthetic dev workloads')) { return }
$deployment = az deployment group create @arguments --name teams-reference-release --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Workload deployment failed.' }
$outputs = $deployment.properties.outputs
$origin = $outputs.publicOrigin.value
$seed = $outputs.seedJobName.value
& (Join-Path $PSScriptRoot 'test-data\Invoke-Fixtures.ps1') -SubscriptionId $SubscriptionId -ResourceGroup $ResourceGroup -JobName $seed
if (-not $EnableRealtimeAdmission) {
    Write-Output "Initial workload deployed at $origin. Complete Entra binding and approve the Front Door private origin, then enable real-time admission."
    return
}
$ready = $false
for ($attempt = 0; $attempt -lt 24; $attempt++) {
    try {
        $health = Invoke-RestMethod "$origin/health/ready" -TimeoutSec 10
        if ($health.status -eq 'ready' -and $health.mode -eq 'simulator') { $ready = $true; break }
    } catch [Net.Http.HttpRequestException] { Write-Warning "Front Door readiness attempt $($attempt + 1) failed." }
    Start-Sleep -Seconds 10
}
if (-not $ready) { throw 'Public dev readiness did not pass. Check private-origin approval, DNS, identity and runtime logs.' }
$tenant = az account show --subscription $SubscriptionId --query tenantId --output tsv
if ($LASTEXITCODE) { throw 'Unable to verify the deployment tenant for public smoke checks.' }
& (Join-Path $PSScriptRoot 'Test-DeployedApp.ps1') -PublicOrigin $origin -TenantId $tenant `
    -WebClientId $settings.parameters.webClientId.value
Write-Output "Synthetic dev service is ready at $origin. Entra browser scenarios require separate authenticated validation."
