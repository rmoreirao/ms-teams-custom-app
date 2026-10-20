[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$InventoryPath,
    [string]$ResourceGroup = 'rg-teams-reference-dev-swc',
    [string]$Location = 'swedencentral',
    [string]$CosmosLocation = $Location,
    [ValidateSet('Balanced_B0', 'Balanced_B1', 'Balanced_B3', 'Balanced_B5')][string]$RedisSku = 'Balanced_B0',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$inventory = Get-Content -Raw -LiteralPath $InventoryPath | ConvertFrom-Json
if (-not $inventory.applications.web.clientId -or -not $inventory.applications.events.clientId) {
    throw 'Create the web and callback application registrations before provisioning the platform.'
}
if ($inventory.bootstrapComplete -eq $false -or $inventory.users.Count -ne 3) {
    Write-Warning 'Entra bootstrap is incomplete. This deploys infrastructure only; Live application admission remains gated.'
}
$account = az account show --subscription $SubscriptionId --output json | ConvertFrom-Json
if ($LASTEXITCODE -or $account.tenantId -ne $inventory.tenantId) { throw 'Subscription and approved identity tenant do not match.' }
$networkFeature = az feature show --subscription $SubscriptionId --namespace Microsoft.Network `
    --name AllowBringYourOwnPublicIpAddress --query properties.state --output tsv
if ($LASTEXITCODE -or $networkFeature -ne 'Registered') {
    throw 'Register Microsoft.Network/AllowBringYourOwnPublicIpAddress and re-register Microsoft.Network before provisioning Container Apps.'
}
$parameters = @{
    resourceGroupName = @{ value = $ResourceGroup }
    location = @{ value = $Location }
    cosmosLocation = @{ value = $CosmosLocation }
    redisSku = @{ value = $RedisSku }
    webClientId = @{ value = $inventory.applications.web.clientId }
    eventsClientId = @{ value = $inventory.applications.events.clientId }
}
$file = [IO.Path]::GetTempFileName()
try {
    @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'; contentVersion = '1.0.0.0'; parameters = $parameters } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $file
    $template = Join-Path (Split-Path $PSScriptRoot -Parent) 'infra\subscription.bicep'
    $arguments = @('--subscription', $SubscriptionId.ToString(), '--location', $Location,
        '--template-file', $template, '--parameters', "@$file")
    az deployment sub what-if @arguments --no-pretty-print
    if ($LASTEXITCODE) { throw 'Platform what-if failed.' }
    if (-not $Apply) { Write-Output 'What-if only. Review cost and privileged role changes before applying.'; return }
    if ($PSCmdlet.ShouldProcess("$SubscriptionId/$ResourceGroup", 'Create billable private-network dev infrastructure and workload RBAC')) {
        az deployment sub create @arguments --name "teams-reference-platform-$Location" --output json
        if ($LASTEXITCODE) { throw 'Platform deployment failed.' }
    }
} finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
