[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$ParametersPath,
    [Parameter(Mandatory)][string]$OutputsPath,
    [ValidatePattern('^[a-z0-9][a-z0-9.-]+\.azurecr\.io/[a-z0-9_./-]+@sha256:[a-f0-9]{64}$')][string]$Image,
    [switch]$DeployWorkloads,
    [switch]$ProvisionRbac,
    [switch]$EnableSynchronization,
    [switch]$EnableAdmission,
    [switch]$EnableMonitoring,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$output = [IO.Path]::GetFullPath($OutputsPath)
if ($output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Resolved live deployment outputs must remain outside the repository.'
}
if ($SubscriptionId -eq [guid]::Empty) { throw 'Select an approved subscription.' }
if ($EnableAdmission -and (-not $EnableSynchronization -or -not $DeployWorkloads)) {
    throw 'Admission requires explicitly approved synchronization and workload deployment.'
}
if ($EnableSynchronization -and -not $DeployWorkloads) { throw 'Synchronization requires workload deployment.' }
if ($DeployWorkloads -and -not $Image) { throw 'Workload deployment requires an immutable image digest.' }
$parameters = Get-Content -Raw -LiteralPath $ParametersPath | ConvertFrom-Json
$settings = $parameters.parameters
foreach ($required in @('prefix', 'location', 'environmentId', 'registryName', 'cosmosAccountName', 'serviceBusName',
    'redisName', 'pubsubName', 'vaultName', 'notificationKeyNames', 'webClientId', 'syncClientId',
    'eventsClientId', 'trustedProxyCidr', 'liveConfig')) {
    if ($null -eq $settings.$required -or $null -eq $settings.$required.value) { throw "Missing live parameter: $required" }
}
foreach ($name in @('webClientId', 'syncClientId', 'eventsClientId')) {
    $id = [guid]::Empty
    if (-not [guid]::TryParse($settings.$name.value, [ref]$id) -or $id -eq [guid]::Empty) {
        throw "Replace the example $name before deployment."
    }
}
$configuration = $settings.liveConfig.value
$readConsistency = if ($configuration.readConsistency) { $configuration.readConsistency } else { 'Strong' }
if ($readConsistency -notin @('Strong', 'SessionPrimary')) {
    throw 'Live readConsistency must select Strong or SessionPrimary.'
}
if ($EnableMonitoring -and (-not $settings.logsWorkspaceId.value -or -not $settings.insightsConnectionString.value)) {
    throw 'Monitoring requires the approved Log Analytics workspace and backend instrumentation connection.'
}
if (-not $configuration.approvalReference -or $configuration.writerEpoch -le 0) {
    throw 'Live deployment requires a recorded approval reference and positive writer epoch.'
}
if (@($configuration.PSObject.Properties.Name | Where-Object {
    $_ -match '(?i)password|secret|token|private|connectionstring'
}).Count) { throw 'Live configuration must contain only nonsecret settings and protected references.' }
if ($DeployWorkloads) {
    if (-not $configuration.resources -or -not $configuration.subscriptionScopes -or
        -not $configuration.entitlementAuthority -or -not $configuration.entitlementScope -or
        -not $configuration.notificationCertificate -or -not $configuration.notificationCertificateId -or
        -not $configuration.notificationKeys) { throw 'Complete the live resource, policy and encryption configuration.' }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        [Convert]::FromBase64String($configuration.notificationCertificate))
    try {
        if ($certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) { throw 'Notification certificate is expired.' }
    } finally { $certificate.Dispose() }
}
$account = az account show --subscription $SubscriptionId --output json | ConvertFrom-Json
if ($LASTEXITCODE -or -not $account.tenantId) { throw 'Unable to resolve the selected tenant/subscription.' }
$expectedEnvironment = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.App/managedEnvironments/"
if (-not $settings.environmentId.value.StartsWith($expectedEnvironment, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The approved private platform must belong to the selected subscription and resource group.'
}
$cosmos = az cosmosdb show --subscription $SubscriptionId --resource-group $ResourceGroup `
    --name $settings.cosmosAccountName.value --output json | ConvertFrom-Json
if ($LASTEXITCODE -or $cosmos.publicNetworkAccess -ne 'Disabled' -or $cosmos.disableLocalAuth -ne $true) {
    throw 'The live platform requires disabled public access and Entra data-plane authentication.'
}
if ($readConsistency -eq 'Strong' -and $cosmos.consistencyPolicy.defaultConsistencyLevel -ne 'Strong') {
    throw 'The Strong profile requires Strong-consistency Cosmos; select SessionPrimary for the supported single-region Session alternative.'
}
if ($readConsistency -eq 'SessionPrimary' -and ($cosmos.consistencyPolicy.defaultConsistencyLevel -notin @('Session', 'Strong') -or
    @($cosmos.locations).Count -ne 1 -or $cosmos.enableMultipleWriteLocations -eq $true)) {
    throw 'SessionPrimary requires one Cosmos region, single-region writes and Session or Strong account consistency.'
}
$environment = az containerapp env show --subscription $SubscriptionId --resource-group $ResourceGroup `
    --name ($settings.environmentId.value.Split('/')[-1]) --output json | ConvertFrom-Json
if ($LASTEXITCODE -or $environment.properties.publicNetworkAccess -ne 'Disabled') {
    throw 'The live Container Apps environment must disable public network access.'
}
$template = Join-Path $root 'infra\live.bicep'
$arguments = @('--subscription', $SubscriptionId.ToString(), '--resource-group', $ResourceGroup,
    '--template-file', $template, '--parameters', ('@' + [IO.Path]::GetFullPath($ParametersPath)),
    "deployWorkloads=$($DeployWorkloads.IsPresent.ToString().ToLowerInvariant())",
    "provisionRbac=$($ProvisionRbac.IsPresent.ToString().ToLowerInvariant())",
    "enableSynchronization=$($EnableSynchronization.IsPresent.ToString().ToLowerInvariant())",
    "enableAdmission=$($EnableAdmission.IsPresent.ToString().ToLowerInvariant())",
    "enableMonitoring=$($EnableMonitoring.IsPresent.ToString().ToLowerInvariant())")
if ($Image) { $arguments += "image=$Image" }
az deployment group what-if @arguments --no-pretty-print
if ($LASTEXITCODE) { throw 'Live deployment what-if failed.' }
if (-not $Apply) { Write-Output 'What-if only; no live infrastructure, consent, Graph traffic or fixtures changed.'; return }
if (-not $PSCmdlet.ShouldProcess("$SubscriptionId/$ResourceGroup",
    'Deploy approved live topology/RBAC and selected synchronization/admission state; never seed fixtures')) { return }
$deployment = az deployment group create @arguments --name teams-reference-live --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Live deployment failed; inspect partial deployment before retrying.' }
New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
$deployment.properties.outputs | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $output -Encoding utf8
Write-Output "Live deployment outputs saved privately to $output. Entra bindings, network approvals and real integration/recovery evidence remain separate gates."
