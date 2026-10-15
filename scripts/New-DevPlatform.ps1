[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$InventoryPath,
    [string]$ResourceGroup = 'rg-teams-reference-dev',
    [string]$CosmosLocation = 'westeurope',
    [ValidateSet('Balanced_B0', 'Balanced_B1')][string]$RedisSku = 'Balanced_B0',
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$inventory = Get-Content -Raw -LiteralPath $InventoryPath | ConvertFrom-Json
if ($inventory.users.Count -ne 4) { throw 'Create the four approved test identities before provisioning.' }
$account = az account show --subscription $SubscriptionId --output json | ConvertFrom-Json
if ($LASTEXITCODE -or $account.tenantId -ne $inventory.tenantId) { throw 'Subscription and approved identity tenant do not match.' }
$parameters = @{
    resourceGroupName = @{ value = $ResourceGroup }
    cosmosLocation = @{ value = $CosmosLocation }
    redisSku = @{ value = $RedisSku }
    webClientId = @{ value = $inventory.applications.web.clientId }
    eventsClientId = @{ value = $inventory.applications.events.clientId }
    fixtureActorIds = @{ value = @($inventory.users | ForEach-Object id) }
}
$file = [IO.Path]::GetTempFileName()
try {
    @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'; contentVersion = '1.0.0.0'; parameters = $parameters } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $file
    $template = Join-Path (Split-Path $PSScriptRoot -Parent) 'infra\subscription.bicep'
    $arguments = @('--subscription', $SubscriptionId.ToString(), '--location', 'westeurope',
        '--template-file', $template, '--parameters', "@$file")
    az deployment sub what-if @arguments --no-pretty-print
    if ($LASTEXITCODE) { throw 'Platform what-if failed.' }
    if (-not $Apply) { Write-Output 'What-if only. Review cost and privileged role changes before applying.'; return }
    if ($PSCmdlet.ShouldProcess("$SubscriptionId/$ResourceGroup", 'Create billable private-network dev infrastructure and workload RBAC')) {
        az deployment sub create @arguments --name teams-reference-platform --output json
        if ($LASTEXITCODE) { throw 'Platform deployment failed.' }
    }
} finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
