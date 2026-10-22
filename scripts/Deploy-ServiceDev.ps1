[CmdletBinding(SupportsShouldProcess,ConfirmImpact='High')]
param(
    [Parameter(Mandatory)][string]$TargetsPath,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]{5,50}$')][string]$RegistryName,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9.-]+\.azurecr\.io/[a-z0-9_./-]+@sha256:[a-f0-9]{64}$')][string]$Image,
    [string]$Confirmation,
    [switch]$Apply,
    [ValidateRange(1,120)][int]$RolloutAttempts=36,
    [ValidateRange(0,60)][int]$RolloutDelaySeconds=5
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'ServiceDevContract.psm1') -Force
$root=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))+[IO.Path]::DirectorySeparatorChar
$path=[IO.Path]::GetFullPath($TargetsPath)
if($path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){throw 'Resolved service target and access inventories must remain private and outside the repository.'}
$contract=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -Depth 100
Assert-ServiceDevTargets $contract
if(-not $contract.cosmosAccountName -or -not $contract.primaryReaderRoleId){throw 'Resolve the existing Cosmos account and reviewed primary-read role.'}
if($Apply -and -not $WhatIfPreference -and $Confirmation -cne 'DEPLOY-DEV'){throw 'An applied service-role release requires DEPLOY-DEV.'}
function Read-Azure([string[]]$Arguments){
    $raw=az @Arguments --subscription $contract.subscriptionId --output json --only-show-errors
    if($LASTEXITCODE -or -not $raw){throw "Service release read failed: $($Arguments[0..1]-join ' ')."}
    return ($raw-join "`n")|ConvertFrom-Json -Depth 100
}
function Assert-Revision($Revision,[string]$ExpectedName,[string]$ExpectedImage){
    if($Revision.name -cne $ExpectedName -or $Revision.properties.active -ne $true -or
        $Revision.properties.provisioningState -notin @('Provisioned','Succeeded') -or $Revision.properties.healthState -ne 'Healthy' -or
        $Revision.properties.runningState -notin @('Running','RunningAtMaxScale') -or
        @($Revision.properties.template.containers).Count -ne 1 -or $Revision.properties.template.containers[0].image -cne $ExpectedImage){
        throw "Exact service revision is not healthy: $ExpectedName."
    }
}
$account=Read-Azure @('account','show')
if($account.id -ne $contract.subscriptionId -or $account.tenantId -ne $contract.tenantId){throw 'The Azure account differs from the reviewed service boundary.'}
$registry=Read-Azure @('acr','show','--name',$RegistryName)
if($registry.loginServer -cne "$RegistryName.azurecr.io" -or $Image.Split('/')[0] -cne $registry.loginServer){throw 'The image registry differs from the approved private registry.'}
$manifest=Read-Azure @('acr','repository','show','--name',$RegistryName,'--image',$Image.Substring($registry.loginServer.Length+1))
if($manifest.digest -cne $Image.Split('@')[1]){throw 'The immutable service image was not verified.'}
$cosmos=Read-Azure @('cosmosdb','show','--resource-group',$contract.resourceGroup,'--name',$contract.cosmosAccountName)
if($cosmos.publicNetworkAccess -ne 'Disabled' -or $cosmos.disableLocalAuth -ne $true -or
    $cosmos.consistencyPolicy.defaultConsistencyLevel -notin @('Session','Strong') -or @($cosmos.locations).Count -ne 1 -or
    $cosmos.enableMultipleWriteLocations -eq $true){throw 'The existing single-region private Session/current-read storage contract changed.'}
