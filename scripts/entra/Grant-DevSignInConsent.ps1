[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$InventoryPath,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$inventory = Get-Content -Raw -LiteralPath $InventoryPath | ConvertFrom-Json
if ($inventory.bootstrapComplete -ne $true -or $inventory.users.Count -ne 4 -or
    @($inventory.users.id | Sort-Object -Unique).Count -ne 4) {
    throw 'Complete the four-user identity bootstrap before preparing sign-in consent.'
}
$account = az account show --subscription $SubscriptionId --output json | ConvertFrom-Json
if ($LASTEXITCODE -or $account.tenantId -ne $inventory.tenantId) { throw 'Subscription and identity inventory tenant do not match.' }
$token = az account get-access-token --subscription $SubscriptionId --resource-type ms-graph --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Graph authentication failed.' }
$headers = @{ Authorization = "Bearer $($token.accessToken)" }
function Graph([string]$Method, [string]$Path, $Body = $null) {
    $arguments = @{ Method = $Method; Uri = "https://graph.microsoft.com/v1.0/$Path"; Headers = $headers; ContentType = 'application/json' }
    if ($null -ne $Body) { $arguments.Body = $Body | ConvertTo-Json -Depth 8 -Compress }
    Invoke-RestMethod @arguments
}
function Read-Grants {
    $filter = [Uri]::EscapeDataString("clientId eq '$($web.id)' and resourceId eq '$($resource.id)'")
    $response = Graph GET "oauth2PermissionGrants?`$filter=$filter"
    if ($response.'@odata.nextLink') { throw 'Unexpected consent record count; review grants before applying.' }
    @($response.value)
}
$scopes = @('openid', 'profile', 'offline_access')
try {
    $application = Graph GET "applications/$($inventory.applications.web.objectId)?`$select=id,appId,tags"
    if ($application.appId -ne $inventory.applications.web.clientId -or
        "teams-reference:$($inventory.prefix)" -notin $application.tags) { throw 'Web application does not match the tagged bootstrap inventory.' }
    $web = Graph GET "servicePrincipals/$($inventory.applications.web.principalId)?`$select=id,appId,appRoleAssignmentRequired"
    if ($web.appId -ne $application.appId -or $web.appRoleAssignmentRequired -ne $true) {
        throw 'The expected web service principal must require explicit user assignment.'
    }
    $filter = [Uri]::EscapeDataString("appId eq '00000003-0000-0000-c000-000000000000'")
    $resources = Graph GET "servicePrincipals?`$filter=$filter&`$select=id,oauth2PermissionScopes"
    if ($resources.value.Count -ne 1) { throw 'Microsoft Graph resource principal is missing or ambiguous.' }
    $resource = $resources.value[0]
    foreach ($scope in $scopes) {
        if (-not ($resource.oauth2PermissionScopes | Where-Object { $_.value -eq $scope -and $_.isEnabled })) {
            throw "The standard sign-in scope $scope is not enabled on the expected resource."
        }
    }
    $assignments = Graph GET "servicePrincipals/$($web.id)/appRoleAssignedTo?`$select=principalId"
    if ($assignments.'@odata.nextLink') { throw 'Review the application assignment inventory before granting consent.' }
    foreach ($user in $inventory.users) {
        if ($user.id -notin $assignments.value.principalId) { throw 'Every fixture user must already be assigned to the web application.' }
    }
    $existing = @(Read-Grants)
    if (@($existing | Where-Object { $_.consentType -ne 'Principal' -or $_.principalId -notin $inventory.users.id }).Count) {
        throw 'Existing broader or unexpected consent requires explicit review; nothing was changed.'
    }
    $plan = @()
    foreach ($user in $inventory.users) {
        $grants = @($existing | Where-Object principalId -eq $user.id)
        if ($grants.Count -gt 1) { throw 'Duplicate consent records require review.' }
        if ($grants.Count -and @($grants[0].scope.Split(' ', [StringSplitOptions]::RemoveEmptyEntries) | Where-Object { $_ -notin $scopes }).Count) {
            throw 'Existing non-sign-in permissions require review; this command will not alter them.'
        }
        $current = if ($grants.Count) { $grants[0] } else { $null }
        $complete = $current -and @($scopes | Where-Object { $_ -notin $current.scope.Split(' ') }).Count -eq 0
        $plan += @{ user = $user.id; existing = $current; complete = $complete }
    }
    if (-not $Apply) {
        Write-Output 'Plan only: consent for four assigned users, scopes openid profile offline_access; no tenant-wide grant or message permissions. Use -Apply only after explicit approval.'
        return
    }
    if (-not $PSCmdlet.ShouldProcess("$($inventory.tenantId) / $($application.appId)", 'Grant only openid profile offline_access for the four assigned fixture users')) { return }
    foreach ($item in $plan) {
        if ($item.complete) { continue }
        if ($item.existing) {
            Graph PATCH "oauth2PermissionGrants/$($item.existing.id)" @{ scope = $scopes -join ' ' } | Out-Null
        } else {
            Graph POST 'oauth2PermissionGrants' @{
                clientId = $web.id
                resourceId = $resource.id
                consentType = 'Principal'
                principalId = $item.user
                scope = $scopes -join ' '
            } | Out-Null
        }
    }
    $verified = @(Read-Grants)
    if ($verified.Count -ne 4 -or @($verified | Where-Object { $_.consentType -ne 'Principal' -or $_.principalId -notin $inventory.users.id }).Count) {
        throw 'Unexpected consent records appeared during verification; review them explicitly.'
    }
    foreach ($user in $inventory.users) {
        $grant = @($verified | Where-Object { $_.consentType -eq 'Principal' -and $_.principalId -eq $user.id })
        if ($grant.Count -ne 1 -or (($grant[0].scope.Split(' ') | Sort-Object) -join ' ') -ne (($scopes | Sort-Object) -join ' ')) {
            throw 'Sign-in consent verification failed; inspect the existing records before retrying.'
        }
    }
    Write-Output 'Verified user-specific sign-in consent for four fixture users. No tenant-wide or Teams message permissions were granted.'
} finally {
    $headers.Clear()
    $token = $null
}
