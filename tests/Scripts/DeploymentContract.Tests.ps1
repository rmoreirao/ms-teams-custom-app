$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$modulePath = Join-Path $root 'scripts\DeploymentContract.psm1'
Import-Module $modulePath -Force
function Expect-Denial([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null }
    catch { if ($_.Exception.Message -notlike "*$Message*") { throw }; $failed = $true }
    if (-not $failed) { throw "Expected deployment contract denial: $Message" }
}
$live = @{ status = 'ready'; mode = 'live'; role = 'Api'; admission = $false }
Test-AppDeploymentContract -Health $live -AnonymousStatus 401
foreach ($mode in @('simulator', 'graph-pilot', '', 'Live')) {
    $live.mode = $mode
    Expect-Denial { Test-AppDeploymentContract -Health $live -AnonymousStatus 401 } 'Live-only deployment mode'
}
$live.mode = 'live'; $live.status = 'preparing'
Expect-Denial { Test-AppDeploymentContract -Health $live -AnonymousStatus 401 } 'Live-only deployment mode'
$live.status = 'ready'
foreach ($status in @(200, 302, 403, 404, 500)) {
    Expect-Denial { Test-AppDeploymentContract -Health $live -AnonymousStatus $status } 'Anonymous API'
}
$live.admission = $true
Expect-Denial { Test-AppDeploymentContract -Health $live -AnonymousStatus 401 } 'preserved rollout stage'
Test-AppDeploymentContract -Health $live -AnonymousStatus 401 -ExpectAdmission $true
$live.admission = 'true'
Expect-Denial { Test-AppDeploymentContract -Health $live -AnonymousStatus 401 -ExpectAdmission $true } 'preserved rollout stage'
$live.admission = $false; $live.role = 'Receiver'
Expect-Denial { Test-AppDeploymentContract -Health $live -AnonymousStatus 401 } 'Live API role'
$live.role = 'Api'
Expect-Denial { Test-AppDeploymentContract -Health $live -AnonymousStatus 401 -ExpectedMode Simulator } 'ValidateSet'

$tenant = '11111111-1111-1111-1111-111111111111'
$client = '22222222-2222-2222-2222-222222222222'
$origin = 'https://example.test'
$canonical = "https://login.microsoftonline.com/$tenant/oauth2/v2.0/authorize?client_id=$client&redirect_uri=$([uri]::EscapeDataString("$origin/auth/callback"))"
$global:DeploymentContractHttpMock = @{
    paths = [Collections.Generic.List[string]]::new()
    shell = '<div id="root"></div><script src="/assets/index.js"></script>'
    assetStatus = 200; readyStatus = 200; health = $live; anonymous = 401; signinStatus = 302; redirect = $canonical
}
& (Get-Module DeploymentContract) {
    function script:Get-AppDeploymentResponse($Client, [string]$Path) {
        $state = $global:DeploymentContractHttpMock
        $state.paths.Add($Path)
        switch ($Path) {
            '/' { return @{ status = 200; body = $state.shell } }
            '/assets/index.js' { return @{ status = $state.assetStatus; body = 'compiled frontend' } }
            '/health/ready' { return @{ status = $state.readyStatus; body = ($state.health | ConvertTo-Json) } }
            '/api/session' { return @{ status = $state.anonymous } }
            '/auth/signin' { return @{ status = $state.signinStatus; location = [uri]$state.redirect } }
            default { throw "Unexpected request in read-only deployment smoke: $Path" }
        }
    }
}
try {
    $arguments = @{ PublicOrigin = $origin; TenantId = $tenant; WebClientId = $client }
    $smoke = Join-Path $root 'scripts\Test-DeployedApp.ps1'
    $result = & $smoke @arguments
    if ($result.status -ne 'passed' -or $result.mode -ne 'live' -or
        $result.authenticatedJourneys -ne 'not_tested' -or $result.graphIntegration -ne 'not_tested' -or
        $result.PSObject.Properties['localAuth'] -or $result.PSObject.Properties['simulated'] -or
        ($global:DeploymentContractHttpMock.paths -join ',') -cne '/,/assets/index.js,/health/ready,/api/session,/auth/signin') {
        throw 'Smoke did not enforce the Live-only GET contract or claimed authenticated/Graph acceptance.'
    }
    foreach ($bad in @(
        'http://example.test', 'https://localhost', 'https://example.test/path', 'https://example.test?query=1',
        'https://user@example.test', 'https://example.test:444', 'https://example.test/#fragment'
    )) {
        Expect-Denial { Invoke-AppDeploymentSmoke -PublicOrigin $bad -TenantId $tenant -WebClientId $client } 'canonical public HTTPS'
    }
    foreach ($bad in @(
        $canonical.Replace('login.microsoftonline.com', 'untrusted.test'),
        $canonical.Replace($tenant, $client),
        $canonical.Replace('https://', 'http://'),
        $canonical.Replace("client_id=$client", "client_id=$tenant"),
        $canonical.Replace('%2Fauth%2Fcallback', '%2Fauth%2Fwrong'),
        '/relative-authority'
    )) {
        $global:DeploymentContractHttpMock.redirect = $bad
        Expect-Denial { & $smoke @arguments } 'Sign-in'
    }
    $global:DeploymentContractHttpMock.redirect = $canonical
    $global:DeploymentContractHttpMock.signinStatus = 200
    Expect-Denial { & $smoke @arguments } 'Entra sign-in'
    $global:DeploymentContractHttpMock.signinStatus = 302
    $global:DeploymentContractHttpMock.anonymous = 200
    Expect-Denial { & $smoke @arguments } 'Anonymous API'
    $global:DeploymentContractHttpMock.anonymous = 401
    $global:DeploymentContractHttpMock.readyStatus = 503
    Expect-Denial { & $smoke @arguments } 'Dependency readiness'
    $global:DeploymentContractHttpMock.readyStatus = 200
    $global:DeploymentContractHttpMock.assetStatus = 404
    Expect-Denial { & $smoke @arguments } 'frontend asset'
    $global:DeploymentContractHttpMock.assetStatus = 200
    $global:DeploymentContractHttpMock.shell = '<html></html>'
    Expect-Denial { & $smoke @arguments } 'React mount'
    Write-Output 'Passed Live deployment contracts and mocked GET-only shell/readiness/anonymous/Entra smoke; no auth/config, local-auth POST, cloud calls or authenticated acceptance claims.'
} finally {
    Remove-Variable -Name DeploymentContractHttpMock -Scope Global
    Import-Module $modulePath -Force
}
