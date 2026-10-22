$ErrorActionPreference='Stop'
function Get-ServiceDevRoles { @('Api','Receiver','Projector','Delivery','Control') }
function Assert-ServiceDevTargets($Contract) {
    $roles=@(Get-ServiceDevRoles)
    if($Contract.schemaVersion -ne 1 -or $Contract.dedicatedTestTenant -ne $true){throw 'A reviewed dedicated-test-tenant service target contract is required.'}
    foreach($field in @('tenantId','subscriptionId','webClientId','webServicePrincipalId','syncClientId')){
        $id=[guid]::Empty
        if(-not [guid]::TryParseExact([string]$Contract.$field,'D',[ref]$id) -or $id -eq [guid]::Empty){throw "Resolve the service target identity: $field."}
    }
    if($Contract.webClientId -eq $Contract.syncClientId){throw 'Web and sync applications must be distinct.'}
    $origin=$null
    if(-not [uri]::TryCreate($Contract.publicOrigin,[UriKind]::Absolute,[ref]$origin) -or
        $origin.Scheme -ne 'https' -or $origin.Port -ne 443 -or $origin.IsLoopback -or $origin.AbsolutePath -ne '/' -or
        $origin.UserInfo -or $origin.Query -or $origin.Fragment){throw 'Use the unchanged canonical public HTTPS origin.'}
    if(@($Contract.roleBindings.PSObject.Properties.Name|Sort-Object) -join ',' -cne (@($roles|Sort-Object)-join ',')){
        throw 'Exactly five service role bindings are required.'
    }
    $names=@();$clients=@();$principals=@();$identities=@()
    foreach($role in $roles){
        $binding=$Contract.roleBindings.$role
        if($binding.appName -notmatch '^[a-z][a-z0-9-]{0,30}[a-z0-9]$' -or -not $binding.containerName){throw "Resolve the existing target for $role."}
        foreach($field in @('clientId','principalId')){
            $id=[guid]::Empty
            if(-not [guid]::TryParseExact([string]$binding.$field,'D',[ref]$id) -or $id -eq [guid]::Empty){throw "Resolve $role $field from actual identity outputs."}
        }
        if($binding.identityResourceId -notmatch "^/subscriptions/$($Contract.subscriptionId)/resourceGroups/$($Contract.resourceGroup)/providers/Microsoft.ManagedIdentity/userAssignedIdentities/[^/]+$"){
            throw "The $role identity leaves the approved existing platform."
        }
        if($binding.minReplicas -lt 1 -or $binding.maxReplicas -lt $binding.minReplicas){throw "Explicit reviewed scale is required for $role."}
        $names+=$binding.appName;$clients+=$binding.clientId;$principals+=$binding.principalId;$identities+=$binding.identityResourceId
    }
    foreach($values in @($names,$clients,$principals,$identities)){
        if(@($values|Sort-Object -Unique).Count -ne 5){throw 'Roles must have five distinct targets, clients, principals and attached identities.'}
    }
    $live=$Contract.liveConfig
    if($live.authorityMode -cne 'Policy' -or $live.entitlementProvider -cne 'Graph' -or $live.readConsistency -cne 'Session' -or
        -not $live.approvalReference -or $live.graphPolicy.approvalReference -match '^(?i)PENDING' -or -not $live.graphPolicy.approvalReference -or
        $live.graphPolicy.webServicePrincipalId -ne $Contract.webServicePrincipalId -or $live.syncClientId -ne $Contract.syncClientId -or
        $live.controlIdentityClientId -ne $Contract.roleBindings.Control.clientId -or $live.writerEpoch -lt 1 -or
        $live.graphPolicy.admittedUserIds.Count -ne 3 -or @($live.graphPolicy.admittedUserIds|Sort-Object -Unique).Count -ne 3 -or
        $live.resources.Count -ne 5 -or @($live.resources.id|Sort-Object -Unique).Count -ne 5 -or
        $live.subscriptionScopes.Count -ne 5 -or $live.directChats.Count -ne 3 -or
        @($live.PSObject.Properties.Name|Where-Object{$_ -like 'synthetic*'}).Count){
        throw 'Resolve the approved normal three-user/five-resource Graph policy; drafts and synthetic settings cannot be deployed.'
    }
    foreach($role in $roles){
        if($live.roleIdentityClientIds.$role -ne $Contract.roleBindings.$role.clientId){throw "The $role runtime identity map differs from its workload binding."}
    }
    foreach($resource in $live.resources){
        if(-not $live.graphPolicy.historyFrom.PSObject.Properties[$resource.id]){throw 'Every approved resource needs its preserved history cutoff.'}
        $path=if($resource.kind -eq 'chat'){"chats/$([uri]::EscapeDataString($resource.graphChatId))/messages"}
            elseif($resource.kind -eq 'channel'){"teams/$([uri]::EscapeDataString($resource.graphTeamId))/channels/$([uri]::EscapeDataString($resource.graphChannelId))/messages"}
            else{throw 'Unsupported initial resource kind.'}
        if(@($live.subscriptionScopes|Where-Object{$_.path -ceq $path}).Count -ne 1){throw 'Only the five approved per-resource subscription paths are allowed.'}
    }
}
function ConvertTo-ServiceCanonical($Value) {
    if($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]){return $Value}
    if($Value -is [array]){return ,@($Value|ForEach-Object{ConvertTo-ServiceCanonical $_})}
    $result=[ordered]@{}
    if($Value -is [Collections.IDictionary]){foreach($key in @($Value.Keys|Sort-Object)){$result[$key]=ConvertTo-ServiceCanonical $Value[$key]}}
    else{foreach($property in @($Value.PSObject.Properties|Sort-Object Name)){$result[$property.Name]=ConvertTo-ServiceCanonical $property.Value}}
    return $result
}
function Get-ServiceDevFingerprint($Value) { ConvertTo-ServiceCanonical $Value|ConvertTo-Json -Depth 100 -Compress }
function Get-ServiceDevPreservedState($App) {
    @{id=$App.id;location=$App.location;tags=$App.tags;identity=$App.identity;configuration=$App.properties.configuration;
        template=$App.properties.template;managedEnvironmentId=$App.properties.managedEnvironmentId;
        environmentId=$App.properties.environmentId;workloadProfileName=$App.properties.workloadProfileName}
}
function Assert-ServiceDevApp($App,[string]$Role,$Contract,[string]$RegistryServer) {
    $binding=$Contract.roleBindings.$Role
    $expected="/subscriptions/$($Contract.subscriptionId)/resourceGroups/$($Contract.resourceGroup)/providers/Microsoft.App/containerApps/$($binding.appName)"
    if($App.id -ne $expected -or $App.properties.managedEnvironmentId -ne $Contract.environmentId -or
        $App.properties.provisioningState -ne 'Succeeded' -or $App.properties.configuration.activeRevisionsMode -ne 'Single' -or
        $App.properties.latestRevisionName -ne $App.properties.latestReadyRevisionName -or
        -not $App.properties.latestReadyRevisionName -or @($App.properties.template.containers).Count -ne 1){
        throw "Wrong or unstable service workload: $Role."
    }
    $identities=@($App.identity.userAssignedIdentities.PSObject.Properties)
    if($App.identity.type -ne 'UserAssigned' -or $identities.Count -ne 1 -or $identities[0].Name -ne $binding.identityResourceId -or
        $identities[0].Value.clientId -ne $binding.clientId -or $identities[0].Value.principalId -ne $binding.principalId){
        throw "The $Role workload must attach only its own reviewed identity."
    }
    $container=$App.properties.template.containers[0]
    if($container.name -cne $binding.containerName -or $container.image -notmatch "^$([regex]::Escape($RegistryServer))/[^@]+@sha256:[a-f0-9]{64}$"){
        throw "The $Role container identity or immutable registry binding is invalid."
    }
    $registries=@($App.properties.configuration.registries|Where-Object server -eq $RegistryServer)
    if($registries.Count -ne 1 -or $registries[0].identity -ne $binding.identityResourceId -or $registries[0].username -or $registries[0].passwordSecretRef){
        throw "The $Role registry pull must use its attached role identity."
    }
    $settings=@{}
    foreach($entry in $container.env){
        if(-not $entry.name -or $settings.ContainsKey($entry.name) -or $entry.name.Contains(':')){throw "Ambiguous $Role environment."}
        $settings[$entry.name]=$entry
        if($entry.secretRef -and $entry.secretRef -notin @($App.properties.configuration.secrets.name)){throw "Unresolved $Role secret reference."}
    }
    foreach($pair in @(@('App__GraphMode','Live'),@('App__Auth','Entra'),@('App__Store','Cosmos'),@('App__Role',$Role),
        @('App__TenantId',$Contract.tenantId),@('AzureAd__TenantId',$Contract.tenantId),@('AzureAd__ClientId',$Contract.webClientId),
        @('AzureClientId',$binding.clientId),@('AZURE_CLIENT_ID',$binding.clientId),@('App__PublicOrigin',$Contract.publicOrigin))){
        if($settings[$pair[0]].value -cne $pair[1] -or $settings[$pair[0]].secretRef){throw "The $Role setting $($pair[0]) differs from the reviewed service contract."}
    }
    if($settings['App__Operation'].value){throw "A diagnostic operation is configured on $Role."}
    foreach($required in @('Azure__CosmosEndpoint','Azure__Redis','Azure__ServiceBus','Azure__PubSub','Azure__ProtectionKey',
        'Callbacks__Audience','Callbacks__ClientId','Callbacks__PrincipalId','App__TrustedProxyNetworks__0')){
        if(-not $settings[$required].value -or $settings[$required].secretRef){throw "Missing non-secret service setting: $Role/$required."}
    }
    if($Role -eq 'Api' -and ($settings['AzureAd__ClientCredentials__0__SourceType'].value -cne 'SignedAssertionFromManagedIdentity' -or
        $settings['AzureAd__ClientCredentials__0__ManagedIdentityClientId'].value -ne $binding.clientId)){
        throw 'The API confidential client must use only its own reviewed managed identity.'
    }
    $live=$settings['Live__Configuration'].value|ConvertFrom-Json -Depth 100
    if(@($live.hostedRoles).Count -ne 1 -or $live.hostedRoles[0] -cne $Role){throw "The $Role process cannot host additional roles."}
    $expectedLive=$Contract.liveConfig|ConvertTo-Json -Depth 100|ConvertFrom-Json -Depth 100
    $expectedLive|Add-Member -NotePropertyName hostedRoles -NotePropertyValue @($Role) -Force
    if((Get-ServiceDevFingerprint $live) -cne (Get-ServiceDevFingerprint $expectedLive)){throw "The $Role policy, scope, identities or admission differ from the approved contract."}
    if($settings['App__RealtimeAdmissionReady'].value -cne $live.admissionEnabled.ToString().ToLowerInvariant()){
        throw "The $Role realtime admission flag differs from its Live admission state."
    }
    $ingress=$App.properties.configuration.ingress
    if($Role -in @('Api','Receiver')){
        if($ingress.external -ne $true -or $ingress.allowInsecure -ne $false -or $ingress.targetPort -ne 8080){throw "The $Role protected ingress is invalid."}
    }elseif($ingress.external -eq $true){throw "The $Role background workload cannot expose public ingress."}
    if($App.properties.template.scale.minReplicas -ne $binding.minReplicas -or $App.properties.template.scale.maxReplicas -ne $binding.maxReplicas){
        throw "The $Role scale differs from the reviewed limits."
    }
}
function Assert-ServiceDevReaderRole($Definition,[string]$CosmosId) {
    $allowed=@('Microsoft.DocumentDB/databaseAccounts/readMetadata',
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read',
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/executeQuery',
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/readChangeFeed',
        'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/executeStoredProcedure')
    $actions=@($Definition.properties.permissions|ForEach-Object{$_.dataActions}|Sort-Object -Unique)
    if($Definition.id -notmatch "^$([regex]::Escape($CosmosId))/sqlRoleDefinitions/[^/]+$" -or
        $Definition.id -like '*/00000000-0000-0000-0000-000000000002' -or
        ($actions -join ',') -cne (@($allowed|Sort-Object)-join ',')){
        throw 'The primary-reader definition must contain exactly the reviewed read-only actions, with no item-write wildcard.'
    }
}
function Assert-ServiceDevDataAccess($Assignments,[string]$CosmosId,[string]$ReaderRoleId,$Contract) {
    $matrix=@{Api=@{'live-state'=$true;'live-authority'=$false};Receiver=@{'live-control'=$false};
        Projector=@{'live-state'=$true;'live-control'=$true};Delivery=@{'live-state'=$true;'live-authority'=$false;'live-leases'=$true};
        Control=@{'live-state'=$true;'live-control'=$true;'live-authority'=$true}}
    foreach($role in @(Get-ServiceDevRoles)){
        $actual=@($Assignments|Where-Object{$_.properties.principalId -eq $Contract.roleBindings.$role.principalId})
        $expected=$matrix[$role]
        if($actual.Count -ne $expected.Count){throw "Effective $Role Cosmos access contains missing or excess assignments; retire compact-role privileges before admission."}
        foreach($container in $expected.Keys){
            $scope="$CosmosId/dbs/teams/colls/$container"
            $definition=if($expected[$container]){"$CosmosId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"}else{$ReaderRoleId}
            if(@($actual|Where-Object{$_.properties.scope -eq $scope -and $_.properties.roleDefinitionId -eq $definition}).Count -ne 1){
                throw "The $Role $container access is not the reviewed least-privileged assignment."
            }
        }
    }
}
function Assert-ServiceDevReadRefresh($Queue,$Assignments,$Contract) {
    if($Queue.name -cne 'graph-read-refresh' -or $Queue.id -notmatch '/providers/Microsoft.ServiceBus/namespaces/[^/]+/queues/graph-read-refresh$' -or
        $Queue.status -cne 'Active' -or $Queue.requiresDuplicateDetection -ne $true -or $Queue.deadLetteringOnMessageExpiration -ne $true -or
        $Queue.maxDeliveryCount -ne 5 -or -not $Queue.defaultMessageTimeToLive -or -not $Queue.lockDuration -or
        -not $Queue.duplicateDetectionHistoryTimeWindow -or
        [Xml.XmlConvert]::ToTimeSpan($Queue.defaultMessageTimeToLive) -ne [TimeSpan]::FromMinutes(5) -or
        [Xml.XmlConvert]::ToTimeSpan($Queue.lockDuration) -ne [TimeSpan]::FromMinutes(1) -or
        [Xml.XmlConvert]::ToTimeSpan($Queue.duplicateDetectionHistoryTimeWindow) -ne [TimeSpan]::FromMinutes(1)){
        throw 'Provision the reviewed graph-read-refresh queue before an image-only release; no queue or grants are created by this script.'
    }
    $expected=@{Api='69a216fc-b8fb-44d8-bc22-1f3c2cd27a39';Control='4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'}
    $dataRoles=@($expected.Values)+@('090c5cfd-751d-490a-894a-3ce6f1109419')
    foreach($role in @('Api','Control')){
        $grants=@($Assignments|Where-Object{$_.principalId -eq $Contract.roleBindings.$role.principalId -and
            $_.roleDefinitionId.Split('/')[-1] -in $dataRoles})
        if($grants.Count -ne 1 -or $grants[0].scope -ine $Queue.id -or
            $grants[0].roleDefinitionId.Split('/')[-1] -cne $expected[$role]){
            throw "The $role read-refresh permission must be its exact queue-scoped sender/receiver grant, not inherited or broader data access."
        }
    }
}
Export-ModuleMember -Function Get-ServiceDevRoles,Assert-ServiceDevTargets,Get-ServiceDevFingerprint,Get-ServiceDevPreservedState,Assert-ServiceDevApp,Assert-ServiceDevDataAccess,Assert-ServiceDevReaderRole,Assert-ServiceDevReadRefresh