$access=Read-Azure @('rest','--method','get','--url',"https://management.azure.com$($cosmos.id)/sqlRoleAssignments?api-version=2024-05-15")
if($contract.primaryReaderRoleId -notmatch "^$([regex]::Escape($cosmos.id))/sqlRoleDefinitions/[^/]+$"){throw 'The reader role leaves the approved Cosmos account.'}
$reader=Read-Azure @('rest','--method','get','--url',"https://management.azure.com$($contract.primaryReaderRoleId)?api-version=2024-05-15")
Assert-ServiceDevReaderRole $reader $cosmos.id
Assert-ServiceDevDataAccess $access.value $cosmos.id $contract.primaryReaderRoleId $contract
$targets=@()
foreach($role in @(Get-ServiceDevRoles)){
    $binding=$contract.roleBindings.$role
    $app=Read-Azure @('containerapp','show','--resource-group',$contract.resourceGroup,'--name',$binding.appName)
    Assert-ServiceDevApp $app $role $contract $registry.loginServer
    if($app.properties.template.containers[0].image.Split('@')[0] -cne $Image.Split('@')[0]){throw "The $role release cannot change image repositories."}
    $revision=Read-Azure @('containerapp','revision','show','--resource-group',$contract.resourceGroup,'--name',$binding.appName,
        '--revision',$app.properties.latestReadyRevisionName)
    Assert-Revision $revision $app.properties.latestReadyRevisionName $app.properties.template.containers[0].image
    $targets+=@{role=$role;name=$binding.appName;app=$app;fingerprint=(Get-ServiceDevFingerprint (Get-ServiceDevPreservedState $app))}
}
foreach($setting in @('Azure__CosmosEndpoint','Azure__Redis','Azure__ServiceBus','Azure__PubSub','Azure__ProtectionKey',
    'Callbacks__Audience','Callbacks__ClientId','Callbacks__PrincipalId','App__TrustedProxyNetworks__0')){
    $values=@($targets|ForEach-Object{($_.app.properties.template.containers[0].env|Where-Object name -eq $setting).value}|Sort-Object -Unique)
    if($values.Count -ne 1){throw "The five roles disagree on existing backend/callback configuration: $setting."}
}
foreach($target in $targets){
    Write-Output "Recovery: role=$($target.role); app=$($target.name); priorImage=$($target.app.properties.template.containers[0].image); priorReadyRevision=$($target.app.properties.latestReadyRevisionName)"
}
$busEndpoint=($targets[0].app.properties.template.containers[0].env|Where-Object name -eq 'Azure__ServiceBus').value
if($busEndpoint -notmatch '^(?<name>[a-z0-9-]+)\.servicebus\.windows\.net$'){throw 'Resolve the existing approved Service Bus namespace endpoint before release.'}
$refresh=Read-Azure @('servicebus','queue','show','--resource-group',$contract.resourceGroup,
    '--namespace-name',$Matches.name,'--name','graph-read-refresh')
