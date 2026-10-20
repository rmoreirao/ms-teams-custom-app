[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$ParametersPath,
    [Parameter(Mandatory)][string]$Image,
    [Parameter(Mandatory)][ValidateSet('PrepareNotification','SessionCheck','RealtimeCheck','Inspect','Reauthorize','EnsurePairs','Probe','VerifyProbe')][string]$Operation,
    [guid]$RunId = [guid]::NewGuid(),
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)) + [IO.Path]::DirectorySeparatorChar
$path = [IO.Path]::GetFullPath($ParametersPath)
if ($path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Operation settings/evidence must stay outside the repository.' }
if ($Image -notmatch '^[a-z0-9][a-z0-9.-]+\.azurecr\.io/[a-z0-9_./-]+@sha256:[a-f0-9]{64}$') { throw 'Supply an immutable image.' }
$settings = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
if ($settings.schemaVersion -ne 1 -or -not $settings.liveConfig.approvalReference) { throw 'Supply explicit target settings and approval reference.' }
if ($Image.Split('/')[0] -ne $settings.registryServer) { throw 'Use the existing approved private registry.' }
$directory = Split-Path $path -Parent
function Read-OperationProof([object[]]$Lines) {
    foreach ($line in $Lines) {
        $text=[string]$line
        $start=$text.IndexOf('{"operation":')
        if ($start -lt 0) { continue }
        try { $candidate=$text.Substring($start) | ConvertFrom-Json }
        catch { continue }
        if ($candidate.operation -eq $Operation -and
            (-not $candidate.runId -or $candidate.runId -eq $RunId.ToString())) { return $candidate }
    }
    return $null
}
$subscription = $settings.subscriptionId
$group = $settings.resourceGroup
$jobName = $settings.operationJobName
$raw = az containerapp job show --subscription $subscription --resource-group $group --name $jobName --output json --only-show-errors
if ($LASTEXITCODE) { throw 'Unable to inspect the selected manual job.' }
$job = ($raw -join "`n") | ConvertFrom-Json
if ($job.properties.configuration.triggerType -ne 'Manual' -or $job.properties.template.containers.Count -ne 1 -or
    $job.properties.environmentId -ne $settings.environmentId) { throw 'Use only the existing manual, single-container job in the approved environment.' }
if ($null -ne $job.properties.configuration.dapr) { throw 'This scoped runner does not replace a job with Dapr configuration.' }
$job.properties.configuration.PSObject.Properties.Remove('dapr')
$raw = az containerapp job execution list --subscription $subscription --resource-group $group --name $jobName --output json --only-show-errors
if ($LASTEXITCODE) { throw 'Unable to check existing job activity.' }
$executions = ($raw -join "`n") | ConvertFrom-Json
if (@($executions | Where-Object { $_.properties.status -in @('Running','Processing','Starting') }).Count) { throw 'The existing job is busy; do not disturb it.' }
Write-Output "Plan: run $Operation once in the existing manual job, with automatic retries disabled, then restore its definition. No Teams send occurs except under the explicit Probe operation."
if (-not $Apply -or -not $PSCmdlet.ShouldProcess("$group/$jobName", "Run the scoped synthetic $Operation operation and restore the job")) { return }
$original = Join-Path $directory "job-before-$RunId.json"
foreach ($item in @($job.properties.template.containers) + @($job.properties.template.initContainers)) {
    if ($null -eq $item) { continue }
    $item.PSObject.Properties.Remove('imageType')
    if ($null -ne $item.resources) { $item.resources.PSObject.Properties.Remove('ephemeralStorage') }
}
@{ identity=$job.identity; properties=@{ configuration=$job.properties.configuration; template=$job.properties.template } } |
    ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $original -Encoding utf8
$identity = if ($Operation -eq 'PrepareNotification') { $settings.workerIdentity } else { $settings.apiIdentity }
$sourceRaw = az containerapp show --subscription $subscription --resource-group $group --name $settings.apiAppName --output json --only-show-errors
if ($LASTEXITCODE) { throw 'Unable to read the current Live API configuration for the scoped operation.' }
$source = ($sourceRaw -join "`n") | ConvertFrom-Json
if (($source.properties.template.containers[0].env | Where-Object name -eq 'App__GraphMode').value -ne 'Live') {
    throw 'Scoped operations require the existing Live API; retired runtime snapshots cannot be restored.'
}
$envs = @($source.properties.template.containers[0].env | Where-Object {
    $_.name -notin @('App__GraphMode','App__Role','Live__Configuration','App__Operation','AzureClientId','AZURE_CLIENT_ID') -and
    -not $_.name.StartsWith('LiveDiagnostics__') -and -not $_.name.StartsWith('LivePreparation__')
})
$live = ($settings.liveConfig | ConvertTo-Json -Depth 30 -Compress) | ConvertFrom-Json
$live | Add-Member -NotePropertyName hostedRoles -NotePropertyValue @('Api','Receiver','Control') -Force
$live | Add-Member -NotePropertyName synchronizationEnabled -NotePropertyValue $true -Force
$live | Add-Member -NotePropertyName admissionEnabled -NotePropertyValue $true -Force
$envs += @(
    @{name='App__GraphMode';value='Live'}, @{name='App__Role';value='Api'}, @{name='App__Operation';value=$Operation}
    @{name='Live__Configuration';value=($live | ConvertTo-Json -Depth 30 -Compress)}
    @{name='AzureClientId';value=$identity.clientId}, @{name='AZURE_CLIENT_ID';value=$identity.clientId}
    @{name='LiveDiagnostics__RunId';value=$RunId.ToString()}, @{name='LiveDiagnostics__HoldLogSeconds';value='60'}
    @{name='LiveDiagnostics__Container';value='session-primary-validation-azurepath'}
    @{name='LiveDiagnostics__ActorId';value=$settings.probeActorId}
    @{name='LiveDiagnostics__DirectChatsNotBefore';value=$settings.directChatsNotBefore}
    @{name='LiveDiagnostics__ResourceIds__0';value='graph-pilot-chat'}
    @{name='LiveDiagnostics__ResourceIds__1';value='graph-pilot-channel'}
    @{name='LivePreparation__KeyUri';value=$settings.notificationKeyUri}
    @{name='LivePreparation__VaultName';value=$settings.vaultName}
    @{name='LivePreparation__ApprovalReference';value=$live.approvalReference}
)
$job.identity = [pscustomobject]@{type='UserAssigned';userAssignedIdentities=@{ $identity.resourceId=@{} }}
foreach ($registry in $job.properties.configuration.registries) { $registry.identity=$identity.resourceId }
$job.properties.configuration.replicaRetryLimit=0
$job.properties.configuration.replicaTimeout=600
$job.properties.template.containers[0].image=$Image
$job.properties.template.containers[0].PSObject.Properties.Remove('imageType')
$job.properties.template.containers[0].resources.PSObject.Properties.Remove('ephemeralStorage')
$job.properties.template.containers[0].env=$envs
$patch = Join-Path $directory "job-operation-$RunId.json"
@{ identity=$job.identity;properties=@{configuration=$job.properties.configuration;template=$job.properties.template}} |
    ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $patch -Encoding utf8
$patched=$false
try {
    az rest --subscription $subscription --method patch --url "https://management.azure.com$($job.id)?api-version=2025-01-01" `
        --body "@$patch" --output none --only-show-errors
    if ($LASTEXITCODE) { throw 'The scoped job patch failed.' }
    $patched=$true
    $raw = az containerapp job start --subscription $subscription --resource-group $group --name $jobName --output json --only-show-errors
    if ($LASTEXITCODE) { throw 'The scoped job did not start; no operation is claimed.' }
    $execution = ($raw -join "`n") | ConvertFrom-Json
    $executionName = $execution.name
    if (-not $executionName) { throw 'The scoped execution identity was not returned.' }
    @{operation=$Operation;runId=$RunId.ToString();executionName=$executionName} | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $directory "execution-$Operation-$RunId.json") -Encoding utf8
    $logPath = Join-Path $directory "operation-$Operation-$RunId.log"
    $proofPath = Join-Path $directory "operation-$Operation-$RunId.json"
    $proof = $null
    $succeeded = $false
    for ($attempt=0;$attempt -lt 120;$attempt++) {
        if (-not $proof) {
            $logs = az containerapp job logs show --subscription $subscription --resource-group $group --name $jobName `
                --execution $executionName --container $job.properties.template.containers[0].name --tail 300 --format text --only-show-errors 2>&1
            if ($LASTEXITCODE -eq 0) {
                $logs | Set-Content -LiteralPath $logPath -Encoding utf8
                $proof=Read-OperationProof $logs
                if ($proof) { $proof | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $proofPath -Encoding utf8 }
            } else {
                $logs | Set-Content -LiteralPath "$logPath.stream-error.txt" -Encoding utf8
            }
        }
        $raw = az containerapp job execution show --subscription $subscription --resource-group $group --name $jobName `
            --job-execution-name $executionName --output json --only-show-errors
        if ($LASTEXITCODE) { throw 'Unable to verify the scoped execution status.' }
        $observed=($raw -join "`n") | ConvertFrom-Json
        if ($observed.properties.status -eq 'Succeeded') { $succeeded=$true;break }
        if ($observed.properties.status -in @('Failed','Stopped')) { throw "The scoped operation failed; private logs: $logPath" }
        Start-Sleep -Seconds 3
    }
    if ($succeeded -and -not $proof) {
        if ($jobName -notmatch '^[a-z0-9-]+$' -or $executionName -notmatch '^[a-z0-9-]+$') {
            throw 'Unexpected execution identifier; persistent-log recovery was not attempted.'
        }
        $raw=az resource list --subscription $subscription --resource-group $group `
            --resource-type Microsoft.OperationalInsights/workspaces --output json --only-show-errors
        if ($LASTEXITCODE) { throw 'Unable to resolve the existing private operation-log workspace.' }
        $workspaces=@(($raw -join "`n") | ConvertFrom-Json)
        if ($workspaces.Count -ne 1) { throw 'Persistent-log recovery requires the single existing workspace in the approved resource group.' }
        $queryPath=Join-Path $directory "operation-$Operation-$RunId.log-query.json"
        $resultPath=Join-Path $directory "operation-$Operation-$RunId.log-result.json"
        @{query="ContainerAppConsoleLogs_CL | where TimeGenerated > ago(2h) and ContainerJobName_s == '$jobName' | where ContainerGroupName_s startswith '$executionName' | where Log_s contains 'operation' | project Log_s | take 10"} |
            ConvertTo-Json | Set-Content -LiteralPath $queryPath -Encoding utf8
        for ($attempt=0;$attempt -lt 12 -and -not $proof;$attempt++) {
            az rest --subscription $subscription --method post `
                --url "https://management.azure.com$($workspaces[0].id)/api/query?api-version=2020-08-01" `
                --body "@$queryPath" --output json --only-show-errors --output-file $resultPath
            if ($LASTEXITCODE) { throw 'Persistent operation-log query failed; inspect the original execution instead of replaying it.' }
            $result=Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            $lines=@($result.Tables[0].Rows | ForEach-Object { $_[0] })
            $proof=Read-OperationProof $lines
            if ($proof) {
                $lines | Set-Content -LiteralPath $logPath -Encoding utf8
                $proof | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath $proofPath -Encoding utf8
            } else { Start-Sleep -Seconds 10 }
        }
    }
    if (-not $succeeded -or -not $proof) { throw "Operation success/proof was not established. Inspect the original execution; do not replay an uncertain probe. Private logs: $logPath" }
    Write-Output "Verified $Operation; private evidence: $proofPath"
} finally {
    if ($patched) {
        az rest --subscription $subscription --method patch --url "https://management.azure.com$($job.id)?api-version=2025-01-01" `
            --body "@$original" --output none --only-show-errors
        if ($LASTEXITCODE) { throw "The original manual job definition was not restored; restore it from $original before any other run." }
    }
}
