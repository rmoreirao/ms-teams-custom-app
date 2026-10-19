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
    & (Join-Path $root 'tests\Scripts\GraphPilotDeployment.Tests.ps1')
    Push-Location (Join-Path $root 'src\Web')
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE) { throw 'Frontend restore failed.' }
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
