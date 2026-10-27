$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temporary = Join-Path ([IO.Path]::GetTempPath()) ("teams-entra-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
$tenant = [guid]'11111111-1111-1111-1111-111111111111'
$subscription = [guid]'22222222-2222-2222-2222-222222222222'
$testState = @{
    accountCalls = [Collections.Generic.List[object]]::new()
    graphCalls = [Collections.Generic.List[string]]::new()
    platformParameters = $null
    networkFeature = 'Registered'
}

function az {
    $testState.accountCalls.Add(@($args))
    $index = [Array]::IndexOf($args, '--subscription')
    if ($index -lt 0 -or $args[$index + 1] -ne $subscription.ToString()) {
        throw 'Bootstrap did not explicitly select the requested subscription.'
    }
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'account' -and $args[1] -eq 'show') {
        return @{ tenantId = $tenant.ToString() } | ConvertTo-Json
    }
    if ($args[0] -eq 'account' -and $args[1] -eq 'get-access-token') {
        return @{ accessToken = 'synthetic-unit-test-value' } | ConvertTo-Json
    }
    if ($args[0] -eq 'feature' -and $args[1] -eq 'show') { return $testState.networkFeature }
    if ($args[0] -eq 'deployment' -and $args[1] -eq 'sub' -and $args[2] -eq 'what-if') {
        $regionIndex = [Array]::IndexOf($args, '--location')
        if ($args[$regionIndex + 1] -ne 'swedencentral') { throw 'Deployment evaluation used the wrong region.' }
        $parameterIndex = [Array]::IndexOf($args, '--parameters')
        $testState.platformParameters = Get-Content -Raw -LiteralPath $args[$parameterIndex + 1].TrimStart('@') | ConvertFrom-Json
        return '{}'
    }
    throw 'Unexpected Azure CLI operation in the isolated bootstrap test.'
}
function Invoke-RestMethod {
    param($Method, $Uri, $Headers, $ContentType, $Body)
    $testState.graphCalls.Add("$Method $Uri")
    if ($Uri -like '*/organization?*') {
        return @{ value = @(@{ verifiedDomains = @(@{ isInitial = $true; name = 'synthetic.onmicrosoft.com' }) }) }
    }
    if ($Uri -like '*/me?*') { return @{ id = 'synthetic-owner' } }
    if ($Method -eq 'GET' -and $Uri -match "/applications\?.*displayName eq 'teams-reference-dev-(web|sync|events|deploy)'") {
        $purpose = $Matches[1]
        return @{ value = @(@{ id = "$purpose-object"; appId = "$purpose-client"; tags = @('teams-reference:teams-reference-dev') }) }
    }
    if ($Method -eq 'PATCH' -and $Uri -like '*/applications/events-object') { return }
    if ($Method -eq 'GET' -and $Uri -match "/servicePrincipals\?.*appId eq '(web|sync|events|deploy)-client'") {
        return @{ value = @(@{ id = "$($Matches[1])-principal" }) }
    }
    if ($Uri -like '*/applications/deploy-object/federatedIdentityCredentials') {
        if ($Method -eq 'GET') { return @{ value = @() } }
        if ($Method -eq 'POST') {
            throw 'Synthetic Authorization_RequestDenied: credential creation is not permitted.'
        }
    }
    throw "Unexpected Graph operation in the isolated test: $Method $Uri"
}

try {
    $inventory = Join-Path $temporary 'identity.json'
    $caught = $false
    try {
        & (Join-Path $root 'scripts\entra\Bootstrap-Dev.ps1') -TenantId $tenant -SubscriptionId $subscription `
            -InventoryPath $inventory -CreateTestUsers -Apply -Confirm:$false
    } catch {
        if ($_.Exception.Message -notlike 'Synthetic Authorization_RequestDenied*') { throw }
        $caught = $true
    }
    if (-not $caught) { throw 'Bootstrap did not propagate the directory permission failure.' }
    $saved = Get-Content -Raw -LiteralPath $inventory | ConvertFrom-Json
    if ($saved.bootstrapComplete -ne $false -or $saved.applications.PSObject.Properties.Name.Count -ne 4 -or $saved.users.Count -ne 0) {
        throw 'Partial bootstrap inventory was not preserved safely.'
    }
    $calls = $testState.accountCalls.Count
    & (Join-Path $root 'scripts\New-DevPlatform.ps1') -SubscriptionId $subscription -InventoryPath $inventory
    if ($testState.platformParameters.parameters.location.value -ne 'swedencentral' -or
        $testState.platformParameters.parameters.cosmosLocation.value -ne 'swedencentral' -or
        $testState.platformParameters.parameters.PSObject.Properties.Name -contains 'fixtureActorIds') {
        throw 'Infrastructure evaluation did not preserve Sweden location or leaked incomplete fixture identities.'
    }
    if ($testState.accountCalls.Count -ne $calls + 3) { throw 'Infrastructure evaluation performed unexpected Azure operations.' }
    & (Join-Path $root 'scripts\New-DevPlatform.ps1') -SubscriptionId $subscription -InventoryPath $inventory `
        -ExistingServiceBusName 'existing-standard'
    if ($testState.platformParameters.parameters.existingServiceBusName.value -cne 'existing-standard') {
        throw 'Infrastructure evaluation did not preserve the selected existing Standard namespace.'
    }
    $calls = $testState.accountCalls.Count
    $testState.networkFeature = 'NotRegistered'
    try {
        & (Join-Path $root 'scripts\New-DevPlatform.ps1') -SubscriptionId $subscription -InventoryPath $inventory
        throw 'An unregistered network prerequisite was incorrectly accepted.'
    } catch {
        if ($_.Exception.Message -ne 'Register Microsoft.Network/AllowBringYourOwnPublicIpAddress and re-register Microsoft.Network before provisioning Container Apps.') { throw }
    }
    if ($testState.accountCalls.Count -ne $calls + 2) { throw 'Missing network prerequisites reached a deployment operation.' }
    $testState.networkFeature = 'Registered'
    $calls = $testState.accountCalls.Count

    $before = $testState.graphCalls.Count
    try {
        & (Join-Path $root 'scripts\entra\Bootstrap-Dev.ps1') -TenantId '33333333-3333-3333-3333-333333333333' `
            -SubscriptionId $subscription -InventoryPath $inventory -Apply -Confirm:$false
        throw 'A cross-tenant inventory was incorrectly accepted.'
    } catch {
        if ($_.Exception.Message -ne 'Existing identity inventory belongs to a different tenant or prefix.') { throw }
    }
    if ($testState.graphCalls.Count -ne $before -or $testState.accountCalls.Count -ne $calls) {
        throw 'Cross-tenant inventory validation occurred after an external call.'
    }
    Write-Output 'Passed isolated platform checks: explicit subscription, partial checkpoint, Sweden region, network prerequisite and early tenant mismatch denial. Application release guards are covered by DevDeployment.Tests.ps1.'
} finally {
    Remove-Item -LiteralPath (Join-Path $temporary 'identity.json') -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue
}
