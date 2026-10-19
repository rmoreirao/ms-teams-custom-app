$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$failed = $false
Get-ChildItem -LiteralPath (Join-Path $root 'scripts') -Recurse -File |
    Where-Object Extension -in @('.ps1', '.psm1') | ForEach-Object {
    $tokens = $null
    $errors = $null
    [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    foreach ($error in $errors) { Write-Error "$($_.Name): $error" -ErrorAction Continue; $failed = $true }
}
if ($failed) { throw 'PowerShell parsing failed.' }
Write-Output 'PowerShell management scripts parsed successfully.'
