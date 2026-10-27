[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InventoryPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidatePattern('^$|^[a-z][a-z0-9-]{4,48}[a-z0-9]$')][string]$ServiceBusName = ''
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))+[IO.Path]::DirectorySeparatorChar
$inputFile=[IO.Path]::GetFullPath($InventoryPath)
$directory=[IO.Path]::GetFullPath($OutputDirectory)
if($inputFile.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or
    ($directory+[IO.Path]::DirectorySeparatorChar).StartsWith($root,[StringComparison]::OrdinalIgnoreCase)){
    throw 'Resolved identity, resource and recovery inventories must remain outside the repository.'
}
$inventory=Get-Content -LiteralPath $inputFile -Raw|ConvertFrom-Json -Depth 100
if($inventory.schemaVersion -ne 1 -or $inventory.apps.Count -ne 2 -or -not $inventory.ownedTag){
    throw 'Supply the bounded read-only inventory of the existing two-app Development deployment.'
}
$web=@($inventory.applications|Where-Object{$_.application.appId -eq $inventory.apps[0].webClientId})
$sync=@($inventory.applications|Where-Object{$_.application.displayName -eq ($inventory.ownedTag.Substring('teams-reference:'.Length)+'-sync')})
if($web.Count -ne 1 -or $sync.Count -ne 1 -or $web[0].application.appId -eq $sync[0].application.appId){
    throw 'Owned, distinct Web and sync registrations must already be identified.'
}
$live=($inventory.apps[0].live|ConvertTo-Json -Depth 100)|ConvertFrom-Json -Depth 100
if($live.readConsistency -cne 'Session'){
    throw 'Explicitly migrate the reviewed source configuration to Session before generating a new service plan; legacy profiles are not normalized.'
}
if($live.resources.Count -ne 5 -or $live.syntheticActorIds.Count -ne 3 -or $live.authorityMode -ne 'DelegatedSynthetic'){
    throw 'Migration planning is restricted to the reviewed three-user/five-resource profile.'
}
$roles=@('Api','Receiver','Projector','Delivery','Control')
$base=$inventory.apps[0].name.Substring(0,$inventory.apps[0].name.Length-4)
$appSuffix=@{Receiver='recv';Delivery='dlvr';Control='ctrl'}
$bindings=@{}
foreach($role in $roles){
    $existing=if($role -eq 'Api'){$inventory.apps[0]}elseif($role -eq 'Projector'){$inventory.apps[1]}else{$null}
    $identity=if($existing){@($existing.identity.userAssignedIdentities.PSObject.Properties|Where-Object{$_.Value.clientId -eq $existing.runtimeClientId})}else{@()}
    if($existing -and $identity.Count -ne 1){throw 'An existing runtime identity binding is ambiguous.'}
    $bindings[$role]=@{
        appName=if($existing){$existing.name}else{"$base-$($appSuffix[$role])"}
        containerName=if($role -eq 'Projector'){'worker'}else{$role.ToLowerInvariant()}
        identityResourceId=if($existing){$identity[0].Name}else{"/subscriptions/$($inventory.subscriptionId)/resourceGroups/$($inventory.resourceGroup)/providers/Microsoft.ManagedIdentity/userAssignedIdentities/$base-$($role.ToLowerInvariant())"}
        clientId=if($existing){$identity[0].Value.clientId}else{$null}
        principalId=if($existing){$identity[0].Value.principalId}else{$null}
        minReplicas=1;maxReplicas=1
        workloadProfileName=if($existing -and $existing.workloadProfileName){$existing.workloadProfileName}else{'Consumption'}
        cooldownPeriod=if($existing -and $null -ne $existing.scale.cooldownPeriod){$existing.scale.cooldownPeriod}else{300}
        pollingInterval=if($existing -and $null -ne $existing.scale.pollingInterval){$existing.scale.pollingInterval}else{30}
    }
    if($bindings[$role].appName -notmatch '^[a-z][a-z0-9-]{0,30}[a-z0-9]$'){
        throw "The proposed $role Container App name exceeds the supported 32-character naming boundary."
    }
}
$users=@($live.syntheticActorIds)
$histories=$live.syntheticHistoryFrom
foreach($name in @($live.PSObject.Properties.Name|Where-Object{$_ -like 'synthetic*' -or $_ -in @('hostedRoles','entitlementAuthority','entitlementScope')})){
    $live.PSObject.Properties.Remove($name)
}
$clients=@{}
foreach($role in $roles){$clients[$role]=$bindings[$role].clientId}
$live.authorityMode='Policy'
$live.syncClientId=$sync[0].application.appId
$live.controlIdentityClientId=$bindings.Control.clientId
$live.synchronizationEnabled=$false
$live.admissionEnabled=$false
$live|Add-Member entitlementProvider 'Graph' -Force
$live|Add-Member roleIdentityClientIds $clients -Force
$live|Add-Member graphPolicy @{
    approvalReference='PENDING-EXACT-IDENTITY-AND-POLICY-APPROVAL'
    webServicePrincipalId=$web[0].servicePrincipal.id
    admittedUserIds=$users
    appRoleIds=@([guid]::Empty.ToString())
    historyFrom=$histories
    sendEnabledResources=@($live.resources.id)
} -Force
foreach($scope in $live.subscriptionScopes){$scope.id='service-'+$scope.id}
$permissions=@('Application.Read.All','Channel.ReadBasic.All','ChannelMessage.Read.All','Chat.Read.All','Team.ReadBasic.All','TeamMember.Read.All','User.Read.All')
$cosmos=@($inventory.platform|Where-Object{$_.type -eq 'Microsoft.DocumentDB/databaseAccounts'})
$buses=@($inventory.platform|Where-Object{$_.type -eq 'Microsoft.ServiceBus/namespaces' -and
    (-not $ServiceBusName -or $_.name -ceq $ServiceBusName)})