$refreshAccess=Read-Azure @('role','assignment','list','--scope',$refresh.id,'--include-inherited')
Assert-ServiceDevReadRefresh $refresh $refreshAccess $contract
Write-Output 'Plan: image-only release to five existing isolated service workloads. No identity, grants, provisioning, admission, edge, scale or data changes.'
if(-not $Apply -or -not $PSCmdlet.ShouldProcess(($targets.name-join ', '),"Release immutable service image $Image")){return}
$directory=Join-Path (Split-Path $path -Parent) ('.service-release-'+[guid]::NewGuid().ToString('N'))
$attempted=[Collections.Generic.List[string]]::new()
$verified=[Collections.Generic.List[string]]::new()
try{
    foreach($target in $targets){
        $current=Read-Azure @('containerapp','show','--resource-group',$contract.resourceGroup,'--name',$target.name)
        if((Get-ServiceDevFingerprint (Get-ServiceDevPreservedState $current)) -cne $target.fingerprint -or
            $current.properties.latestRevisionName -cne $target.app.properties.latestRevisionName -or
            $current.properties.latestReadyRevisionName -cne $target.app.properties.latestReadyRevisionName){throw 'A target changed after all-target preflight.'}
    }
    foreach($target in $targets){
        $current=Read-Azure @('containerapp','show','--resource-group',$contract.resourceGroup,'--name',$target.name)
        if((Get-ServiceDevFingerprint (Get-ServiceDevPreservedState $current)) -cne $target.fingerprint){throw "The $($target.role) configuration changed before update."}
        $expected=$target.app|ConvertTo-Json -Depth 100|ConvertFrom-Json -Depth 100
        $revision=$expected.properties.latestReadyRevisionName
        if($expected.properties.template.containers[0].image -cne $Image){
            $suffix='dev-'+[guid]::NewGuid().ToString('N').Substring(0,16)
            $revision="$($target.name)--$suffix"
            $expected.properties.template.containers[0].image=$Image
            $expected.properties.template|Add-Member -NotePropertyName revisionSuffix -NotePropertyValue $suffix -Force
            $writable=$expected.properties.template|ConvertTo-Json -Depth 100|ConvertFrom-Json -Depth 100
            foreach($container in @($writable.containers)+@($writable.initContainers)){
                if($null -eq $container){continue}
                $container.PSObject.Properties.Remove('imageType')
                if($container.resources){$container.resources.PSObject.Properties.Remove('ephemeralStorage')}
            }
            $null=New-Item -ItemType Directory -Path $directory -Force
            $patch=Join-Path $directory "$($target.role).json"
            @{properties=@{template=$writable}}|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $patch -Encoding utf8
            $attempted.Add($target.name)
            $args=@('rest','--method','patch','--url',"https://management.azure.com$($target.app.id)?api-version=2025-07-01",
                '--body',"@$patch",'--subscription',$contract.subscriptionId,'--output','none','--only-show-errors')
            if($current.etag){$args+=@('--headers',"If-Match=$($current.etag)")}
            az @args
            if($LASTEXITCODE){throw "Image update failed for $($target.name)."}
            Remove-Item -LiteralPath $patch
        }
        $ready=$false
        for($index=0;$index -lt $RolloutAttempts;$index++){
            $observed=Read-Azure @('containerapp','show','--resource-group',$contract.resourceGroup,'--name',$target.name)
            if($observed.properties.provisioningState -in @('Failed','Canceled','Cancelled')){throw "Failed service rollout: $($target.role)."}
            if($observed.properties.provisioningState -eq 'Succeeded' -and $observed.properties.latestRevisionName -ceq $revision -and
                $observed.properties.latestReadyRevisionName -ceq $revision){
                if((Get-ServiceDevFingerprint (Get-ServiceDevPreservedState $observed)) -cne
                    (Get-ServiceDevFingerprint (Get-ServiceDevPreservedState $expected))){throw 'Preserved service configuration changed during rollout.'}
                $detail=Read-Azure @('containerapp','revision','show','--resource-group',$contract.resourceGroup,'--name',$target.name,'--revision',$revision)
                Assert-Revision $detail $revision $Image
                $ready=$true;break
            }
            if($index+1 -lt $RolloutAttempts){Start-Sleep -Seconds $RolloutDelaySeconds}
        }
        if(-not $ready){throw "Exact service revision readiness timed out: $($target.role)."}
        $verified.Add($target.name)
        $target.releasedRevision=$revision
        $target.releasedFingerprint=Get-ServiceDevFingerprint (Get-ServiceDevPreservedState $expected)
        Write-Output "Verified: role=$($target.role); app=$($target.name); image=$Image; readyRevision=$revision"
    }
    foreach($target in $targets){
        $current=Read-Azure @('containerapp','show','--resource-group',$contract.resourceGroup,'--name',$target.name)
        if((Get-ServiceDevFingerprint (Get-ServiceDevPreservedState $current)) -cne $target.releasedFingerprint -or
            $current.properties.latestRevisionName -cne $target.releasedRevision -or
            $current.properties.latestReadyRevisionName -cne $target.releasedRevision){throw 'A verified service target changed before final acceptance.'}
    }
    & (Join-Path $PSScriptRoot 'Test-DeployedApp.ps1') -PublicOrigin $contract.publicOrigin -TenantId $contract.tenantId `
        -WebClientId $contract.webClientId -ExpectAdmission:$contract.liveConfig.admissionEnabled
    Write-Output 'Five exact isolated service revisions and public smoke passed. Unattended synchronization and user journeys require separate evidence.'
}catch{
    throw "Service release failed; partial rollout is possible. Attempted=[$($attempted-join ',')]; verified=[$($verified-join ',')]. $($_.Exception.Message) No automatic rollback or message replay was attempted."
}finally{
    if(Test-Path -LiteralPath $directory){Remove-Item -LiteralPath $directory -Recurse -Force}
}
