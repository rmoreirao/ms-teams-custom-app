[CmdletBinding()]
param([int]$Port = 5255)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $env:TEAMS_LOCAL_PASSWORD) {
    $secure = Read-Host 'Choose a temporary development password (not stored)' -AsSecureString
    $env:TEAMS_LOCAL_PASSWORD = [System.Net.NetworkCredential]::new('', $secure).Password
}
try {
    $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16)
    $env:LocalAuth__PasswordSalt = [Convert]::ToHexString($salt)
    $env:LocalAuth__PasswordHash = [Convert]::ToHexString([Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
        [Text.Encoding]::UTF8.GetBytes($env:TEAMS_LOCAL_PASSWORD), $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32))
    Remove-Item Env:TEAMS_LOCAL_PASSWORD
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    dotnet run --project (Join-Path $root 'src\Teams.Host') --no-launch-profile --urls "http://localhost:$Port"
    if ($LASTEXITCODE) { throw 'Local application failed.' }
}
finally {
    Remove-Item Env:LocalAuth__PasswordHash -ErrorAction SilentlyContinue
    Remove-Item Env:LocalAuth__PasswordSalt -ErrorAction SilentlyContinue
    Remove-Item Env:TEAMS_LOCAL_PASSWORD -ErrorAction SilentlyContinue
}
