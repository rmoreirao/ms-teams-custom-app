$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temporary=Join-Path ([IO.Path]::GetTempPath()) ("teams-pilot-deploy-"+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$path=Join-Path $temporary 'pilot.parameters.json'
$subscription='11111111-1111-1111-1111-111111111111'
$client='22222222-2222-2222-2222-222222222222'
$state=@{reads=0;writes=0;role='Api';patched=$false;body=$null;healthCalls=0}
$settings=@{parameters=@{
    fixtureActorIds=@{value=@(
        '33333333-3333-3333-3333-333333333333',
        '44444444-4444-4444-4444-444444444444',
        '55555555-5555-5555-5555-555555555555'
    )};webClientId=@{value=$client}
    graphPilotConfig=@{value=@{notBefore='2026-10-19T00:00:00Z';resources=@(@{id='graph-pilot-chat'})}}
}}
function az {
    $global:LASTEXITCODE=0
    if ($args[0] -eq 'containerapp' -and $args[1] -eq 'show') {
        $state.reads++
        $currentImage=if($state.patched){$state.body.properties.template.containers[0].image}else{'old'}
        return @{
            id="/subscriptions/$subscription/resourceGroups/rg/providers/Microsoft.App/containerApps/api"
            properties=@{provisioningState='Succeeded';configuration=@{activeRevisionsMode='Single'};latestRevisionName='new';latestReadyRevisionName='old';template=@{
                containers=@(@{name='api';image=$currentImage;imageType='ContainerImage';resources=@{cpu=0.25;memory='0.5Gi'};env=@(
                    @{name='App__Role';value=$state.role};@{name='AzureAd__ClientId';value=$client}
                    @{name='App__PublicOrigin';value='https://example.test'};@{name='App__TenantId';value=$subscription}
                    @{name='ExistingSecret';secretRef='unchanged'}
                )});scale=@{minReplicas=1;maxReplicas=1;cooldownPeriod=300;pollingInterval=30}
            }}
        } | ConvertTo-Json -Depth 15
    }
    if ($args[0] -eq 'rest' -and $args -contains 'patch') {
        $state.writes++
        $state.patched=$true
        $bodyPath=$args[[array]::IndexOf($args,'--body')+1].Substring(1)
        $state.body=Get-Content -Raw -LiteralPath $bodyPath | ConvertFrom-Json
        return
    }
    $state.writes++
    throw 'A plan-only targeted deployment attempted an unexpected command.'
}
function Start-Sleep {}
function Invoke-RestMethod { $state.healthCalls++;throw 'An old revision must not be accepted through public readiness.' }
try {
    $settings | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path
    $arguments=@{SubscriptionId=$subscription;ResourceGroup='rg';AppName='api';ParametersPath=$path;Image=('registry.azurecr.io/teams-reference@sha256:'+('a'*64))}
    $command=Join-Path $root 'scripts\Deploy-GraphPilot.ps1'
    & $command @arguments
    & $command @arguments -Apply -WhatIf
    if ($state.writes -or (Get-ChildItem -LiteralPath $temporary -File).Count -ne 1) {
        throw 'Plan/WhatIf mutated a resource or created a private patch/snapshot.'
    }
    $state.role='Worker'
    $denied=$false
    try { & $command @arguments } catch { if($_.Exception.Message -notlike '*API role/application*'){throw};$denied=$true }
    if(-not $denied){throw 'A worker was accepted as the targeted API.'}
    $state.role='Api'
    $denied=$false
    try { & $command @arguments -Apply -Confirm:$false } catch {
        if($_.Exception.Message -notlike '*readiness did not pass*'){throw};$denied=$true
    }
    if(-not $denied -or $state.writes -ne 1 -or $state.healthCalls){throw 'An old/mismatched revision passed rollout verification.'}
    $template=$state.body.properties.template
    $container=$template.containers[0]
    if (@($state.body.PSObject.Properties.Name) -ne 'properties' -or
        @($state.body.properties.PSObject.Properties.Name) -ne 'template' -or
        $container.image -ne $arguments.Image -or $container.resources.cpu -ne 0.25 -or $container.resources.memory -ne '0.5Gi' -or
        $container.PSObject.Properties['imageType'] -or $template.scale.PSObject.Properties['cooldownPeriod'] -or
        $template.scale.PSObject.Properties['pollingInterval'] -or $template.scale.minReplicas -ne 1 -or $template.scale.maxReplicas -ne 1 -or
        ($container.env | Where-Object name -eq 'ExistingSecret').secretRef -ne 'unchanged' -or
        ($container.env | Where-Object name -eq 'App__GraphMode').value -ne 'GraphPilot') {
        throw 'The API-only patch changed unrelated settings or retained unsupported ARM properties.'
    }
    Write-Output 'Passed targeted GraphPilot deployment guards: no-write plan/WhatIf, API-only patch, preserved sizing/secrets, ARM normalization and stale-revision rejection.'
} finally {
    Remove-Item -LiteralPath $path
    foreach($name in @('api-template.before-update.json','api-template.update.json')){
        $artifact=Join-Path $temporary $name
        if(Test-Path -LiteralPath $artifact){Remove-Item -LiteralPath $artifact}
    }
    Remove-Item -LiteralPath $temporary
}