if($buses.Count -ne 1){throw 'Inventory one reviewed Service Bus namespace, or select its exact ServiceBusName from the inventory.'}
$reader=@($inventory.cosmosRoleDefinitions|Where-Object{$_.properties.roleName -eq 'synthetic-live-primary-reader-v1'})
if($cosmos.Count -ne 1 -or $reader.Count -ne 1){throw 'Inventory the existing Cosmos account and primary-reader definition before preparing the access diff.'}
$matrix=@{Api=@{'live-state'=$true;'live-authority'=$false};Receiver=@{'live-control'=$false};Projector=@{'live-state'=$true;'live-control'=$true};
    Delivery=@{'live-state'=$true;'live-authority'=$false;'live-leases'=$true};Control=@{'live-state'=$true;'live-control'=$true;'live-authority'=$true}}
$desiredData=@();$preservedData=@{};$retiredData=@()
foreach($role in $roles){
    foreach($container in $matrix[$role].Keys){
        $scope="$($cosmos[0].id)/dbs/teams/colls/$container"
        $definition=if($matrix[$role][$container]){"$($cosmos[0].id)/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"}else{$reader[0].id}
        $existing=@($inventory.cosmosRoleAssignments|Where-Object{$bindings[$role].principalId -and
            $_.properties.principalId -eq $bindings[$role].principalId -and $_.properties.scope -eq $scope -and $_.properties.roleDefinitionId -eq $definition})
        if($existing.Count -gt 1){throw 'Duplicate effective Cosmos assignments require explicit reconciliation.'}
        if($existing.Count){$preservedData["${role}:$container"]=$existing[0].name}
        $desiredData+=@{role=$role;principalId=$bindings[$role].principalId;scope=$scope;roleDefinitionId=$definition;
            action=if($existing.Count){'Preserve'}else{'Create after identity resolution'}}
    }
}
foreach($assignment in $inventory.cosmosRoleAssignments){
    if($assignment.properties.principalId -notin @($bindings.Api.principalId,$bindings.Projector.principalId)){continue}
    $match=@($desiredData|Where-Object{$_.principalId -eq $assignment.properties.principalId -and $_.scope -eq $assignment.properties.scope -and
        $_.roleDefinitionId -eq $assignment.properties.roleDefinitionId})
    if(-not $match.Count){$retiredData+=@{id=$assignment.id;principalId=$assignment.properties.principalId;
        scope=$assignment.properties.scope;roleDefinitionId=$assignment.properties.roleDefinitionId;action='Remove only during approved fenced cutover'}}
}
$targets=@{schemaVersion=1;tenantId=$inventory.tenantId;subscriptionId=$inventory.subscriptionId;resourceGroup=$inventory.resourceGroup;
    environmentId=$inventory.apps[0].environmentId;publicOrigin=$inventory.apps[0].publicOrigin;
    webClientId=$web[0].application.appId;webServicePrincipalId=$web[0].servicePrincipal.id;syncClientId=$sync[0].application.appId;
    dedicatedTestTenant=$true;roleBindings=$bindings;liveConfig=$live;cosmosAccountName=$cosmos[0].name;primaryReaderRoleId=$reader[0].id;
    serviceBusName=$buses[0].name}
