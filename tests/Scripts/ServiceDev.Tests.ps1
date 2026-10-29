$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $root 'scripts\ServiceDevContract.psm1') -Force
$temporary=Join-Path ([IO.Path]::GetTempPath()) ('teams-service-dev-'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $temporary
function Clone($Value){$Value|ConvertTo-Json -Depth 100|ConvertFrom-Json -Depth 100}
function Reject([scriptblock]$Action,[string]$Expected){
    $failed=$false
    try{& $Action}catch{if($_.Exception.Message -notlike "*$Expected*"){throw};$failed=$true}
    if(-not $failed){throw "Expected refusal: $Expected"}
}
$subscription='11111111-1111-1111-1111-111111111111'
$tenant='22222222-2222-2222-2222-222222222222'
$scope="/subscriptions/$subscription/resourceGroups/test-service"
$origin='https://example.invalid'
$registry='approvedregistry.azurecr.io'
$old="$registry/teams-reference@sha256:$('a'*64)"
$image="$registry/teams-reference@sha256:$('b'*64)"
$bindings=@{};$clients=@{};$index=0
foreach($role in @(Get-ServiceDevRoles)){
    $index++
    $client="10000000-0000-0000-0000-$('{0:d12}' -f $index)"
    $bindings[$role]=@{appName="existing-$($role.ToLowerInvariant())";containerName=$role.ToLowerInvariant();
        identityResourceId="$scope/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$($role.ToLowerInvariant())";
        clientId=$client;principalId="20000000-0000-0000-0000-$('{0:d12}' -f $index)";minReplicas=1;maxReplicas=1}
    $clients[$role]=$client
}
$users=@('66666666-6666-6666-6666-666666666666','77777777-7777-7777-7777-777777777777','88888888-8888-8888-8888-888888888888')
$resources=@(@{id='group';kind='chat';graphChatId='group'},@{id='one';kind='chat';graphChatId='one'},
    @{id='two';kind='chat';graphChatId='two'},@{id='three';kind='chat';graphChatId='three'},
    @{id='channel';kind='channel';graphTeamId='team';graphChannelId='channel'})
$histories=@{};foreach($resource in $resources){$histories[$resource.id]=[DateTime]::UtcNow.AddDays(-1).ToString('o')}
$contract=Clone @{
    schemaVersion=1;dedicatedTestTenant=$true;tenantId=$tenant;subscriptionId=$subscription;resourceGroup='test-service';
    webClientId='33333333-3333-3333-3333-333333333333';syncClientId='44444444-4444-4444-4444-444444444444';
    webServicePrincipalId='55555555-5555-5555-5555-555555555555';publicOrigin=$origin;
    environmentId="$scope/providers/Microsoft.App/managedEnvironments/existing";roleBindings=$bindings;
    cosmosAccountName='existing-cosmos';primaryReaderRoleId="$scope/providers/Microsoft.DocumentDB/databaseAccounts/existing-cosmos/sqlRoleDefinitions/custom-reader";
    serviceBusName='existing';
    liveConfig=@{authorityMode='Policy';entitlementProvider='Graph';readConsistency='Session';approvalReference='reviewed';
        syncClientId='44444444-4444-4444-4444-444444444444';controlIdentityClientId=$clients.Control;writerEpoch=1;
        roleIdentityClientIds=$clients;synchronizationEnabled=$true;admissionEnabled=$true;
        graphPolicy=@{approvalReference='reviewed-policy';webServicePrincipalId='55555555-5555-5555-5555-555555555555';
            admittedUserIds=$users;historyFrom=$histories;appRoleIds=@([guid]::Empty.ToString());sendEnabledResources=@($resources.id)};
        resources=$resources;subscriptionScopes=@($resources|ForEach-Object{@{id=$_.id;path=if($_.kind -eq 'chat'){"chats/$($_.graphChatId)/messages"}else{'teams/team/channels/channel/messages'}}});
        directChats=@(@{resourceId='one';firstActorId=$users[0];secondActorId=$users[1]},@{resourceId='two';firstActorId=$users[0];secondActorId=$users[2]},
            @{resourceId='three';firstActorId=$users[1];secondActorId=$users[2]})}
}
function New-App([string]$Role){
    $binding=$contract.roleBindings.$Role
    $live=Clone $contract.liveConfig
    $live|Add-Member -NotePropertyName hostedRoles -NotePropertyValue @($Role)
    $settings=@{'App__GraphMode'='Live';'App__Auth'='Entra';'App__Store'='Cosmos';'App__Role'=$Role;
        'App__TenantId'=$tenant;'AzureAd__TenantId'=$tenant;'AzureAd__ClientId'=$contract.webClientId;
        'AzureClientId'=$binding.clientId;'AZURE_CLIENT_ID'=$binding.clientId;'App__PublicOrigin'=$origin;
        'Azure__CosmosEndpoint'='https://existing-cosmos.documents.azure.com/';'Azure__Redis'='existing.redis:10000';
        'Azure__ServiceBus'='existing.servicebus.windows.net';'Azure__PubSub'='https://existing.webpubsub.azure.com';
        'Azure__ProtectionKey'='https://existing.vault.azure.net/keys/session';'Callbacks__ClientId'='99999999-9999-9999-9999-999999999999';
        'Callbacks__Audience'='api://99999999-9999-9999-9999-999999999999';'Callbacks__PrincipalId'='90000000-0000-0000-0000-000000000001';
        'App__TrustedProxyNetworks__0'='10.0.0.0/24';'App__RealtimeAdmissionReady'='true';
        'AzureAd__ClientCredentials__0__SourceType'='SignedAssertionFromManagedIdentity';
        'AzureAd__ClientCredentials__0__ManagedIdentityClientId'=$binding.clientId;
        'Live__Configuration'=($live|ConvertTo-Json -Depth 100 -Compress)}
    return Clone @{
        id="$scope/providers/Microsoft.App/containerApps/$($binding.appName)";location='swedencentral';tags=@{environment='Development'};
        identity=@{type='UserAssigned';userAssignedIdentities=@{$binding.identityResourceId=@{clientId=$binding.clientId;principalId=$binding.principalId}}};
        properties=@{managedEnvironmentId=$contract.environmentId;provisioningState='Succeeded';
            latestRevisionName="$($binding.appName)--old";latestReadyRevisionName="$($binding.appName)--old";
            configuration=@{activeRevisionsMode='Single';secrets=@();registries=@(@{server=$registry;identity=$binding.identityResourceId});
                ingress=if($Role -in @('Api','Receiver')){@{external=$true;targetPort=8080;allowInsecure=$false}}else{$null}};
            template=@{containers=@(@{name=$binding.containerName;image=$old;imageType='ContainerImage';
                resources=@{cpu=0.5;memory='1Gi';ephemeralStorage='2Gi'};env=@($settings.Keys|ForEach-Object{@{name=$_;value=$settings[$_]}})});
                scale=@{minReplicas=1;maxReplicas=1}}}
    }
}
$cosmosId="$scope/providers/Microsoft.DocumentDB/databaseAccounts/existing-cosmos"
$reader=Clone @{id=$contract.primaryReaderRoleId;properties=@{permissions=@(@{dataActions=@(
    'Microsoft.DocumentDB/databaseAccounts/readMetadata',
    'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read',
    'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/executeQuery',
    'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/readChangeFeed',
    'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/executeStoredProcedure')})}}
$matrix=@{Api=@{'live-state'=$true;'live-authority'=$false};Receiver=@{'live-control'=$false};Projector=@{'live-state'=$true;'live-control'=$true};
    Delivery=@{'live-state'=$true;'live-authority'=$false;'live-leases'=$true};Control=@{'live-state'=$true;'live-control'=$true;'live-authority'=$true}}
$data=@()
foreach($role in @(Get-ServiceDevRoles)){foreach($container in $matrix[$role].Keys){
    $data+=Clone @{properties=@{principalId=$contract.roleBindings.$role.principalId;scope="$cosmosId/dbs/teams/colls/$container";
        roleDefinitionId=if($matrix[$role][$container]){"$cosmosId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"}else{$contract.primaryReaderRoleId}}}
}}
$refresh=Clone @{name='graph-read-refresh';id="$scope/providers/Microsoft.ServiceBus/namespaces/existing/queues/graph-read-refresh";
    status='Active';requiresDuplicateDetection=$true;deadLetteringOnMessageExpiration=$true;maxDeliveryCount=5;
    defaultMessageTimeToLive='PT5M';lockDuration='PT1M';duplicateDetectionHistoryTimeWindow='PT1M'}
$refreshGrants=@(
    (Clone @{principalId=$contract.roleBindings.Api.principalId;scope=$refresh.id;roleDefinitionId="$scope/providers/Microsoft.Authorization/roleDefinitions/69a216fc-b8fb-44d8-bc22-1f3c2cd27a39"})
    (Clone @{principalId=$contract.roleBindings.Control.principalId;scope=$refresh.id;roleDefinitionId="$scope/providers/Microsoft.Authorization/roleDefinitions/4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0"})
)
$state=@{apps=@{};writes=0;writeOrder=@();reads=@{};failRole='';driftRole=''}
foreach($role in @(Get-ServiceDevRoles)){$state.apps[$role]=New-App $role}
function az {
    $global:LASTEXITCODE=0
    if($args[0] -eq 'servicebus'){return $refresh|ConvertTo-Json -Depth 100}
    if($args[0] -eq 'role'){
        if($args -contains '--all' -or $args -notcontains '--include-inherited' -or
            $args[[Array]::IndexOf($args,'--scope')+1] -cne $refresh.id){
            throw 'Queue access must use its exact scope and inherited grants, without the incompatible subscription-wide --all option.'
        }
        return ConvertTo-Json -InputObject @($refreshGrants) -Depth 100
    }
    if($args[0] -eq 'account'){return @{id=$subscription;tenantId=$tenant}|ConvertTo-Json}
    if($args[0] -eq 'acr' -and $args[1] -eq 'show'){return @{loginServer=$registry}|ConvertTo-Json}
    if($args[0] -eq 'acr'){return @{digest=$image.Split('@')[1]}|ConvertTo-Json}
    if($args[0] -eq 'cosmosdb'){return @{id=$cosmosId;publicNetworkAccess='Disabled';disableLocalAuth=$true;
        consistencyPolicy=@{defaultConsistencyLevel='Session'};locations=@(@{name='swedencentral'});enableMultipleWriteLocations=$false}|ConvertTo-Json}
    if($args[0] -eq 'rest' -and $args[2] -eq 'get'){
        $uri=$args[[Array]::IndexOf($args,'--url')+1]
        if($uri -like '*sqlRoleDefinitions/*'){return $reader|ConvertTo-Json -Depth 100}
        return @{value=$data}|ConvertTo-Json -Depth 100
    }
    if($args[0] -eq 'rest'){
        if(@($state.reads.Keys).Count -ne 5){throw 'A write occurred before five-target preflight.'}
        $uri=$args[[Array]::IndexOf($args,'--url')+1]
        $target=($uri.Split('?')[0]).Split('/')[-1]
        $role=@(Get-ServiceDevRoles|Where-Object{$contract.roleBindings.$_.appName -eq $target})[0]
        if($role -eq $state.failRole){$global:LASTEXITCODE=1;return}
        $patchPath=$args[[Array]::IndexOf($args,'--body')+1].Substring(1)
        $patch=Get-Content -LiteralPath $patchPath -Raw|ConvertFrom-Json -Depth 100
        $template=$patch.properties.template
        if($template.containers[0].PSObject.Properties['imageType'] -or $template.containers[0].resources.PSObject.Properties['ephemeralStorage']){
            throw 'Read-only Azure fields were submitted.'
        }
        $template.containers[0]|Add-Member -NotePropertyName imageType -NotePropertyValue 'ContainerImage'
        $template.containers[0].resources|Add-Member -NotePropertyName ephemeralStorage -NotePropertyValue '2Gi'
        $state.apps[$role].properties.template=$template
        $state.apps[$role].properties.latestRevisionName="$target--$($template.revisionSuffix)"
        $state.apps[$role].properties.latestReadyRevisionName=$state.apps[$role].properties.latestRevisionName
        $state.writeOrder+=@($role)
        $state.writes++;return
    }
    $target=$args[[Array]::IndexOf($args,'--name')+1]
    $role=@(Get-ServiceDevRoles|Where-Object{$contract.roleBindings.$_.appName -eq $target})[0]
    if(-not $role){throw 'Unknown role target.'}
    if($args[1] -eq 'show'){
        $state.reads[$role]=1
        return $state.apps[$role]|ConvertTo-Json -Depth 100
    }
    return @{name=$state.apps[$role].properties.latestReadyRevisionName;properties=@{active=$true;provisioningState='Provisioned';healthState='Healthy';
        runningState='Running';template=$state.apps[$role].properties.template}}|ConvertTo-Json -Depth 100
}
function Invoke-WebRequest { throw 'Public smoke should not run after the intentional partial rollout failure.' }
try{
    Assert-ServiceDevTargets $contract
    $channelContract=Clone $contract
    $channelContract.liveConfig|Add-Member -NotePropertyName channelDiscovery -NotePropertyValue @{
        registryEnabled=$true;enabled=$true;sendEnabled=$false;approvalReference='PENDING';historyFrom=[DateTimeOffset]::UtcNow.AddDays(-1).ToString('o')
    }
    Reject {Assert-ServiceDevTargets $channelContract} 'separate reviewed approval'
    $channelContract.liveConfig.channelDiscovery.approvalReference='reviewed-expanded-channel-scope'
    Assert-ServiceDevTargets $channelContract
    $culture=[Globalization.CultureInfo]::CurrentCulture
    $historyFrom=$channelContract.liveConfig.channelDiscovery.historyFrom
    try{
        [Globalization.CultureInfo]::CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo('en-NL')
        $iso='2001-12-22T09:52:45.1807249+02:00'
        foreach($value in @($iso,[DateTime]::Parse($iso,[Globalization.CultureInfo]::InvariantCulture),
            [DateTimeOffset]::Parse($iso,[Globalization.CultureInfo]::InvariantCulture))){
            $channelContract.liveConfig.channelDiscovery.historyFrom=$value
            Assert-ServiceDevTargets $channelContract
            Assert-ServiceDevTargets (Clone $channelContract)
        }
        foreach($invalid in @('invalid',[DateTimeOffset]::MinValue,[DateTime]::UtcNow.AddDays(1),[DateTimeOffset]::UtcNow.AddDays(1))){
            $channelContract.liveConfig.channelDiscovery.historyFrom=$invalid
            Reject {Assert-ServiceDevTargets $channelContract} 'separate reviewed approval'
        }
    }finally{
        [Globalization.CultureInfo]::CurrentCulture=$culture
        $channelContract.liveConfig.channelDiscovery.historyFrom=$historyFrom
    }
    Reject {Assert-ServiceDevDataAccess $data $cosmosId $contract.primaryReaderRoleId $channelContract} 'missing or excess'
    $channelData=@($data)
    foreach($role in @(Get-ServiceDevRoles)){
        $channelData+=Clone @{properties=@{principalId=$contract.roleBindings.$role.principalId;scope="$cosmosId/dbs/teams/colls/live-resources";
            roleDefinitionId=if($role -eq 'Control'){"$cosmosId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"}else{$contract.primaryReaderRoleId}}}
    }
    Assert-ServiceDevDataAccess $channelData $cosmosId $contract.primaryReaderRoleId $channelContract
    $wrongData=Clone $channelData
    ($wrongData|Where-Object{$_.properties.principalId -eq $contract.roleBindings.Receiver.principalId -and
        $_.properties.scope -like '*/live-resources'}).properties.roleDefinitionId="$cosmosId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
    Reject {Assert-ServiceDevDataAccess $wrongData $cosmosId $contract.primaryReaderRoleId $channelContract} 'least-privileged'
    $channelQueue=Clone $refresh
    $channelQueue.name='channel-work';$channelQueue.id=$refresh.id.Replace('graph-read-refresh','channel-work');$channelQueue.defaultMessageTimeToLive='PT1H'
    $channelGrants=@($refreshGrants|ForEach-Object{$grant=Clone $_;$grant.scope=$channelQueue.id;$grant})
    $channelGrants+=Clone @{principalId=$contract.roleBindings.Control.principalId;scope=$channelQueue.id;
        roleDefinitionId="$scope/providers/Microsoft.Authorization/roleDefinitions/69a216fc-b8fb-44d8-bc22-1f3c2cd27a39"}
    Assert-ServiceDevChannelWork $channelQueue $channelGrants $channelContract
    Reject {Assert-ServiceDevChannelWork $channelQueue @($channelGrants|Select-Object -First 2) $channelContract} 'Unexpected Control'
    $badGrant=Clone $channelGrants
    $badGrant[0].scope=$channelQueue.id.Substring(0,$channelQueue.id.IndexOf('/queues/'))
    Reject {Assert-ServiceDevChannelWork $channelQueue $badGrant $channelContract} 'queue-scoped'
    foreach($legacy in @('Strong','SessionPrimary','')){
        $bad=Clone $contract;$bad.liveConfig.readConsistency=$legacy
        Reject {Assert-ServiceDevTargets $bad} 'approved normal'
    }
    $planningLive=Clone $contract.liveConfig
    $planningLive.authorityMode='DelegatedSynthetic'
    $planningLive|Add-Member -NotePropertyName syntheticActorIds -NotePropertyValue $users
    $planningLive|Add-Member -NotePropertyName syntheticHistoryFrom -NotePropertyValue $histories
    $planningBase='teamsrefdev-abcdefghijklm'
    $planningApps=@()
    foreach($role in @('Api','Projector')){
        $binding=$contract.roleBindings.$role
        $planningApps+=@{
            name=if($role -eq 'Api'){"$planningBase-api"}else{"$planningBase-worker"}
            identity=@{userAssignedIdentities=@{$binding.identityResourceId=@{clientId=$binding.clientId;principalId=$binding.principalId}}}
            runtimeClientId=$binding.clientId;webClientId=$contract.webClientId
            environmentId=$contract.environmentId;publicOrigin=$origin;live=$planningLive
        }
    }
    $planningInventory=@{
        schemaVersion=1;tenantId=$tenant;subscriptionId=$subscription;resourceGroup='test-service'
        ownedTag='teams-reference:teams-reference-dev';apps=$planningApps
        applications=@(
            @{application=@{appId=$contract.webClientId};servicePrincipal=@{id=$contract.webServicePrincipalId}}
            @{application=@{appId=$contract.syncClientId;displayName='teams-reference-dev-sync'};servicePrincipal=@{id='40000000-0000-0000-0000-000000000001'}}
        )
        platform=@(@{id=$cosmosId;name='existing-cosmos';type='Microsoft.DocumentDB/databaseAccounts'},
            @{name='existing';type='Microsoft.ServiceBus/namespaces'})
        cosmosRoleDefinitions=@(@{id=$contract.primaryReaderRoleId;properties=@{roleName='synthetic-live-primary-reader-v1'}})
        cosmosRoleAssignments=$data;roleAssignments=@()
    }
    $planningPath=Join-Path $temporary 'inventory.json'
    $planningDirectory=Join-Path $temporary 'approval'
    $planningInventory|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $planningPath
    & (Join-Path $root 'scripts\New-ServiceDevPlan.ps1') -InventoryPath $planningPath -OutputDirectory $planningDirectory
    $legacyInventory=Clone $planningInventory
    $legacyInventory.apps[0].live.readConsistency='SessionPrimary'
    $legacyPath=Join-Path $temporary 'legacy-inventory.json'
    $legacyInventory|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $legacyPath
    Reject {& (Join-Path $root 'scripts\New-ServiceDevPlan.ps1') -InventoryPath $legacyPath -OutputDirectory $planningDirectory} 'not normalized'
    $planned=Get-Content -LiteralPath (Join-Path $planningDirectory 'service-targets.draft.json') -Raw|ConvertFrom-Json -Depth 100
    if($planned.serviceBusName -cne 'existing'){throw 'The planner did not pin the inventoried Service Bus namespace.'}
    $multipleBuses=Clone $planningInventory
    $multipleBuses.platform+=@(@{name='new-standard';type='Microsoft.ServiceBus/namespaces'})
    $multipleBusesPath=Join-Path $temporary 'multiple-buses.json'
    $multipleBuses|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $multipleBusesPath
    Reject {& (Join-Path $root 'scripts\New-ServiceDevPlan.ps1') -InventoryPath $multipleBusesPath -OutputDirectory $planningDirectory} 'one reviewed Service Bus'
    & (Join-Path $root 'scripts\New-ServiceDevPlan.ps1') -InventoryPath $multipleBusesPath -OutputDirectory $planningDirectory -ServiceBusName 'new-standard'
    $selected=Get-Content -LiteralPath (Join-Path $planningDirectory 'service-targets.draft.json') -Raw|ConvertFrom-Json -Depth 100
    if($selected.serviceBusName -cne 'new-standard'){throw 'The planner did not preserve the explicit existing namespace selection.'}
    foreach($role in @('Receiver','Delivery','Control')){
        if($planned.roleBindings.$role.appName.Length -gt 32 -or
            -not $planned.roleBindings.$role.identityResourceId.EndsWith("-$($role.ToLowerInvariant())")){
            throw 'The planner must produce valid short workload names without changing the reviewed identity names.'
        }
        if($planned.roleBindings.$role.workloadProfileName -ne 'Consumption' -or
            $planned.roleBindings.$role.cooldownPeriod -ne 300 -or $planned.roleBindings.$role.pollingInterval -ne 30){
            throw 'The planner must carry explicit reviewed workload-profile and scale-timing defaults.'
        }
    }
    if($planned.roleBindings.Api.appName -ne "$planningBase-api" -or $planned.roleBindings.Projector.appName -ne "$planningBase-worker"){
        throw 'Planning must preserve the two existing workload names.'
    }
    foreach($role in @(Get-ServiceDevRoles)){Assert-ServiceDevApp $state.apps[$role] $role $contract $registry}
    Assert-ServiceDevDataAccess $data $cosmosId $contract.primaryReaderRoleId $contract
    Assert-ServiceDevReaderRole $reader $cosmosId
    Assert-ServiceDevReadRefresh $refresh $refreshGrants $contract
    $bad=Clone $refresh;$bad.maxDeliveryCount=10
    Reject {Assert-ServiceDevReadRefresh $bad $refreshGrants $contract} 'Provision the reviewed'
    foreach($field in @('lockDuration','duplicateDetectionHistoryTimeWindow')){
        $bad=Clone $refresh;$bad.$field='PT30S'
        Reject {Assert-ServiceDevReadRefresh $bad $refreshGrants $contract} 'Provision the reviewed'
    }
    $bad=Clone $refresh;$bad.status='SendDisabled'
    Reject {Assert-ServiceDevReadRefresh $bad $refreshGrants $contract} 'Provision the reviewed'
    Reject {Assert-ServiceDevReadRefresh $refresh @($refreshGrants[0]) $contract} 'exact queue-scoped'
    $bad=Clone $refreshGrants;$bad[0].scope=$scope
    Reject {Assert-ServiceDevReadRefresh $refresh $bad $contract} 'exact queue-scoped'
    $bad=Clone $refreshGrants;$bad[1].roleDefinitionId=$bad[0].roleDefinitionId
    Reject {Assert-ServiceDevReadRefresh $refresh $bad $contract} 'exact queue-scoped'
    $bad=Clone $reader;$bad.properties.permissions[0].dataActions+=@('Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/*')
    Reject {Assert-ServiceDevReaderRole $bad $cosmosId} 'read-only actions'
    $bad=Clone $contract;$bad.liveConfig.graphPolicy.approvalReference='PENDING-APPROVAL'
    Reject {Assert-ServiceDevTargets $bad} 'drafts'
    $bad=Clone $contract;$bad.roleBindings.Api.clientId=$bad.roleBindings.Control.clientId
    Reject {Assert-ServiceDevTargets $bad} 'distinct'
    $bad=Clone $contract;$bad.dedicatedTestTenant=$false
    Reject {Assert-ServiceDevTargets $bad} 'dedicated'
    $bad=Clone $contract;$bad.serviceBusName=''
    Reject {Assert-ServiceDevTargets $bad} 'Service Bus namespace'
    $bad=Clone $state.apps.Api
    ($bad.properties.template.containers[0].env|Where-Object name -eq 'Azure__ServiceBus').value='different.servicebus.windows.net'
    Reject {Assert-ServiceDevApp $bad 'Api' $contract $registry} 'reviewed service contract'
    $bad=Clone $state.apps.Api
    $bad.identity.userAssignedIdentities|Add-Member -NotePropertyName $contract.roleBindings.Control.identityResourceId -NotePropertyValue @{clientId=$clients.Control}
    Reject {Assert-ServiceDevApp $bad 'Api' $contract $registry} 'only its own'
    $bad=Clone $state.apps.Api
    ($bad.properties.template.containers[0].env|Where-Object name -eq 'AzureClientId').value=$clients.Control
    Reject {Assert-ServiceDevApp $bad 'Api' $contract $registry} 'reviewed service contract'
    $bad=Clone $state.apps.Api
    $env=($bad.properties.template.containers[0].env|Where-Object name -eq 'Live__Configuration')
    $value=$env.value|ConvertFrom-Json -Depth 100;$value.hostedRoles=@('Api','Control');$env.value=$value|ConvertTo-Json -Depth 100 -Compress
    Reject {Assert-ServiceDevApp $bad 'Api' $contract $registry} 'additional roles'
    $extra=Clone $data[0];$extra.properties.principalId=$contract.roleBindings.Api.principalId
    Reject {Assert-ServiceDevDataAccess ($data+@($extra)) $cosmosId $contract.primaryReaderRoleId $contract} 'excess'
    $path=Join-Path $temporary 'targets.json';$contract|ConvertTo-Json -Depth 100|Set-Content -LiteralPath $path
    & (Join-Path $root 'scripts\Deploy-ServiceDev.ps1') -TargetsPath $path -RegistryName 'approvedregistry' -Image $image
    if($state.writes){throw 'Read-only preflight mutated a workload.'}
    & (Join-Path $root 'scripts\Deploy-ServiceDev.ps1') -TargetsPath $path -RegistryName 'approvedregistry' -Image $image -Apply -WhatIf
    if($state.writes){throw 'WhatIf mutated a workload.'}
    $state.failRole='Projector'
    Reject {& (Join-Path $root 'scripts\Deploy-ServiceDev.ps1') -TargetsPath $path -RegistryName 'approvedregistry' -Image $image -Apply -Confirmation DEPLOY-DEV -Confirm:$false -RolloutAttempts 1 -RolloutDelaySeconds 0} 'partial rollout'
    if($state.writes -ne 2){throw 'The failed partial rollout continued or rolled back automatically.'}
    if(($state.writeOrder -join ',') -cne 'Control,Receiver'){throw 'Partial rollout did not prepare Control before the API.'}
    Import-Module (Join-Path $root 'scripts\DeploymentContract.psm1') -Force
    & (Get-Module DeploymentContract) {
        param($Tenant,$Client,$Origin)
        $script:serviceSmoke=@{tenant=$Tenant;client=$Client;origin=$Origin}
        function script:Get-AppDeploymentResponse($Client,[string]$Path){
            switch($Path){
                '/' { return [pscustomobject]@{status=200;body='<div id="root"></div><script src="/assets/app.js"></script>'} }
                '/assets/app.js' { return [pscustomobject]@{status=200;body='compiled shell'} }
                '/health/ready' { return [pscustomobject]@{status=200;body='{"status":"ready","mode":"live","role":"Api","admission":true}'} }
                '/api/session' { return [pscustomobject]@{status=401;body='{}'} }
                '/auth/signin' {
                    return [pscustomobject]@{status=302;body='';location=[uri]("https://login.microsoftonline.com/$($script:serviceSmoke.tenant)/oauth2/v2.0/authorize?client_id="+
                        $script:serviceSmoke.client+'&redirect_uri='+[uri]::EscapeDataString($script:serviceSmoke.origin+'/auth/callback'))}
                }
                default { throw 'Unexpected public smoke request.' }
            }
        }
    } $tenant $contract.webClientId $origin
    foreach($role in @(Get-ServiceDevRoles)){$state.apps[$role]=New-App $role}
    $state.writes=0;$state.writeOrder=@();$state.reads=@{};$state.failRole=''
    & (Join-Path $root 'scripts\Deploy-ServiceDev.ps1') -TargetsPath $path -RegistryName 'approvedregistry' -Image $image -Apply -Confirmation DEPLOY-DEV -Confirm:$false -RolloutAttempts 1 -RolloutDelaySeconds 0
    if($state.writes -ne 5){throw 'The complete image-only rollout did not update exactly five targets.'}
    if(($state.writeOrder -join ',') -cne 'Control,Receiver,Projector,Delivery,Api'){throw 'The API must be released after all background roles.'}
    & (Join-Path $root 'scripts\Deploy-ServiceDev.ps1') -TargetsPath $path -RegistryName 'approvedregistry' -Image $image -Apply -Confirmation DEPLOY-DEV -Confirm:$false -RolloutAttempts 1 -RolloutDelaySeconds 0
    if($state.writes -ne 5){throw 'Reapplying the same image was not idempotent.'}
    Write-Output 'Passed five-role target/access guards, no-write plan/WhatIf, all-target preflight, partial-failure preservation, exact full rollout and idempotent reapply; no cloud calls.'
}finally{Remove-Item -LiteralPath $temporary -Recurse -Force}
