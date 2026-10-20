[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$ProfilePath,
    [Parameter(Mandatory)][string]$InventoryPath,
    [ValidateSet('Plan', 'Apply', 'Verify', 'Export')][string]$Mode = 'Plan',
    [switch]$ApprovePermissionChanges,
    [switch]$ApproveTrustRotation
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$inventoryFile = [IO.Path]::GetFullPath($InventoryPath)
if ($inventoryFile.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Resolved identity inventories must be outside the repository.'
}
$profileHash = (Get-FileHash -LiteralPath $ProfilePath -Algorithm SHA256).Hash
$json = Get-Content -Raw -LiteralPath $ProfilePath
if ((Get-FileHash -LiteralPath $ProfilePath -Algorithm SHA256).Hash -ne $profileHash) {
    throw 'Identity profile changed while it was being read.'
}
$schema = Join-Path $root 'infra\identity\profile.schema.json'
if (-not (Test-Json -Json $json -SchemaFile $schema -ErrorAction Stop)) { throw 'Invalid identity profile.' }
$profile = $json | ConvertFrom-Json
if ([guid]$profile.tenantId -eq [guid]::Empty -or [guid]$profile.subscriptionId -eq [guid]::Empty) {
    throw 'Replace the example tenant/subscription IDs before contacting Azure.'
}
if (@($profile.applications.purpose | Sort-Object -Unique).Count -ne $profile.applications.Count) { throw 'Duplicate application purpose.' }
foreach ($desired in $profile.applications) {
    if ($desired.purpose -eq 'web' -and -not $desired.assignmentRequired) { throw 'The web application must require explicit assignment.' }
    if ($desired.purpose -eq 'sync' -and $desired.assignmentRequired) {
        throw 'This implementation requires the documented rich-notification assignment policy on sync; Change Tracking role provisioning is not implemented.'
    }
    if ($desired.purpose -ne 'sync' -and $desired.applicationPermissions) { throw 'Only sync may receive Graph application permissions.' }
    if ($desired.purpose -ne 'web' -and $desired.delegatedGrants) { throw 'Only web may receive delegated grants.' }
    if ($desired.purpose -ne 'web' -and $desired.assignedUsers) { throw 'Only web may admit users.' }
    if ($desired.purpose -ne 'web' -and $desired.assignedGroups) { throw 'Only web may admit groups.' }
    if ($desired.purpose -ne 'events' -and $desired.callbackPrincipals) { throw 'Callback assignments belong to events.' }
    if ($desired.graphNotificationPublisher -and ($desired.purpose -ne 'web' -or -not $desired.assignmentRequired)) {
        throw 'Graph publisher admission is an explicit assignment-required web application setting.'
    }
    if ($desired.basicGraphValidation -and ($desired.purpose -ne 'web' -or $desired.assignedUsers.Count -ne 3 -or
        $desired.assignedGroups -or @($desired.delegatedGrants | Where-Object consentType -ne 'Principal').Count)) {
        throw 'Basic Graph validation is restricted to the three directly assigned web users with Principal consent.'
    }
    if ($desired.directChatValidation -and (-not $desired.basicGraphValidation -or $desired.delegatedGrants.Count -ne 3)) {
        throw 'Direct chat validation requires the explicit three-user basic Graph profile and three Principal grants.'
    }
    $principals = @()
    foreach ($grant in $desired.delegatedGrants) {
        if (($grant.consentType -eq 'Principal') -ne ($null -ne $grant.principalId)) { throw 'Principal grants require a user; AllPrincipals grants must not specify one.' }
        $key = "$($grant.consentType):$($grant.principalId)"
        if ($key -in $principals) { throw 'Duplicate delegated consent principal.' }
        $principals += $key
        if ($grant.consentType -eq 'Principal' -and $grant.principalId -notin $desired.assignedUsers) { throw 'Consent users must be explicitly assigned.' }
        $allowedScopes = @('openid', 'profile', 'offline_access', 'ChatMessage.Send', 'ChannelMessage.Send')
        if ($desired.basicGraphValidation) { $allowedScopes += @('Chat.Read', 'Channel.ReadBasic.All', 'ChannelMessage.Read.All') }
        if ($desired.directChatValidation) { $allowedScopes += 'Chat.Create' }
        if (@($grant.scopes | Where-Object { $_ -notin $allowedScopes }).Count) {
            throw 'Web grants exceed the supported sign-in/send permission boundary.'
        }
    }
    if (@($desired.federatedCredentials.name | Sort-Object -Unique).Count -ne @($desired.federatedCredentials | Where-Object { $null -ne $_ }).Count) {
        throw 'Duplicate federated credential name.'
    }
    foreach ($fic in $desired.federatedCredentials) {
        $managedIssuer = "https://login.microsoftonline.com/$($profile.tenantId)/v2.0"
        $managedPrincipal = [guid]::Empty
        if ($fic.issuer -ceq 'https://token.actions.githubusercontent.com') {
            if ($desired.purpose -ne 'deploy' -or $fic.subject -notmatch '^repo:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+:environment:[A-Za-z0-9_.-]+$') {
                throw 'GitHub trust requires a deploy identity and an exact environment subject.'
            }
        } elseif ($fic.issuer -cne $managedIssuer -or $desired.purpose -notin @('web', 'sync') -or
            -not [guid]::TryParse($fic.subject, [ref]$managedPrincipal) -or $managedPrincipal -eq [guid]::Empty) {
            throw 'Workload trust must use a same-tenant managed-identity principal ID.'
        }
    }
}
if (Test-Path -LiteralPath $inventoryFile) {
    $previous = Get-Content -Raw -LiteralPath $inventoryFile | ConvertFrom-Json
    if ($previous.schemaVersion -ne 2) { throw 'Use a separate schema-v2 reconciliation inventory; preserve the legacy bootstrap inventory.' }
    if ($previous.tenantId -ne $profile.tenantId -or $previous.prefix -ne $profile.prefix) { throw 'Inventory belongs to another tenant or prefix.' }
}
$account = az account show --subscription $profile.subscriptionId --output json | ConvertFrom-Json
if ($LASTEXITCODE -or $account.tenantId -ne $profile.tenantId) { throw 'Subscription and profile tenant do not match.' }
$token = az account get-access-token --subscription $profile.subscriptionId --resource-type ms-graph --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Graph authentication failed.' }
$headers = @{ Authorization = "Bearer $($token.accessToken)" }
$tag = "teams-reference:$($profile.prefix)"
$observed = @{}
$steps = [Collections.Generic.List[object]]::new()
function Graph([string]$Method, [string]$Path, $Body = $null) {
    $arguments = @{ Method = $Method; Uri = "https://graph.microsoft.com/v1.0/$Path"; Headers = $headers; ContentType = 'application/json' }
    if ($null -ne $Body) { $arguments.Body = $Body | ConvertTo-Json -Depth 20 -Compress }
    Invoke-RestMethod @arguments
}
function Collection([string]$Path) {
    $items = @()
    do {
        $page = Graph GET $Path
        $items += @($page.value)
        $next = $page.'@odata.nextLink'
        if ($next) {
            $uri = [uri]$next
            if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'graph.microsoft.com' -or $uri.Port -ne 443 -or
                $uri.UserInfo -or $uri.Fragment -or -not $uri.AbsolutePath.StartsWith('/v1.0/')) { throw 'Untrusted Graph continuation URL.' }
            $Path = $next.Substring('https://graph.microsoft.com/v1.0/'.Length)
        }
    } while ($next)
    $items
}
function Same($Left, $Right) {
    ((@($Left) | Sort-Object) -join "`n") -ceq ((@($Right) | Sort-Object) -join "`n")
}
function Step([string]$Purpose, [string]$Kind, $Body = $null, [string]$Id = '', [bool]$Permission = $false) {
    $steps.Add(@{ purpose = $Purpose; kind = $Kind; body = $Body; id = $Id; permission = $Permission })
}
function Save-Inventory([bool]$Complete) {
    $directory = Split-Path $inventoryFile -Parent
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $value = @{
        schemaVersion = 2; tenantId = $profile.tenantId; prefix = $profile.prefix
        profileHash = $profileHash
        applications = $observed; reconciliationComplete = $Complete
        checkedAt = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $temporary = "$inventoryFile.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $value | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $temporary -Encoding utf8
        Move-Item -LiteralPath $temporary -Destination $inventoryFile -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
try {
    $resources = @(Collection "servicePrincipals?`$filter=appId eq '00000003-0000-0000-c000-000000000000'")
    if ($resources.Count -ne 1) { throw 'Microsoft Graph resource principal missing or ambiguous.' }
    $resource = $resources[0]
    foreach ($desired in $profile.applications) {
        $purpose = $desired.purpose
        $name = "$($profile.prefix)-$purpose"
        $apps = @(Collection "applications?`$filter=displayName eq '$name'")
        if ($apps.Count -gt 1) { throw "Ambiguous application: $name" }
        $app = if ($apps.Count) { $apps[0] } else { $null }
        if ($app -and ($tag -notin $app.tags -or $app.signInAudience -ne 'AzureADMyOrg')) { throw "Application ownership/audience mismatch: $name" }
        if ($previous -and $previous.applications.$purpose.clientId -and
            ($null -eq $app -or $previous.applications.$purpose.clientId -ne $app.appId -or $previous.applications.$purpose.objectId -ne $app.id)) {
            throw "Inventory identity changed for $purpose; review replacement explicitly."
        }
        $scopeNames = @($desired.delegatedGrants | ForEach-Object { $_.scopes } | Sort-Object -Unique)
        $scopeIds = @()
        foreach ($scope in $scopeNames) {
            $match = @($resource.oauth2PermissionScopes | Where-Object { $_.value -ceq $scope -and $_.isEnabled })
            if ($match.Count -ne 1) { throw "Unsupported delegated scope: $scope" }
            $scopeIds += @{ id = $match[0].id; type = 'Scope' }
        }
        $roleIds = @()
        foreach ($role in $desired.applicationPermissions) {
            $match = @($resource.appRoles | Where-Object { $_.value -ceq $role -and $_.isEnabled -and 'Application' -in $_.allowedMemberTypes })
            if ($match.Count -ne 1 -or $role -eq 'Teamwork.Migrate.All') { throw "Unsupported application role: $role" }
            $roleIds += @{ id = $match[0].id; type = 'Role' }
        }
        $access = @()
        if ($scopeIds.Count + $roleIds.Count) { $access = @(@{ resourceAppId = $resource.appId; resourceAccess = @($scopeIds) + @($roleIds) }) }
        $body = @{ displayName = $name; signInAudience = 'AzureADMyOrg'; tags = @($tag); requiredResourceAccess = $access }
        if ($purpose -eq 'web') {
            $body.web = @{ redirectUris = @($desired.redirectUris | Where-Object { $null -ne $_ }); implicitGrantSettings = @{ enableAccessTokenIssuance = $false; enableIdTokenIssuance = $false } }
            $body.web.logoutUrl = $desired.logoutUrl
        }
        if ($purpose -eq 'events') {
            $body.appRoles = @(@{ id = '277c2a98-926e-469c-8bd4-eb51680d6377'; value = 'PubSub.Callback'; displayName = 'PubSub callback'
                description = 'Authenticate the designated Web PubSub upstream service.'; allowedMemberTypes = @('Application'); isEnabled = $true })
        }
        if (-not $app) { Step $purpose 'create-app' $body }
        else {
            $body.tags = @($app.tags)
            if ($purpose -eq 'web' -and (
                @($app.web.redirectUris | Where-Object { $_ -notin $desired.redirectUris }).Count -or
                ($app.web.logoutUrl -and $app.web.logoutUrl -ne $desired.logoutUrl))) {
                throw 'Existing web redirects/logout require explicit rotation; removal is not implicit reconciliation.'
            }
            $declared = @($app.requiredResourceAccess | ForEach-Object { $resourceApp = $_.resourceAppId; $_.resourceAccess | ForEach-Object { "$resourceApp/$($_.type)/$($_.id)" } })
            $wanted = @($access | ForEach-Object { $resourceApp = $_.resourceAppId; $_.resourceAccess | ForEach-Object { "$resourceApp/$($_.type)/$($_.id)" } })
            if (@($declared | Where-Object { $_ -notin $wanted }).Count) { throw "Unexpected declared permissions on $purpose; explicit review required." }
            if (-not (Same $declared $wanted) -or ($purpose -eq 'web' -and
                (-not (Same $app.web.redirectUris $desired.redirectUris) -or $app.web.logoutUrl -ne $desired.logoutUrl -or
                    $app.web.implicitGrantSettings.enableAccessTokenIssuance -or $app.web.implicitGrantSettings.enableIdTokenIssuance))) {
                Step $purpose 'update-app' $body
            }
            if ($purpose -eq 'events' -and -not ($app.appRoles | Where-Object { $_.id -eq '277c2a98-926e-469c-8bd4-eb51680d6377' -and $_.value -eq 'PubSub.Callback' -and $_.isEnabled })) {
                throw 'Existing callback role requires explicit reconciliation.'
            }
        }
        $sps = if ($app) { @(Collection "servicePrincipals?`$filter=appId eq '$($app.appId)'") } else { @() }
        if ($sps.Count -gt 1) { throw 'Ambiguous client service principal.' }
        $sp = if ($sps.Count) { $sps[0] } else { $null }
        if ($previous -and $previous.applications.$purpose.principalId -and
            $previous.applications.$purpose.principalId -ne $sp.id) { throw "Service principal changed for $purpose; review replacement explicitly." }
        if (-not $sp) { Step $purpose 'create-sp' }
        if (-not $sp -or $sp.appRoleAssignmentRequired -ne $desired.assignmentRequired) {
            Step $purpose 'assignment-policy' @{ appRoleAssignmentRequired = $desired.assignmentRequired } '' $true
        }
        if ($purpose -eq 'events' -and $app -and
            @($app.identifierUris | Where-Object { $_ -cne "api://$($app.appId)" }).Count) {
            throw 'Unexpected callback audience requires explicit rotation.'
        }
        if ($purpose -eq 'events' -and $app -and -not (Same $app.identifierUris @("api://$($app.appId)"))) { Step $purpose 'audience' }
        elseif ($purpose -eq 'events' -and -not $app) { Step $purpose 'audience' }
        $fics = if ($app) { @(Collection "applications/$($app.id)/federatedIdentityCredentials") } else { @() }
        foreach ($fic in $fics) {
            $expected = @($desired.federatedCredentials | Where-Object name -CEQ $fic.name)
            if ($expected.Count -ne 1) { throw "Unexpected federation on $purpose; explicit removal review required." }
            if ($expected[0].issuer -cne $fic.issuer -or $expected[0].subject -cne $fic.subject -or
                -not (Same $expected[0].audiences $fic.audiences)) {
                if (-not $fic.id) { throw 'Existing federation has no stable object ID.' }
                Step $purpose 'federation-rotation' @{
                    issuer = $expected[0].issuer; subject = $expected[0].subject; audiences = @($expected[0].audiences)
                } $fic.id
                $steps[$steps.Count - 1].expected = @{
                    name = $fic.name; issuer = $fic.issuer; subject = $fic.subject; audiences = @($fic.audiences)
                }
            }
        }
        foreach ($fic in $desired.federatedCredentials) { if ($fic.name -cnotin $fics.name) { Step $purpose 'federation' $fic } }
        $assignments = if ($sp) { @(Collection "servicePrincipals/$($sp.id)/appRoleAssignedTo") } else { @() }
        $publisher = $null
        if ($desired.graphNotificationPublisher) {
            if (@($app.appRoles | Where-Object { $null -ne $_ }).Count) {
                throw 'Default publisher admission requires a web application with no declared app roles.'
            }
            $publishers = @(Collection "servicePrincipals?`$filter=appId eq '0bf30f3b-4a52-48df-9a82-234910c4a086'")
            if ($publishers.Count -ne 1 -or $publishers[0].appId -ne '0bf30f3b-4a52-48df-9a82-234910c4a086') {
                throw 'The existing Microsoft Graph Change Tracking principal is missing or ambiguous.'
            }
            $publisher = $publishers[0]
        }
        if ($purpose -eq 'web') {
            foreach ($assignment in $assignments) {
                if ($assignment.principalId -notin (@($desired.assignedUsers) + @($desired.assignedGroups) + @($publisher.id)) -or
                    $assignment.appRoleId -ne [guid]::Empty.ToString()) {
                    throw 'Unexpected web admission assignment; explicit review required.'
                }
            }
        }
        foreach ($user in $desired.assignedUsers) {
            $null = Graph GET "users/$user`?`$select=id"
            if ($user -notin $assignments.principalId) { Step $purpose 'user' @{ principalId = $user; appRoleId = [guid]::Empty.ToString() } '' $true }
        }
        foreach ($group in $desired.assignedGroups) {
            $null = Graph GET "groups/$group`?`$select=id"
            if ($group -notin $assignments.principalId) { Step $purpose 'group' @{ principalId = $group; appRoleId = [guid]::Empty.ToString() } '' $true }
        }
        if ($publisher -and $publisher.id -notin $assignments.principalId) {
            Step $purpose 'publisher' @{ principalId = $publisher.id; appRoleId = [guid]::Empty.ToString() } '' $true
        }
        if ($purpose -eq 'events') {
            foreach ($assignment in $assignments) {
                if ($assignment.principalId -notin $desired.callbackPrincipals -or $assignment.appRoleId -ne '277c2a98-926e-469c-8bd4-eb51680d6377') {
                    throw 'Unexpected callback admission assignment; explicit review required.'
                }
            }
            foreach ($principal in $desired.callbackPrincipals) {
                $null = Graph GET "servicePrincipals/$principal`?`$select=id"
                if ($principal -notin $assignments.principalId) {
                    Step $purpose 'callback' @{ principalId = $principal; appRoleId = '277c2a98-926e-469c-8bd4-eb51680d6377' } '' $true
                }
            }
        }
        $grants = if ($sp) { @(Collection "oauth2PermissionGrants?`$filter=clientId eq '$($sp.id)'") } else { @() }
        foreach ($grant in $grants) {
            $expected = @($desired.delegatedGrants | Where-Object { $_.consentType -eq $grant.consentType -and $_.principalId -eq $grant.principalId })
            if ($grant.resourceId -ne $resource.id -or $expected.Count -ne 1 -or
                @($grant.scope.Split(' ', [StringSplitOptions]::RemoveEmptyEntries) | Where-Object { $_ -notin $expected[0].scopes }).Count) {
                throw "Unexpected delegated access on $purpose; explicit review required."
            }
        }
        foreach ($grant in $desired.delegatedGrants) {
            $existing = @($grants | Where-Object { $_.consentType -eq $grant.consentType -and $_.principalId -eq $grant.principalId })
            if ($existing.Count -gt 1) { throw 'Duplicate consent records.' }
            if (-not $existing.Count -or -not (Same ($existing[0].scope.Split(' ')) $grant.scopes)) {
                $grantBody = @{ resourceId = $resource.id; consentType = $grant.consentType; scope = $grant.scopes -join ' ' }
                if ($grant.principalId) { $grantBody.principalId = $grant.principalId }
                Step $purpose 'consent' $grantBody $(if ($existing.Count) { $existing[0].id } else { '' }) $true
            }
        }
        $roles = if ($sp) { @(Collection "servicePrincipals/$($sp.id)/appRoleAssignments") } else { @() }
        foreach ($role in $roles) {
            if ($role.resourceId -ne $resource.id -or $role.appRoleId -notin $roleIds.id) { throw "Unexpected application access on $purpose; explicit review required." }
        }
        foreach ($role in $roleIds) {
            if ($role.id -notin $roles.appRoleId) { Step $purpose 'role' @{ resourceId = $resource.id; appRoleId = $role.id } '' $true }
        }
        $observed[$purpose] = @{ objectId = $app.id; clientId = $app.appId; principalId = $sp.id
            delegatedGrants = $grants; applicationGrants = $roles; federatedCredentials = $fics; admissionAssignments = $assignments }
    }
    Write-Output ("Identity {0}: {1} pending changes ({2})." -f $Mode, $steps.Count, (($steps | ForEach-Object { "$($_.purpose)/$($_.kind)" }) -join ', '))
    if ((Get-FileHash -LiteralPath $ProfilePath -Algorithm SHA256).Hash -ne $profileHash) {
        throw 'Identity profile changed during preflight; nothing was applied.'
    }
    if ($Mode -eq 'Verify' -and $steps.Count) { throw 'Identity profile is not reconciled.' }
    if ($Mode -eq 'Export' -and $PSCmdlet.ShouldProcess($inventoryFile, 'Export resolved identity state')) {
        Save-Inventory ($steps.Count -eq 0)
    }
    if ($Mode -ne 'Apply') { return }
    if (-not $steps.Count) {
        if (-not $previous -or -not $previous.reconciliationComplete -or
            $previous.profileHash -ne $profileHash) {
            if ($PSCmdlet.ShouldProcess($inventoryFile, 'Checkpoint verified identity state')) { Save-Inventory $true }
        }
        return
    }
    if (@($steps | Where-Object permission).Count -and -not $ApprovePermissionChanges) { throw 'Explicit -ApprovePermissionChanges is required for consent or admission changes.' }
    if (@($steps | Where-Object kind -eq 'federation-rotation').Count -and -not $ApproveTrustRotation) {
        throw 'Explicit -ApproveTrustRotation is required for changed federation; no trust was modified.'
    }
    if (-not $PSCmdlet.ShouldProcess("$($profile.tenantId)/$($profile.prefix)", 'Apply preflighted identity changes and explicitly selected trust rotation; no implicit removals')) { return }
    Save-Inventory $false
    foreach ($step in $steps) {
        $ids = $observed[$step.purpose]
        switch ($step.kind) {
            'create-app' { $result = Graph POST 'applications' $step.body; $ids.objectId = $result.id; $ids.clientId = $result.appId }
            'update-app' { $null = Graph PATCH "applications/$($ids.objectId)" $step.body }
            'create-sp' { $result = Graph POST 'servicePrincipals' @{ appId = $ids.clientId }; $ids.principalId = $result.id }
            'assignment-policy' { $null = Graph PATCH "servicePrincipals/$($ids.principalId)" $step.body }
            'audience' { $null = Graph PATCH "applications/$($ids.objectId)" @{ identifierUris = @("api://$($ids.clientId)") } }
            'federation' { $null = Graph POST "applications/$($ids.objectId)/federatedIdentityCredentials" $step.body }
            'federation-rotation' {
                $current = Graph GET "applications/$($ids.objectId)/federatedIdentityCredentials/$($step.id)"
                if ($current.name -cne $step.expected.name -or $current.issuer -cne $step.expected.issuer -or
                    $current.subject -cne $step.expected.subject -or -not (Same $current.audiences $step.expected.audiences)) {
                    throw 'Federation changed after preflight; rotation refused.'
                }
                $null = Graph PATCH "applications/$($ids.objectId)/federatedIdentityCredentials/$($step.id)" $step.body
            }
            'user' { $step.body.resourceId = $ids.principalId; $null = Graph POST "servicePrincipals/$($ids.principalId)/appRoleAssignedTo" $step.body }
            'group' { $step.body.resourceId = $ids.principalId; $null = Graph POST "servicePrincipals/$($ids.principalId)/appRoleAssignedTo" $step.body }
            'callback' { $step.body.resourceId = $ids.principalId; $null = Graph POST "servicePrincipals/$($ids.principalId)/appRoleAssignedTo" $step.body }
            'publisher' { $step.body.resourceId = $ids.principalId; $null = Graph POST "servicePrincipals/$($ids.principalId)/appRoleAssignedTo" $step.body }
            'consent' {
                if ($step.id) { $null = Graph PATCH "oauth2PermissionGrants/$($step.id)" @{ scope = $step.body.scope } }
                else { $step.body.clientId = $ids.principalId; $null = Graph POST 'oauth2PermissionGrants' $step.body }
            }
            'role' { $step.body.principalId = $ids.principalId; $null = Graph POST "servicePrincipals/$($ids.principalId)/appRoleAssignments" $step.body }
        }
        Save-Inventory $false
    }
    & $PSCommandPath -ProfilePath $ProfilePath -InventoryPath $InventoryPath -Mode Verify
    & $PSCommandPath -ProfilePath $ProfilePath -InventoryPath $InventoryPath -Mode Export -Confirm:$false
} finally {
    $headers.Clear()
    $token = $null
}
