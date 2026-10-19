$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("teams-consent-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$inventoryPath = Join-Path $temporary 'identity.json'
$tenant = '11111111-1111-1111-1111-111111111111'
$subscription = '22222222-2222-2222-2222-222222222222'
$users = @('alex', 'sam', 'jordan') | ForEach-Object { @{ id = "user-$_"; persona = $_ } }
@{
    tenantId = $tenant
    prefix = 'teams-reference-dev'
    bootstrapComplete = $true
    applications = @{ web = @{ objectId = 'web-object'; clientId = 'web-client'; principalId = 'web-principal' } }
    users = @($users)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $inventoryPath
$consentTestState = @{
    grants = [Collections.Generic.List[object]]::new()
    writes = [Collections.Generic.List[object]]::new()
    assignmentRequired = $true
}
function az {
    $index = [Array]::IndexOf($args, '--subscription')
    if ($index -lt 0 -or $args[$index + 1] -ne $subscription) { throw 'Consent command did not explicitly select its subscription.' }
    $global:LASTEXITCODE = 0
    if ($args[1] -eq 'show') { return @{ tenantId = $tenant } | ConvertTo-Json }
    if ($args[1] -eq 'get-access-token') { return @{ accessToken = 'synthetic-unit-test-value' } | ConvertTo-Json }
    throw 'Unexpected Azure CLI operation.'
}
function Invoke-RestMethod {
    param($Method, $Uri, $Headers, $ContentType, $Body)
    if ($Method -eq 'GET' -and $Uri -like '*/applications/web-object`?*') {
        return @{ appId = 'web-client'; tags = @('teams-reference:teams-reference-dev') }
    }
    if ($Method -eq 'GET' -and $Uri -like '*/servicePrincipals/web-principal`?*') {
        return @{ id = 'web-principal'; appId = 'web-client'; appRoleAssignmentRequired = $consentTestState.assignmentRequired }
    }
    if ($Method -eq 'GET' -and $Uri -like '*/servicePrincipals`?*') {
        return @{ value = @(@{ id = 'graph-principal'; oauth2PermissionScopes = @(
            @{ value = 'openid'; isEnabled = $true }
            @{ value = 'profile'; isEnabled = $true }
            @{ value = 'offline_access'; isEnabled = $true }
        ) }) }
    }
    if ($Method -eq 'GET' -and $Uri -like '*/appRoleAssignedTo`?*') {
        return @{ value = @($users | ForEach-Object { @{ principalId = $_.id } }) }
    }
    if ($Method -eq 'GET' -and $Uri -like '*/oauth2PermissionGrants`?*') {
        return @{ value = @($consentTestState.grants.ToArray()) }
    }
    if ($Method -eq 'POST' -and $Uri -like '*/oauth2PermissionGrants') {
        $grant = $Body | ConvertFrom-Json
        if ($grant.clientId -ne 'web-principal' -or $grant.resourceId -ne 'graph-principal' -or
            $grant.consentType -ne 'Principal' -or $grant.principalId -notin $users.id -or
            $grant.scope -ne 'openid profile offline_access') {
            throw 'Consent payload exceeded the approved application, resource, users or scopes.'
        }
        $grant | Add-Member -NotePropertyName id -NotePropertyValue "grant-$($consentTestState.grants.Count)"
        $consentTestState.grants.Add($grant)
        $consentTestState.writes.Add($grant)
        return $grant
    }
    if ($Method -eq 'PATCH' -and $Uri -like '*/oauth2PermissionGrants/existing-alex') {
        $update = $Body | ConvertFrom-Json
        if ($update.scope -ne 'openid profile offline_access') { throw 'Consent update exceeded the standard sign-in scopes.' }
        $consentTestState.grants[0].scope = $update.scope
        $consentTestState.writes.Add($update)
        return
    }
    throw "Unexpected Graph operation in isolated consent test: $Method $Uri"
}
try {
    $command = Join-Path $root 'scripts\entra\Grant-DevSignInConsent.ps1'
    & $command -SubscriptionId $subscription -InventoryPath $inventoryPath
    if ($consentTestState.writes.Count) { throw 'Plan-only execution wrote a consent record.' }
    $consentTestState.grants.Add([pscustomobject]@{
        id = 'existing-alex'; clientId = 'web-principal'; resourceId = 'graph-principal'
        consentType = 'Principal'; principalId = 'user-alex'; scope = 'openid'
    })
    & $command -SubscriptionId $subscription -InventoryPath $inventoryPath -Apply -Confirm:$false
    if ($consentTestState.writes.Count -ne 3) { throw 'Expected exactly three user-specific grants.' }
    & $command -SubscriptionId $subscription -InventoryPath $inventoryPath -Apply -Confirm:$false
    if ($consentTestState.writes.Count -ne 3) { throw 'Repeated consent execution was not idempotent.' }

    $consentTestState.grants.Add([pscustomobject]@{ id = 'existing-broad'; consentType = 'AllPrincipals'; scope = 'openid profile'; principalId = $null })
    try {
        & $command -SubscriptionId $subscription -InventoryPath $inventoryPath -Apply -Confirm:$false
        throw 'Unexpected tenant-wide consent was not rejected.'
    } catch {
        if ($_.Exception.Message -ne 'Existing broader or unexpected consent requires explicit review; nothing was changed.') { throw }
    }
    if ($consentTestState.writes.Count -ne 3) { throw 'Broader consent caused unapproved changes.' }
    $consentTestState.grants.RemoveAt(3)
    $consentTestState.assignmentRequired = $false
    try {
        & $command -SubscriptionId $subscription -InventoryPath $inventoryPath -Apply -Confirm:$false
        throw 'Consent was accepted for a non-assignment-restricted app.'
    } catch {
        if ($_.Exception.Message -ne 'The expected web service principal must require explicit user assignment.') { throw }
    }
    Write-Output 'Passed 5 isolated consent checks: plan-only, exact three-user scopes, idempotency, broader-consent refusal, and assignment restriction.'
} finally {
    Remove-Item -LiteralPath $inventoryPath -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue
}
