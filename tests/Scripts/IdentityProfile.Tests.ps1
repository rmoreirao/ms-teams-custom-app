$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("teams-identity-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$profilePath = Join-Path $temporary 'profile.json'
$inventory = Join-Path $temporary 'resolved.json'
$tenant = '11111111-1111-1111-1111-111111111111'
$subscription = '22222222-2222-2222-2222-222222222222'
$user = '33333333-3333-3333-3333-333333333333'
$group = '66666666-6666-6666-6666-666666666666'
$profile = @{
    schemaVersion = 1; tenantId = $tenant; subscriptionId = $subscription; prefix = 'test-identity'
    applications = @(@{
        purpose = 'web'; assignmentRequired = $true; redirectUris = @('https://example.invalid/auth/callback')
        assignedUsers = @($user); assignedGroups = @($group); applicationPermissions = @()
        delegatedGrants = @(@{ consentType = 'Principal'; principalId = $user; scopes = @('openid', 'ChatMessage.Send') })
        federatedCredentials = @(@{ name = 'managed-api'; issuer = "https://login.microsoftonline.com/$tenant/v2.0"
            subject = '44444444-4444-4444-4444-444444444444'; audiences = @('api://AzureADTokenExchange') })
    })
}
$state = @{
    apps = [Collections.Generic.List[object]]::new(); principals = [Collections.Generic.List[object]]::new()
    assignments = [Collections.Generic.List[object]]::new(); grants = [Collections.Generic.List[object]]::new()
    fics = [Collections.Generic.List[object]]::new(); writes = 0; cliCalls = 0; failConsentOnce = $false; raceTrust = $false
}
function az {
    $state.cliCalls++
    $index = [Array]::IndexOf($args, '--subscription')
    if ($index -lt 0 -or $args[$index + 1] -ne $subscription) { throw 'Missing explicit subscription.' }
    $global:LASTEXITCODE = 0
    if ($args[1] -eq 'show') { return @{ tenantId = $tenant } | ConvertTo-Json }
    if ($args[1] -eq 'get-access-token') { return @{ accessToken = 'synthetic-test-value' } | ConvertTo-Json }
    throw 'Unexpected Azure CLI command.'
}
function Invoke-RestMethod {
    param($Method, $Uri, $Headers, $ContentType, $Body)
    $path = $Uri.Substring('https://graph.microsoft.com/v1.0/'.Length)
    if ($Method -eq 'GET') {
        if ($path -like "servicePrincipals?`$filter=appId eq '00000003*") {
            return @{ value = @(@{ id = 'graph-sp'; appId = '00000003-0000-0000-c000-000000000000'
                oauth2PermissionScopes = @(@{ id = 'openid-id'; value = 'openid'; isEnabled = $true }, @{ id = 'send-id'; value = 'ChatMessage.Send'; isEnabled = $true })
                appRoles = @() }) }
        }
        if ($path.StartsWith('applications?')) { return @{ value = @($state.apps.ToArray()) } }
        if ($path.StartsWith('servicePrincipals?')) { return @{ value = @($state.principals.ToArray()) } }
        if ($path -like 'applications/*/federatedIdentityCredentials') { return @{ value = @($state.fics.ToArray()) } }
        if ($path -eq 'applications/app-object/federatedIdentityCredentials/fic-managed') {
            if ($state.raceTrust) { $state.fics[0].subject = '77777777-7777-7777-7777-777777777777'; $state.raceTrust = $false }
            return $state.fics[0]
        }
        if ($path -like 'servicePrincipals/*/appRoleAssignedTo') { return @{ value = @($state.assignments.ToArray()) } }
        if ($path -like 'servicePrincipals/*/appRoleAssignments') { return @{ value = @() } }
        if ($path.StartsWith('oauth2PermissionGrants?')) { return @{ value = @($state.grants.ToArray()) } }
        if ($path.StartsWith("users/$user")) { return @{ id = $user } }
        if ($path.StartsWith("groups/$group")) { return @{ id = $group } }
        throw "Unexpected test read $path"
    }
    $state.writes++
    $value = $Body | ConvertFrom-Json
    if ($Method -eq 'POST' -and $path -eq 'applications') {
        $value | Add-Member id 'app-object'
        $value | Add-Member appId 'app-client'
        $state.apps.Add($value)
        return $value
    }
    if ($Method -eq 'POST' -and $path -eq 'servicePrincipals') {
        $value | Add-Member id 'client-sp'
        $value | Add-Member appRoleAssignmentRequired $false
        $state.principals.Add($value)
        return $value
    }
    if ($Method -eq 'PATCH' -and $path -eq 'servicePrincipals/client-sp') {
        $state.principals[0].appRoleAssignmentRequired = $value.appRoleAssignmentRequired
        return
    }
    if ($Method -eq 'POST' -and $path -like '*/federatedIdentityCredentials') {
        $value | Add-Member id 'fic-managed'
        $state.fics.Add($value); return $value
    }
    if ($Method -eq 'PATCH' -and $path -eq 'applications/app-object/federatedIdentityCredentials/fic-managed') {
        $state.fics[0].issuer = $value.issuer; $state.fics[0].subject = $value.subject; $state.fics[0].audiences = $value.audiences
        return
    }
    if ($Method -eq 'POST' -and $path -like '*/appRoleAssignedTo') {
        if ($value.resourceId -ne 'client-sp' -or $value.principalId -notin @($user, $group)) { throw 'Wrong user/group assignment IDs.' }
        $state.assignments.Add($value); return $value
    }
    if ($Method -eq 'POST' -and $path -eq 'oauth2PermissionGrants') {
        if ($state.failConsentOnce) { $state.failConsentOnce = $false; throw 'Synthetic consent interruption' }
        if ($value.clientId -ne 'client-sp' -or $value.resourceId -ne 'graph-sp') { throw 'Wrong consent ID types.' }
        $value | Add-Member id 'consent-id'
        $state.grants.Add($value); return $value
    }
    throw "Unexpected test mutation $Method $path"
}
function Save-Profile { $profile | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $profilePath }
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action }
    catch { if ($_.Exception.Message -notlike "*$Message*") { throw }; $failed = $true }
    if (-not $failed) { throw "Missing expected failure: $Message" }
}
try {
    Save-Profile
    $command = Join-Path $root 'scripts\entra\Sync-Identity.ps1'
    & $command -ProfilePath $profilePath -InventoryPath $inventory
    if ($state.writes -or (Test-Path -LiteralPath $inventory)) { throw 'Plan mutated state.' }
    Expect-Failure { & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Verify } 'not reconciled'
    Expect-Failure { & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Apply -Confirm:$false } 'ApprovePermissionChanges'
    if ($state.writes) { throw 'Unapproved apply mutated state.' }
    $state.failConsentOnce = $true
    Expect-Failure { & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Apply -ApprovePermissionChanges -Confirm:$false } 'Synthetic consent interruption'
    $checkpoint = Get-Content -Raw -LiteralPath $inventory | ConvertFrom-Json
    if ($checkpoint.reconciliationComplete -or $checkpoint.applications.web.principalId -ne 'client-sp') {
        throw 'Interrupted apply lost its partial checkpoint.'
    }
    & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Apply -ApprovePermissionChanges -Confirm:$false
    $count = $state.writes
    $resolved = Get-Content -Raw -LiteralPath $inventory | ConvertFrom-Json
    if (-not $resolved.reconciliationComplete -or $resolved.applications.web.principalId -ne 'client-sp') { throw 'Final checkpoint missing.' }
    & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Apply -ApprovePermissionChanges -Confirm:$false
    if ($state.writes -ne $count) { throw 'Second apply was not a no-op.' }
    Remove-Item -LiteralPath $inventory
    & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Apply -WhatIf
    & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Export -WhatIf
    if (Test-Path -LiteralPath $inventory) { throw 'WhatIf wrote rediscovered state.' }
    & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Export -Confirm:$false
    if ($state.writes -ne $count) { throw 'Rediscovery duplicated directory identities.' }
    $state.fics[0].subject = '55555555-5555-5555-5555-555555555555'
    & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Plan
    Expect-Failure { & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Apply -ApprovePermissionChanges -Confirm:$false } 'ApproveTrustRotation'
    if ($state.writes -ne $count) { throw 'Unapproved trust rotation changed directory state.' }
    & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Apply -ApproveTrustRotation -Confirm:$false
    if ($state.fics[0].subject -ne $profile.applications[0].federatedCredentials[0].subject) { throw 'Approved exact trust rotation failed.' }
    $count = $state.writes
    $state.fics[0].subject = '55555555-5555-5555-5555-555555555555'
    $state.raceTrust = $true
    Expect-Failure { & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Apply -ApproveTrustRotation -Confirm:$false } 'changed after preflight'
    if ($state.writes -ne $count) { throw 'Concurrent trust drift was overwritten.' }
    $state.fics[0].subject = $profile.applications[0].federatedCredentials[0].subject
    $state.grants[0].scope += ' Chat.Read'
    Expect-Failure { & $command -ProfilePath $profilePath -InventoryPath $inventory -Mode Apply -ApprovePermissionChanges -Confirm:$false } 'Unexpected delegated access'
    if ($state.writes -ne $count) { throw 'Drift rejection mutated state.' }
    $before = $state.cliCalls
    $profile.tenantId = [guid]::Empty.ToString(); Save-Profile
    Expect-Failure { & $command -ProfilePath $profilePath -InventoryPath $inventory } 'Replace the example'
    if ($state.cliCalls -ne $before) { throw 'Placeholder validation contacted Azure.' }
    $profile.tenantId = $tenant; $profile.unknown = $true; Save-Profile
    Expect-Failure { & $command -ProfilePath $profilePath -InventoryPath $inventory } '*'
    if ($state.cliCalls -ne $before) { throw 'Invalid schema contacted Azure.' }
    Write-Output 'Passed identity profile checks: no-write plan/WhatIf, verify gate, user/group admission, consent opt-in, interrupted apply/resume, verification/export, no-op reapply, rediscovery, explicit trust rotation/race refusal, broad-grant refusal, placeholder/schema rejection.'
} finally {
    foreach ($path in @($profilePath, $inventory)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path } }
    Remove-Item -LiteralPath $temporary
}
