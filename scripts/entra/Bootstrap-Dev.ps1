[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$TenantId,
    [guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$InventoryPath,
    [ValidatePattern('^[a-z0-9-]{3,32}$')][string]$Prefix = 'teams-reference-dev',
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository = 'rmoreirao/ms-teams-custom-app',
    [switch]$CreateTestUsers,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')) + [IO.Path]::DirectorySeparatorChar
if ([IO.Path]::GetFullPath($InventoryPath).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Identity and credential inventories must be outside the repository.'
}
$users = @()
if (Test-Path -LiteralPath $InventoryPath) {
    $previous = Get-Content -Raw -LiteralPath $InventoryPath | ConvertFrom-Json
    if ($previous.tenantId -ne $TenantId.ToString() -or $previous.prefix -ne $Prefix) {
        throw 'Existing identity inventory belongs to a different tenant or prefix.'
    }
    $users = @($previous.users)
    if (@($users | Where-Object persona -notin @('alex', 'sam', 'jordan')).Count) {
        throw 'Inventory contains a retired or unknown fixture persona. Reconcile it explicitly before bootstrap.'
    }
}
if (-not $Apply) {
    Write-Output 'Plan: create/reuse four tagged application registrations and their service principals; configure GitHub dev-environment federation. Optional: create three dedicated unlicensed fixture users (alex, sam, jordan). No Graph message consent or licence purchase.'
    return
}
if (-not $PSCmdlet.ShouldProcess("$TenantId / $Prefix", 'Provision dev application identities and requested test users')) { return }
$directory = Split-Path ([IO.Path]::GetFullPath($InventoryPath)) -Parent
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$accountArguments = if ($PSBoundParameters.ContainsKey('SubscriptionId')) { @('--subscription', $SubscriptionId.ToString()) } else { @() }
$account = az account show @accountArguments --output json | ConvertFrom-Json
if ($LASTEXITCODE -or $account.tenantId -ne $TenantId.ToString()) { throw 'Sign in to the explicitly selected tenant first.' }
$token = az account get-access-token @accountArguments --resource-type ms-graph --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Graph authentication failed.' }
$headers = @{ Authorization = "Bearer $($token.accessToken)" }
function Graph([string]$Method, [string]$Path, $Body = $null) {
    $args = @{ Method = $Method; Uri = "https://graph.microsoft.com/v1.0/$Path"; Headers = $headers; ContentType = 'application/json' }
    if ($null -ne $Body) { $args.Body = $Body | ConvertTo-Json -Depth 12 -Compress }
    Invoke-RestMethod @args
}
$organization = Graph GET 'organization?$select=verifiedDomains'
$domain = ($organization.value[0].verifiedDomains | Where-Object isInitial | Select-Object -First 1).name
if (-not $domain) { throw 'Initial tenant domain was not available.' }
$owner = Graph GET 'me?$select=id'
$tag = "teams-reference:$Prefix"
$apps = @{}
function Save-Inventory([bool]$Complete = $false) {
    @{
        schemaVersion = 1
        tenantId = $TenantId.ToString()
        prefix = $Prefix
        applications = $apps
        users = @($users)
        bootstrapComplete = $Complete
    } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $InventoryPath -Encoding utf8
}
foreach ($purpose in @('web', 'sync', 'events', 'deploy')) {
    $name = "$Prefix-$purpose"
    $existing = Graph GET "applications?`$filter=displayName eq '$name'"
    if ($existing.value.Count -gt 1) { throw "Ambiguous application name: $name" }
    if ($existing.value.Count -eq 1) {
        $app = $existing.value[0]
        if ($tag -notin $app.tags) { throw "Existing application $name is not owned by this bootstrap tag." }
    } else {
        $body = @{ displayName = $name; signInAudience = 'AzureADMyOrg'; tags = @($tag); api = @{ requestedAccessTokenVersion = 2 } }
        if ($purpose -eq 'web') { $body.web = @{ redirectUris = @(); implicitGrantSettings = @{ enableAccessTokenIssuance = $false; enableIdTokenIssuance = $false } } }
        if ($purpose -eq 'events') { $body.appRoles = @(@{
            id = '277c2a98-926e-469c-8bd4-eb51680d6377'; displayName = 'PubSub callback'; description = 'Authenticate the designated Web PubSub upstream service.'
            value = 'PubSub.Callback'; allowedMemberTypes = @('Application'); isEnabled = $true
        }) }
        $app = Graph POST 'applications' $body
        $owners = Graph GET "applications/$($app.id)/owners?`$select=id"
        if ($owner.id -notin $owners.value.id) {
            Graph POST "applications/$($app.id)/owners/`$ref" @{ '@odata.id' = "https://graph.microsoft.com/v1.0/directoryObjects/$($owner.id)" } | Out-Null
        }
    }
    if ($purpose -eq 'events') { Graph PATCH "applications/$($app.id)" @{ identifierUris = @("api://$($app.appId)") } | Out-Null }
    $principals = Graph GET "servicePrincipals?`$filter=appId eq '$($app.appId)'"
    $principal = if ($principals.value.Count) { $principals.value[0] } else { Graph POST 'servicePrincipals' @{ appId = $app.appId } }
    $apps[$purpose] = @{ objectId = $app.id; clientId = $app.appId; principalId = $principal.id }
    Save-Inventory
}
$federation = Graph GET "applications/$($apps.deploy.objectId)/federatedIdentityCredentials"
if ('github-dev' -notin $federation.value.name) {
    Graph POST "applications/$($apps.deploy.objectId)/federatedIdentityCredentials" @{
        name = 'github-dev'; issuer = 'https://token.actions.githubusercontent.com'
        subject = "repo:${Repository}:environment:dev"; audiences = @('api://AzureADTokenExchange')
    } | Out-Null
} elseif (($federation.value | Where-Object name -eq 'github-dev').subject -ne "repo:${Repository}:environment:dev") {
    throw 'Existing GitHub federation belongs to another repository or environment.'
}
if ($CreateTestUsers) {
    if (-not $IsWindows) { throw 'This bootstrap stores initial credentials with Windows DPAPI. Run on Windows or provide a separately approved secret-storage implementation.' }
    $directory = Split-Path ([IO.Path]::GetFullPath($InventoryPath)) -Parent
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $users = @()
    foreach ($persona in @('alex', 'sam', 'jordan')) {
        $upn = "$Prefix-$persona@$domain"
        $existing = Graph GET "users?`$filter=userPrincipalName eq '$upn'&`$select=id,department,userPrincipalName"
        if ($existing.value.Count) {
            $user = $existing.value[0]
            if ($user.department -ne $tag) { throw "Existing user $upn is not a tagged fixture account." }
        } else {
            $password = 'Aa1!' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(30))
            $user = Graph POST 'users' @{
                displayName = "Reference Test $persona"; userPrincipalName = $upn; mailNickname = "$Prefix-$persona"
                department = $tag; accountEnabled = $true
                passwordProfile = @{ password = $password; forceChangePasswordNextSignIn = $true }
            }
            $secure = ConvertTo-SecureString $password -AsPlainText -Force
            [pscredential]::new($upn, $secure) | Export-Clixml (Join-Path $directory "$persona.credential.xml")
            $password = $null
        }
        $users += @{ persona = $persona; id = $user.id; upn = $upn }
        Save-Inventory
    }
}
Graph PATCH "servicePrincipals/$($apps.web.principalId)" @{ appRoleAssignmentRequired = $true } | Out-Null
$assignments = Graph GET "servicePrincipals/$($apps.web.principalId)/appRoleAssignedTo?`$select=principalId"
foreach ($user in $users) {
    if ($user.id -notin $assignments.value.principalId) {
        Graph POST "servicePrincipals/$($apps.web.principalId)/appRoleAssignedTo" @{
            principalId = $user.id; resourceId = $apps.web.principalId; appRoleId = '00000000-0000-0000-0000-000000000000'
        } | Out-Null
    }
}
Save-Inventory -Complete $true
Write-Output "Identity inventory saved privately to $InventoryPath. No licences or Teams message permissions were assigned."
$headers.Clear()
$token = $null
