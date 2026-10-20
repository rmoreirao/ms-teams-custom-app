$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$settings = Get-Content -LiteralPath (Join-Path $root 'src\Teams.Host\appsettings.json') -Raw | ConvertFrom-Json
if ($settings.App.GraphMode -ne 'Live' -or $settings.App.Store -ne 'Cosmos' -or $settings.App.Auth -ne 'Entra') {
    throw 'Application defaults must require real Live/Cosmos/Entra services.'
}
$development = Get-Content -LiteralPath (Join-Path $root 'src\Teams.Host\appsettings.Development.json') -Raw | ConvertFrom-Json
if ($development.App) { throw 'Development settings must not introduce a different runtime or authentication mode.' }
foreach ($path in @(
    'src\Teams.Infrastructure\Simulator.cs', 'src\Teams.Infrastructure\Fixtures.cs', 'src\Teams.Infrastructure\Stores.cs',
    'src\Teams.Host\GraphPilot.cs', 'src\Teams.Host\ProjectionWorker.cs',
    'scripts\Deploy-GraphPilot.ps1', 'scripts\entra\Prepare-GraphPilot.ps1',
    'scripts\test-data\Invoke-Fixtures.ps1', 'scripts\test-data\Reset-Fixtures.ps1', 'scripts\test-data\manifest.json'
)) {
    if (Test-Path -LiteralPath (Join-Path $root $path)) { throw "Retired runtime entry point remains: $path" }
}
foreach ($directory in @('src\Teams.Core', 'src\Teams.Host', 'src\Teams.Infrastructure')) {
    $files = Get-ChildItem -LiteralPath (Join-Path $root $directory) -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    foreach ($file in $files) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        if ($text -match 'class\s+(Simulator|GraphPilot\w*|MemoryStateStore|MemoryPartitionDocuments|MemoryBudget|LocalRealtime)\b' -or
            $text -match 'GetSimulatorReceiptAsync|SaveSimulatorReceiptAsync|MapPost\("/auth/local"|MapPost\("/simulator/') {
            throw "A retired implementation or test double is shipped: $($file.Name)"
        }
    }
}
Get-ChildItem -LiteralPath (Join-Path $root 'infra') -Recurse -File -Filter '*.bicep' | ForEach-Object {
    $text = Get-Content -LiteralPath $_.FullName -Raw
    if ($text -match "value:\s*'(Simulator|GraphPilot|Seed|Reset|Verify|Worker|All)'" -or
        $text -match "param\s+graphMode\b|param\s+graphPilotConfig\b|param\s+fixtureActorIds\b") {
        throw "Infrastructure can still select a retired runtime or fixture role: $($_.Name)"
    }
}
Write-Output 'Passed Live-only packaging/configuration guards: no deployable legacy runtime, local sign-in, runtime test doubles or seed/reset entry points.'
