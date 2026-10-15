[CmdletBinding()]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$JobName
)
$ErrorActionPreference = 'Stop'
$execution = az containerapp job start --subscription $SubscriptionId --resource-group $ResourceGroup --name $JobName --output json | ConvertFrom-Json
if ($LASTEXITCODE -or -not $execution.name) { throw 'Fixture job did not start.' }
for ($attempt = 0; $attempt -lt 60; $attempt++) {
    $status = az containerapp job execution show --subscription $SubscriptionId --resource-group $ResourceGroup --name $JobName --job-execution-name $execution.name --query properties.status --output tsv
    if ($LASTEXITCODE) { throw 'Unable to read fixture job status.' }
    if ($status -eq 'Succeeded') { Write-Output "Fixture job $($execution.name) succeeded. Review the dataset manifest and authenticated conversation assertions."; return }
    if ($status -in @('Failed', 'Stopped', 'Degraded')) { throw "Fixture job failed: $status" }
    Start-Sleep -Seconds 5
}
throw 'Fixture job did not complete within the bounded wait.'
