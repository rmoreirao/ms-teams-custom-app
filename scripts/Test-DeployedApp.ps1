[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$PublicOrigin,
    [Parameter(Mandatory)][guid]$TenantId,
    [Parameter(Mandatory)][guid]$WebClientId
)
$ErrorActionPreference = 'Stop'
if ($PublicOrigin.Scheme -ne 'https' -or $PublicOrigin.AbsolutePath -ne '/' -or $PublicOrigin.Query -or $PublicOrigin.Fragment) {
    throw 'Use the deployed HTTPS origin without a path, query or fragment.'
}
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(20)
$client.BaseAddress = $PublicOrigin
try {
    $homepage = $client.GetAsync('/').GetAwaiter().GetResult()
    if ([int]$homepage.StatusCode -ne 200) { throw "Application shell returned HTTP $([int]$homepage.StatusCode)." }
    $html = $homepage.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ($html -notmatch '<div id="root"></div>') { throw 'Application shell is missing the React mount point.' }
    $asset = [regex]::Match($html, 'src="(?<path>/assets/[^"]+\.js)"')
    if (-not $asset.Success) { throw 'Application shell is missing its compiled JavaScript entry point.' }
    $bundle = $client.GetAsync($asset.Groups['path'].Value).GetAwaiter().GetResult()
    if ([int]$bundle.StatusCode -ne 200) { throw 'Compiled frontend asset could not be loaded.' }

    $ready = $client.GetAsync('/health/ready').GetAwaiter().GetResult()
    if ([int]$ready.StatusCode -ne 200) { throw "Dependency readiness returned HTTP $([int]$ready.StatusCode)." }
    $health = $ready.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    if ($health.status -ne 'ready' -or $health.mode -ne 'simulator' -or $health.fixtureVersion -ne 'split-view-v1') {
        throw 'Dependency readiness did not confirm the expected simulator fixture environment.'
    }
    $anonymous = $client.GetAsync('/api/session').GetAwaiter().GetResult()
    if ([int]$anonymous.StatusCode -ne 401) { throw 'Anonymous API access did not fail closed with HTTP 401.' }
    $capability = $client.GetAsync('/auth/config').GetAwaiter().GetResult()
    if ([int]$capability.StatusCode -ne 200) { throw 'Public sign-in capability configuration is unavailable.' }
    $authConfig = $capability.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
    if ($authConfig.localAuth -ne $false -or $authConfig.simulated -ne $true) {
        throw 'Deployed sign-in configuration does not match the protected simulator environment.'
    }
    $local = $client.PostAsync('/auth/local', [Net.Http.StringContent]::new('{}')).GetAwaiter().GetResult()
    if ([int]$local.StatusCode -ne 404) { throw 'Local development sign-in is reachable on the deployed application.' }

    $signin = $client.GetAsync('/auth/signin').GetAwaiter().GetResult()
    if ([int]$signin.StatusCode -ne 302 -or -not $signin.Headers.Location) { throw 'Entra sign-in did not issue an authorization redirect.' }
    $redirect = $signin.Headers.Location
    if ($redirect.Scheme -ne 'https' -or $redirect.Host -ne 'login.microsoftonline.com' -or
        $redirect.AbsolutePath -ne "/$TenantId/oauth2/v2.0/authorize") {
        throw 'Sign-in redirected to an unexpected authority or tenant.'
    }
    $query = [System.Web.HttpUtility]::ParseQueryString($redirect.Query)
    if ($query['client_id'] -ne $WebClientId.ToString() -or
        $query['redirect_uri'] -ne "$($PublicOrigin.AbsoluteUri.TrimEnd('/'))/auth/callback") {
        throw 'Sign-in client ID or canonical callback origin is incorrect.'
    }
    [pscustomobject]@{
        origin = $PublicOrigin.AbsoluteUri.TrimEnd('/')
        status = 'passed'
        mode = 'simulator'
        fixtureVersion = $health.fixtureVersion
        frontend = 'loaded'
        anonymousApi = 'denied'
        localSignIn = 'disabled'
        entraRedirect = 'validated'
        authenticatedJourneys = 'not_tested'
    }
} finally {
    $client.Dispose()
    $handler.Dispose()
}
