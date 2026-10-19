$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Import-Module (Join-Path $root 'scripts\DeploymentContract.psm1') -Force
function Expect-Denial([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action }
    catch { if ($_.Exception.Message -notlike "*$Message*") { throw }; $failed = $true }
    if (-not $failed) { throw "Expected deployment contract denial: $Message" }
}
$simulator = @{ status = 'ready'; mode = 'simulator'; fixtureVersion = 'split-view-v2' }
$live = @{ status = 'ready'; mode = 'live'; role = 'Api'; admission = $false }
$simAuth = @{ localAuth = $false; simulated = $true }
$liveAuth = @{ localAuth = $false; simulated = $false }
Test-AppDeploymentContract -ExpectedMode Simulator -Health $simulator -AuthConfig $simAuth -AnonymousStatus 401
Test-AppDeploymentContract -ExpectedMode Live -Health $live -AuthConfig $liveAuth -AnonymousStatus 401
Expect-Denial { Test-AppDeploymentContract -ExpectedMode Live -Health $simulator -AuthConfig $simAuth -AnonymousStatus 401 } 'expected deployment mode'
Expect-Denial { Test-AppDeploymentContract -ExpectedMode Live -Health $live -AuthConfig $liveAuth -AnonymousStatus 200 } 'Anonymous API'
Expect-Denial { Test-AppDeploymentContract -ExpectedMode Live -Health $live -AuthConfig $simAuth -AnonymousStatus 401 } 'authentication capabilities'
$live.admission = $true
Expect-Denial { Test-AppDeploymentContract -ExpectedMode Live -Health $live -AuthConfig $liveAuth -AnonymousStatus 401 } 'approved rollout stage'
Test-AppDeploymentContract -ExpectedMode Live -Health $live -AuthConfig $liveAuth -AnonymousStatus 401 -ExpectAdmission $true
$live.role = 'Receiver'
Expect-Denial { Test-AppDeploymentContract -ExpectedMode Live -Health $live -AuthConfig $liveAuth -AnonymousStatus 401 -ExpectAdmission $true } 'Live API role'
Write-Output 'Passed read-only deployment contracts: simulator/live mode, role/admission stage, anonymous denial and capability isolation; no live calls or acceptance claims.'
