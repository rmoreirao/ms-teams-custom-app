[CmdletBinding()]
param([switch]$Browser)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & (Join-Path $root 'scripts\Check-Scripts.ps1')
    & (Join-Path $root 'tests\Scripts\EntraBootstrap.Tests.ps1')
    & (Join-Path $root 'tests\Scripts\SignInConsent.Tests.ps1')
    & (Join-Path $root 'tests\Scripts\IdentityProfile.Tests.ps1')
    & (Join-Path $root 'tests\Scripts\LiveDeployment.Tests.ps1')
    & (Join-Path $root 'tests\Scripts\DeploymentContract.Tests.ps1')
    & (Join-Path $root 'tests\Scripts\DevDeployment.Tests.ps1')
    & (Join-Path $root 'tests\Scripts\LiveOnly.Tests.ps1')
    & (Join-Path $root 'tests\Scripts\LiveSetup.Tests.ps1')
    node --test (Join-Path $root 'tests\Scripts\primary-read.test.mjs') (Join-Path $root 'tests\Scripts\pubsub-waf.test.mjs')
    if ($LASTEXITCODE) { throw 'Azure protocol contract checks failed.' }
    Push-Location (Join-Path $root 'src\Web')
    try {
        npm run build
        if ($LASTEXITCODE) { throw 'Frontend build failed.' }
        npm test
        if ($LASTEXITCODE) { throw 'Frontend tests failed.' }
        npm run check:entra
        if ($LASTEXITCODE) { throw 'Authentication helper type checks failed.' }
        dotnet test (Join-Path $root 'TeamsReference.slnx') --nologo --verbosity quiet
        if ($LASTEXITCODE) { throw 'Backend tests failed.' }
        if ($Browser) {
            npm run test:entra:unit
            if ($LASTEXITCODE) { throw 'Local authentication helper tests failed.' }
            npm run test:e2e
            if ($LASTEXITCODE) { throw 'Browser scenarios failed.' }
        }
    } finally { Pop-Location }
} finally { Pop-Location }
