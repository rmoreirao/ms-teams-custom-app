[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][guid]$SubscriptionId,
    [Parameter(Mandatory)][string]$InventoryPath,
    [Parameter(Mandatory)][string]$CheckpointPath,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')) + [IO.Path]::DirectorySeparatorChar
$checkpointFile = [IO.Path]::GetFullPath($CheckpointPath)
if ($checkpointFile.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Development Teams inventories must remain outside the repository.' }
$inventory = Get-Content -Raw -LiteralPath $InventoryPath | ConvertFrom-Json
if ($inventory.users.Count -ne 3 -or @($inventory.users.id | Sort-Object -Unique).Count -ne 3) {
    throw 'Exactly three approved fixture users are required.'
}
$account = az account show --subscription $SubscriptionId --output json | ConvertFrom-Json
if ($LASTEXITCODE -or $account.tenantId -ne $inventory.tenantId) { throw 'Selected subscription and identity tenant differ.' }
if (-not $Apply) {
    Write-Output 'Plan: create one tagged private team/channel and group chat for three fixture users. Use a one-hour temporary Team.Create/Channel.ReadBasic.All/Chat.Create provisioner, removed afterward. No Azure resources, licences or runtime app-only message permissions.'
    return
}
if (-not $PSCmdlet.ShouldProcess($inventory.tenantId, 'Create the approved synthetic team/channel/group chat and remove the temporary creation-only provisioner afterward')) { return }
$token = az account get-access-token --subscription $SubscriptionId --resource-type ms-graph --output json | ConvertFrom-Json
if ($LASTEXITCODE) { throw 'Graph authentication failed.' }
$headers = @{ Authorization = "Bearer $($token.accessToken)" }
function Graph([string]$Method, [string]$Path, $Body = $null, $Auth = $headers) {
    $arguments = @{ Method=$Method; Uri="https://graph.microsoft.com/v1.0/$Path"; Headers=$Auth; ContentType='application/json' }
    if ($null -ne $Body) { $arguments.Body = $Body | ConvertTo-Json -Depth 15 -Compress }
    Invoke-RestMethod @arguments
}
function Url64([byte[]]$Value) { [Convert]::ToBase64String($Value).TrimEnd('=').Replace('+','-').Replace('/','_') }
$scope = if (Test-Path -LiteralPath $checkpointFile) {
    Get-Content -Raw -LiteralPath $checkpointFile | ConvertFrom-Json -AsHashtable
} else {
    $nonce = [guid]::NewGuid().ToString('N').Substring(0,8)
    @{
        schemaVersion=1;tenantId=$inventory.tenantId;actorIds=@($inventory.users.id);nonce=$nonce
        notBefore=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('O')
        teamTitle="Teams Reference Validation $nonce";channelTitle='Teams Reference Validation Channel'
        chatTitle="Teams Reference Validation Chat $nonce"
        teamDispatchStarted=$false;chatDispatchStarted=$false
        teamId=$null;channelId=$null;chatId=$null;provisionerObjectId=$null;complete=$false
    }
}
function Save {
    New-Item -ItemType Directory -Path (Split-Path $checkpointFile -Parent) -Force | Out-Null
    $scope | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $checkpointFile -Encoding utf8
}
if ($scope.tenantId -ne $inventory.tenantId -or
    ((@($scope.actorIds | Sort-Object) -join ',') -ne (@($inventory.users.id | Sort-Object) -join ','))) {
    throw 'Existing checkpoint belongs to another tenant or actor set.'
}
if ($scope.complete) { Write-Output 'Dedicated Development Teams scope already complete; no new resources created.'; $headers.Clear(); return }
if (($scope.teamDispatchStarted -and -not $scope.teamId) -or ($scope.chatDispatchStarted -and -not $scope.chatId)) {
    throw 'A creation outcome is uncertain. Inspect the original tagged resource before any new POST.'
}
if ($scope.provisionerObjectId) { throw 'An earlier temporary provisioner requires explicit cleanup review.' }
$rsa=$null; $certificate=$null; $creationHeaders=$null
try {
    $rsa=[Security.Cryptography.RSA]::Create(2048)
    $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new(
        "CN=TeamsRefPilot-$($scope.nonce)",$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $certificate=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5),[DateTimeOffset]::UtcNow.AddHours(1))
    $app=Graph POST 'applications' @{
        displayName="teams-reference-pilot-provisioner-$($scope.nonce)";signInAudience='AzureADMyOrg'
        tags=@("teams-reference:pilot:$($scope.nonce)")
        keyCredentials=@(@{
            keyId=[guid]::NewGuid().ToString();type='AsymmetricX509Cert';usage='Verify';displayName='One-hour synthetic Teams setup'
            key=[Convert]::ToBase64String($certificate.RawData)
            startDateTime=$certificate.NotBefore.ToUniversalTime().ToString('O')
            endDateTime=$certificate.NotAfter.ToUniversalTime().ToString('O')
        })
    }
    $scope.provisionerObjectId=$app.id; Save
    $sp=Graph POST 'servicePrincipals' @{appId=$app.appId}
    $resource=(Graph GET "servicePrincipals?`$filter=appId eq '00000003-0000-0000-c000-000000000000'").value[0]
    foreach ($permission in @('Team.Create','Channel.ReadBasic.All','Chat.Create')) {
        $role=@($resource.appRoles | Where-Object {$_.value -eq $permission -and $_.isEnabled -and 'Application' -in $_.allowedMemberTypes})
        if ($role.Count -ne 1) { throw "Setup permission $permission is unavailable or ambiguous." }
        Graph POST "servicePrincipals/$($resource.id)/appRoleAssignedTo" @{
            principalId=$sp.id;resourceId=$resource.id;appRoleId=$role[0].id
        } | Out-Null
    }
    Start-Sleep -Seconds 15
    $audience="https://login.microsoftonline.com/$($inventory.tenantId)/oauth2/v2.0/token"
    $now=[DateTimeOffset]::UtcNow
    $head=Url64 ([Text.Encoding]::UTF8.GetBytes((@{alg='RS256';typ='JWT';x5t=(Url64 ([Security.Cryptography.SHA1]::HashData($certificate.RawData)))} | ConvertTo-Json -Compress)))
    $payload=Url64 ([Text.Encoding]::UTF8.GetBytes((@{
        aud=$audience;iss=$app.appId;sub=$app.appId;jti=[guid]::NewGuid().ToString()
        nbf=$now.AddMinutes(-1).ToUnixTimeSeconds();exp=$now.AddMinutes(5).ToUnixTimeSeconds()
    } | ConvertTo-Json -Compress)))
    $signed="$head.$payload"
    $signature=Url64 ($rsa.SignData([Text.Encoding]::ASCII.GetBytes($signed),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1))
    $access=Invoke-RestMethod $audience -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
        client_id=$app.appId;scope='https://graph.microsoft.com/.default';grant_type='client_credentials'
        client_assertion_type='urn:ietf:params:oauth:client-assertion-type:jwt-bearer';client_assertion="$signed.$signature"
    }
    $creationHeaders=@{Authorization="Bearer $($access.access_token)"}
    $access=$null; $signed=$null; $signature=$null
    Write-Output 'Temporary creation-only provisioner configured.'
    if (-not $scope.teamId) {
        $scope.teamDispatchStarted=$true; Save
        $body=@{
            'template@odata.bind'="https://graph.microsoft.com/v1.0/teamsTemplates('standard')"
            displayName=$scope.teamTitle;description="Synthetic validation only; teams-reference:$($scope.nonce)"
            visibility='private';firstChannelName=$scope.channelTitle
            members=@($inventory.users | Select-Object -First 1 | ForEach-Object {@{
                '@odata.type'='#microsoft.graph.aadUserConversationMember';roles=@('owner')
                'user@odata.bind'="https://graph.microsoft.com/v1.0/users('$($_.id)')"
            }})
        } | ConvertTo-Json -Depth 12 -Compress
        try {
            $reply=Invoke-WebRequest 'https://graph.microsoft.com/v1.0/teams' -Method Post -Headers $creationHeaders -ContentType 'application/json' -Body $body
        } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
            $status=[int]$_.Exception.Response.StatusCode
            if ($status -ge 400 -and $status -lt 500 -and $status -ne 408) { $scope.teamDispatchStarted=$false; Save }
            throw
        }
        if ([int]$reply.StatusCode -ne 202) { throw 'Team creation did not return accepted evidence.' }
        $reference=@($reply.Headers['Location']) -join ''
        $scope.teamOperationReference=$reference
        $id=[regex]::Match($reference,'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
        if (-not $id.Success) { throw 'No exact team reference was returned; do not repeat the POST.' }
        $scope.teamId=$id.Value; Save
        Write-Output 'Team creation accepted; waiting for the tagged channel.'
    }
    if (-not $scope.channelId) {
        for ($attempt=0;$attempt -lt 40;$attempt++) {
            try {
                $channels=Graph GET "teams/$($scope.teamId)/channels" $null $creationHeaders
                $found=@($channels.value | Where-Object displayName -eq $scope.channelTitle)
                if ($found.Count -eq 1) { $scope.channelId=$found[0].id; Save; break }
            } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
                if ([int]$_.Exception.Response.StatusCode -notin @(404,409,503)) { throw }
            }
            Start-Sleep -Seconds 10
        }
        if (-not $scope.channelId) { throw 'Channel provisioning is not ready; resume the saved team instead of creating another.' }
    }
    $members=Graph GET "groups/$($scope.teamId)/members?`$select=id"
    if (@($members.value.id | Where-Object {$_ -notin $scope.actorIds}).Count) {
        throw 'Team membership differs from the three approved identities.'
    }
    foreach ($actorId in $scope.actorIds) {
        if ($actorId -notin $members.value.id) {
            Graph POST "groups/$($scope.teamId)/members/`$ref" @{
                '@odata.id'="https://graph.microsoft.com/v1.0/directoryObjects/$actorId"
            } | Out-Null
        }
    }
    for ($attempt=0;$attempt -lt 6;$attempt++) {
        $members=Graph GET "groups/$($scope.teamId)/members?`$select=id"
        if (-not @($scope.actorIds | Where-Object {$_ -notin $members.value.id}).Count) { break }
        Start-Sleep -Seconds 5
    }
    if (@($scope.actorIds | Where-Object {$_ -notin $members.value.id}).Count) { throw 'Group membership propagation is not ready.' }
    if (-not $scope.chatId) {
        $scope.chatDispatchStarted=$true; Save
        try {
            $chat=Graph POST 'chats' @{
                chatType='group';topic=$scope.chatTitle
                members=@($inventory.users | ForEach-Object {@{
                    '@odata.type'='#microsoft.graph.aadUserConversationMember';roles=@('owner')
                    'user@odata.bind'="https://graph.microsoft.com/v1.0/users('$($_.id)')"
                }})
            } $creationHeaders
        } catch [Microsoft.PowerShell.Commands.HttpResponseException] {
            $status=[int]$_.Exception.Response.StatusCode
            if ($status -ge 400 -and $status -lt 500 -and $status -ne 408) { $scope.chatDispatchStarted=$false; Save }
            throw
        }
        if (-not $chat.id) { throw 'Chat creation lacks an ID; do not repeat the uncertain POST.' }
        $scope.chatId=$chat.id; Save
    }
    $scope.complete=$true; Save
    Write-Output 'Dedicated three-user team/channel/group chat created. IDs saved privately; no messages sent.'
} finally {
    try {
        if ($scope.provisionerObjectId) {
            $owned=Graph GET "applications/$($scope.provisionerObjectId)?`$select=id,displayName,tags"
            if ($owned.displayName -ne "teams-reference-pilot-provisioner-$($scope.nonce)" -or
                "teams-reference:pilot:$($scope.nonce)" -notin $owned.tags) { throw 'Temporary provisioner ownership mismatch; cleanup refused.' }
            Graph DELETE "applications/$($scope.provisionerObjectId)" | Out-Null
            $scope.provisionerObjectId=$null; Save
            Write-Output 'Temporary creation-only provisioner removed.'
        }
    } finally {
        if ($creationHeaders) { $creationHeaders.Clear() }
        if ($certificate) { $certificate.Dispose() }
        if ($rsa) { $rsa.Dispose() }
        $headers.Clear();$token=$null
    }
}
