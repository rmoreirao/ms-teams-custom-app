[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$InventoryPath,
    [Parameter(Mandatory)][string]$PublicOrigin,
    [Parameter(Mandatory)][guid]$ApiIdentityPrincipalId,
    [Parameter(Mandatory)][guid]$PubSubPrincipalId
)
$ErrorActionPreference = 'Stop'
if (-not $PublicOrigin.StartsWith('https://') -or ([uri]$PublicOrigin).AbsolutePath -ne '/') { throw 'Use the approved HTTPS origin without a path.' }
$inventory = Get-Content -Raw -LiteralPath $InventoryPath | ConvertFrom-Json
if (-not $PSCmdlet.ShouldProcess($inventory.tenantId, 'Finalize web redirects, managed-identity trust and PubSub callback app role')) { return }
$account = az account show --output json | ConvertFrom-Json
if ($LASTEXITCODE -or $account.tenantId -ne $inventory.tenantId) { throw 'Wrong tenant.' }
$token = az account get-access-token --resource-type ms-graph --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Graph authentication failed.' }
$headers = @{ Authorization = "Bearer $($token.accessToken)" }
function Graph([string]$Method, [string]$Path, $Body = $null) {
    $args = @{ Method = $Method; Uri = "https://graph.microsoft.com/v1.0/$Path"; Headers = $headers; ContentType = 'application/json' }
    if ($null -ne $Body) { $args.Body = $Body | ConvertTo-Json -Depth 10 -Compress }
    Invoke-RestMethod @args
}
$origin = $PublicOrigin.TrimEnd('/')
Graph PATCH "applications/$($inventory.applications.web.objectId)" @{ web = @{ redirectUris = @("$origin/auth/callback"); logoutUrl = "$origin/" } } | Out-Null
$credentials = Graph GET "applications/$($inventory.applications.web.objectId)/federatedIdentityCredentials"
if ('api-managed-identity' -notin $credentials.value.name) {
    Graph POST "applications/$($inventory.applications.web.objectId)/federatedIdentityCredentials" @{
        name = 'api-managed-identity'; issuer = "https://login.microsoftonline.com/$($inventory.tenantId)/v2.0"
        subject = $ApiIdentityPrincipalId.ToString(); audiences = @('api://AzureADTokenExchange')
    } | Out-Null
} elseif (($credentials.value | Where-Object name -eq 'api-managed-identity').subject -ne $ApiIdentityPrincipalId.ToString()) {
    throw 'Existing web federation trusts a different managed identity; explicit rotation is required.'
}
$assignments = Graph GET "servicePrincipals/$($inventory.applications.events.principalId)/appRoleAssignedTo"
if (-not ($assignments.value | Where-Object { $_.principalId -eq $PubSubPrincipalId.ToString() -and $_.appRoleId -eq '277c2a98-926e-469c-8bd4-eb51680d6377' })) {
    Graph POST "servicePrincipals/$($inventory.applications.events.principalId)/appRoleAssignedTo" @{
        principalId = $PubSubPrincipalId.ToString(); resourceId = $inventory.applications.events.principalId
        appRoleId = '277c2a98-926e-469c-8bd4-eb51680d6377'
    } | Out-Null
}
$headers.Clear()
Write-Output 'Dev identity configuration completed. No Graph message consent was granted.'
