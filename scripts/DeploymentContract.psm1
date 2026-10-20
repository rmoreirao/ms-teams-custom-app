function Test-AppDeploymentContract {
    [CmdletBinding()]
    param(
        [ValidateSet('Live')][string]$ExpectedMode = 'Live',
        [Parameter(Mandatory)]$Health,
        [Parameter(Mandatory)][int]$AnonymousStatus,
        [bool]$ExpectAdmission = $false
    )
    if ($Health.status -cne 'ready' -or $Health.mode -cne 'live') {
        throw 'Readiness did not confirm the Live-only deployment mode.'
    }
    if ($Health.role -cne 'Api' -or $Health.admission -isnot [bool] -or $Health.admission -ne $ExpectAdmission) {
        throw 'Live API role or admission does not match the preserved rollout stage.'
    }
    if ($AnonymousStatus -ne 401) { throw 'Anonymous API access did not fail closed with HTTP 401.' }
}

function Get-AppDeploymentResponse($Client, [string]$Path) {
    $response = $Client.GetAsync($Path).GetAwaiter().GetResult()
    try {
        [pscustomobject]@{
            status = [int]$response.StatusCode
            body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            location = $response.Headers.Location
        }
    } finally {
        $response.Dispose()
    }
}

function Invoke-AppDeploymentSmoke {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][uri]$PublicOrigin,
        [Parameter(Mandatory)][guid]$TenantId,
        [Parameter(Mandatory)][guid]$WebClientId,
        [bool]$ExpectAdmission = $false
    )
    if ($PublicOrigin.Scheme -ne 'https' -or $PublicOrigin.AbsolutePath -ne '/' -or $PublicOrigin.Query -or
        $PublicOrigin.Fragment -or $PublicOrigin.UserInfo -or $PublicOrigin.Port -ne 443 -or $PublicOrigin.IsLoopback -or
        $TenantId -eq [guid]::Empty -or $WebClientId -eq [guid]::Empty) {
        throw 'Use the deployed canonical public HTTPS origin and nonempty Entra tenant/client IDs.'
    }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(20)
    $client.BaseAddress = $PublicOrigin
    try {
        $homepage = Get-AppDeploymentResponse $client '/'
        if ($homepage.status -ne 200) { throw "Application shell returned HTTP $($homepage.status)." }
        if ($homepage.body -notmatch '<div id="root"></div>') { throw 'Application shell is missing the React mount point.' }
        $asset = [regex]::Match($homepage.body, 'src="(?<path>/assets/[^"]+\.js)"')
        if (-not $asset.Success) { throw 'Application shell is missing its compiled JavaScript entry point.' }
        $bundle = Get-AppDeploymentResponse $client $asset.Groups['path'].Value
        if ($bundle.status -ne 200) { throw 'Compiled frontend asset could not be loaded.' }
        $ready = Get-AppDeploymentResponse $client '/health/ready'
        if ($ready.status -ne 200) { throw "Dependency readiness returned HTTP $($ready.status)." }
        $health = $ready.body | ConvertFrom-Json
        $anonymous = Get-AppDeploymentResponse $client '/api/session'
        Test-AppDeploymentContract -Health $health -AnonymousStatus $anonymous.status -ExpectAdmission $ExpectAdmission

        $signin = Get-AppDeploymentResponse $client '/auth/signin'
        if ($signin.status -ne 302 -or -not $signin.location) {
            throw 'Entra sign-in did not issue an authorization redirect.'
        }
        $redirect = [uri]$signin.location
        if (-not $redirect.IsAbsoluteUri -or $redirect.Scheme -ne 'https' -or
            $redirect.Host -ne 'login.microsoftonline.com' -or $redirect.Port -ne 443 -or
            $redirect.UserInfo -or $redirect.Fragment -or $redirect.AbsolutePath -cne "/$TenantId/oauth2/v2.0/authorize") {
            throw 'Sign-in redirected to an unexpected authority or tenant.'
        }
        $query = [System.Web.HttpUtility]::ParseQueryString($redirect.Query)
        if ($query['client_id'] -cne $WebClientId.ToString() -or
            $query['redirect_uri'] -cne "$($PublicOrigin.AbsoluteUri.TrimEnd('/'))/auth/callback") {
            throw 'Sign-in client ID or canonical callback origin is incorrect.'
        }
        [pscustomobject]@{
            origin = $PublicOrigin.AbsoluteUri.TrimEnd('/')
            status = 'passed'
            mode = 'live'
            admission = $health.admission
            frontend = 'loaded'
            anonymousApi = 'denied'
            entraRedirect = 'validated'
            authenticatedJourneys = 'not_tested'
            graphIntegration = 'not_tested'
            productionAcceptance = 'not_certified'
        }
    } finally {
        $client.Dispose()
        $handler.Dispose()
    }
}
Export-ModuleMember -Function Test-AppDeploymentContract, Invoke-AppDeploymentSmoke
