function Test-AppDeploymentContract {
    [CmdletBinding()]
    param(
        [ValidateSet('Simulator', 'GraphPilot', 'Live')][string]$ExpectedMode = 'Simulator',
        [Parameter(Mandatory)]$Health,
        [Parameter(Mandatory)]$AuthConfig,
        [Parameter(Mandatory)][int]$AnonymousStatus,
        [bool]$ExpectAdmission = $false
    )
    $expected = if ($ExpectedMode -eq 'GraphPilot') { 'graph-pilot' } else { $ExpectedMode.ToLowerInvariant() }
    if ($Health.status -ne 'ready' -or $Health.mode -ne $expected) {
        throw 'Readiness did not confirm the expected deployment mode.'
    }
    if ($ExpectedMode -eq 'Simulator' -and $Health.fixtureVersion -ne 'split-view-v2') {
        throw 'Readiness did not confirm the expected simulator fixture dataset.'
    }
    if ($ExpectedMode -eq 'Live' -and ($Health.role -ne 'Api' -or $Health.admission -ne $ExpectAdmission)) {
        throw 'Live API role or admission does not match the approved rollout stage.'
    }
    if ($AnonymousStatus -ne 401) { throw 'Anonymous API access did not fail closed with HTTP 401.' }
    if ($AuthConfig.localAuth -ne $false -or $AuthConfig.simulated -ne ($ExpectedMode -eq 'Simulator')) {
        throw 'Public authentication capabilities do not match the protected deployment mode.'
    }
}
Export-ModuleMember -Function Test-AppDeploymentContract