$approval=@{
    schemaVersion=1;status='APPROVAL-REQUIRED';tenantId=$inventory.tenantId;subscriptionId=$inventory.subscriptionId
    initialUsers=$users;initialResources=@($live.resources.id);publicOrigin=$inventory.apps[0].publicOrigin
    syncApplication=@{clientId=$sync[0].application.appId;principalId=$sync[0].servicePrincipal.id;
        currentApplicationGrants=$sync[0].applicationGrants;proposedApplicationPermissions=$permissions;
        currentCredentialMetadata=$sync[0].credentialMetadata;
        proposedApplicationRoleIds=$inventory.proposedGraphPermissionRoles;graphResourcePrincipalId=$inventory.graphResourcePrincipalId;
        federationSubject='The newly provisioned Control managed-identity principal; resolve and reverify before apply.'
        publisherAdmission='Assignment-required sync application; explicitly admit Microsoft Graph Change Tracking only.'}
    webApplication=@{clientId=$web[0].application.appId;applicationPermissions=@();assignedUsers=$users;
        delegatedScopes=@('openid','profile','offline_access','ChatMessage.Send','ChannelMessage.Send');
        legacyGrantRetirement='Only the three owned Principal grants may retire the recorded read/create validation scopes after explicit permission approval.'}
    identitiesToCreate=@($roles|Where-Object{$_ -notin @('Api','Projector')}|ForEach-Object{$bindings[$_].identityResourceId})
    workloadsToCreate=@($roles|Where-Object{$_ -notin @('Api','Projector')}|ForEach-Object{$bindings[$_].appName})
    workloadSizing=@{perRoleCpu=0.5;perRoleMemory='1Gi';minReplicas=1;maxReplicas=1;additionalRoles=3;
        additionalAllocatedCpu=1.5;additionalAllocatedMemory='3Gi';spendingCapChange=$false}
    requiredPrivilegeRetirement=@('Remove compact Api authority/control/validation-container write access and notification-queue send rights no longer required.',
        'Remove compact worker Delivery-only authority/lease/realtime rights after isolating Projector.',
        'Verify effective inherited roles and trust; adding narrow assignments does not remove existing broad assignments.')
    cosmosAccess=@{accountId=$cosmos[0].id;primaryReaderRoleId=$reader[0].id;desiredAssignments=$desiredData;
        preservedAssignmentNames=$preservedData;retireAssignments=$retiredData}
    currentWorkloadArmAssignments=@($inventory.roleAssignments|Where-Object{
        $_.properties.principalId -in @($bindings.Api.principalId,$bindings.Projector.principalId)})
    readRefresh=@{queue='graph-read-refresh';namespace='Existing approved Service Bus namespace';ttl='PT5M';maxDeliveryCount=5;
        duplicateDetectionWindow='PT1M';access=@(@{role='Api';permission='Data Sender';scope='queue'},
            @{role='Control';permission='Data Receiver';scope='queue'});
        approval='Provision the queue and these exact grants separately before the new image; no Graph permissions are added.'}
    effectiveArmAccessReview='Before apply, resolve all new principals and review direct, group-derived and inherited management/data access; do not assume a new narrow assignment removes an old broader role.'
    subscriptionMigration='Existing delegated-application subscriptions are not inherited. Preserve creation journals; use reviewed service-prefixed scope IDs with the sync application and preserve encrypted backlog keys.'
    blockingApprovals=@('Exact Graph consent and supported membership/policy boundary.',
        'Creation of three role identities and three workloads with reviewed cost impact.',
        'Exact effective RBAC removals/additions, sync federation and publisher admission.',
        'Graph callback edge/private-origin changes and controller cutover.',
        'Any real message writes; none are authorized by this plan generator.')
    guarantees=@('No cloud calls or mutations from this generator.','No source resource, data, identity or token deletion.',
        'No unattended test-account password/sign-in automation.','Draft runtime configuration deliberately fails validation until identities and approval are resolved.')
}
$null=New-Item -ItemType Directory -Path $directory -Force
$targets|ConvertTo-Json -Depth 100|Set-Content -LiteralPath (Join-Path $directory 'service-targets.draft.json') -Encoding utf8
$approval|ConvertTo-Json -Depth 100|Set-Content -LiteralPath (Join-Path $directory 'approval-required.json') -Encoding utf8
Write-Output "Prepared offline approval artifacts in $directory. No permissions, resources, configuration or Teams messages were changed."
