[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$PublicOrigin,
    [Parameter(Mandatory)][guid]$TenantId,
    [Parameter(Mandatory)][guid]$WebClientId,
    [ValidateSet('Live')][string]$ExpectedMode = 'Live',
    [switch]$ExpectAdmission
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DeploymentContract.psm1')
Invoke-AppDeploymentSmoke -PublicOrigin $PublicOrigin -TenantId $TenantId -WebClientId $WebClientId `
    -ExpectAdmission $ExpectAdmission.IsPresent
