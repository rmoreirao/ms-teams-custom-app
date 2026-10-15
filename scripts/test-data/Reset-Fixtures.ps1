[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [Parameter(Mandatory)][string]$JobName
)
$ErrorActionPreference = 'Stop'
if (-not $PSCmdlet.ShouldProcess("$ResourceGroup/$JobName", 'Reset only the six allowlisted synthetic conversations and invalidate command windows')) { return }
$job = az containerapp job show --subscription $SubscriptionId --resource-group $ResourceGroup --name $JobName --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Unable to inspect the synthetic seed job.' }
$container = $job.properties.template.containers | Where-Object name -eq 'seed'
if (-not $container -or ($container.env | Where-Object name -eq 'App__GraphMode').value -ne 'Simulator' -or
    ($container.env | Where-Object name -eq 'App__Role').value -ne 'Seed') { throw 'The target is not a simulator seed job.' }
($container.env | Where-Object name -eq 'App__Role').value = 'Reset'
$container.env += [pscustomobject]@{ name = 'App__AllowSyntheticReset'; value = 'true' }
$file = [IO.Path]::GetTempFileName()
try {
    $job.properties.template | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $file
    $execution = az containerapp job start --subscription $SubscriptionId --resource-group $ResourceGroup --name $JobName --yaml $file --output json | ConvertFrom-Json
    if ($LASTEXITCODE) { throw 'Reset execution did not start.' }
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $status = az containerapp job execution show --subscription $SubscriptionId --resource-group $ResourceGroup --name $JobName --job-execution-name $execution.name --query properties.status --output tsv
        if ($LASTEXITCODE) { throw 'Unable to read reset execution status.' }
        if ($status -eq 'Succeeded') { Write-Output 'Synthetic fixture reset and verification completed; operation evidence was retained.'; return }
        if ($status -in @('Failed', 'Stopped', 'Degraded')) { throw "Reset failed: $status" }
        Start-Sleep -Seconds 5
    }
    throw 'Reset did not finish within the bounded wait.'
} finally { Remove-Item -LiteralPath $file -ErrorAction SilentlyContinue }
