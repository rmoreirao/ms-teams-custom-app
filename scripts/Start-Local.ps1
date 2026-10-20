[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ConfigurationPath,
    [ValidateRange(1024, 65535)][int]$Port = 7266
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)) + [IO.Path]::DirectorySeparatorChar
$path = [IO.Path]::GetFullPath($ConfigurationPath)
if ($path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep resolved application configuration outside the repository.' }
$settings = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
if ($settings.App.GraphMode -ne 'Live' -or $settings.App.Store -ne 'Cosmos' -or $settings.App.Auth -ne 'Entra' -or
    $settings.App.PublicOrigin -ne "https://localhost:$Port") {
    throw 'Use real-service Live/Cosmos/Entra settings and the explicitly approved HTTPS localhost origin. No local identity or memory-store mode exists.'
}
$previousConfig = $env:TEAMS_CONFIGURATION_PATH
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
try {
    $env:TEAMS_CONFIGURATION_PATH = $path
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    dotnet run --project (Join-Path $root 'src\Teams.Host') --no-launch-profile --urls "https://localhost:$Port"
    if ($LASTEXITCODE) { throw 'Real-service local application failed. Check Live configuration, Entra credentials and private Azure connectivity.' }
} finally {
    $env:TEAMS_CONFIGURATION_PATH = $previousConfig
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
}
