[CmdletBinding()]
param([switch]$Browser)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    Push-Location (Join-Path $root 'src\Web')
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE) { throw 'Frontend restore failed.' }
        npm run build
        if ($LASTEXITCODE) { throw 'Frontend build failed.' }
        npm test
        if ($LASTEXITCODE) { throw 'Frontend tests failed.' }
        dotnet test (Join-Path $root 'TeamsReference.slnx') --nologo --verbosity quiet
        if ($LASTEXITCODE) { throw 'Backend tests failed.' }
        if ($Browser) {
            npm run test:e2e
            if ($LASTEXITCODE) { throw 'Browser scenarios failed.' }
        }
    } finally { Pop-Location }
} finally { Pop-Location }
