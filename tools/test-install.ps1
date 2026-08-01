<#
.SYNOPSIS
    Tests for install.ps1 - the Windows one-liner installer.

.DESCRIPTION
    install.ps1 is the primary install path and can't be covered by the xUnit suite (which tests
    Sprig.Core), so its parsing-critical logic is exercised here instead. Run it after touching
    install.ps1:

        powershell -NoProfile -File tools\test-install.ps1

    It loads install.ps1's functions with the entrypoint call stripped, then drives them directly.
    Nothing here talks to github.com, so the live release lookup, the download, and the installer
    actually running are still manual checks. What it does cover is the stuff that silently breaks the
    whole install: the file staying pure ASCII, and the SHA256SUMS.txt line parsing.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$installPs1 = Join-Path $root 'install.ps1'
$src = (Get-Content $installPs1 -Raw) -replace '(?m)^Install-Sprig\s+.*$', ''
Invoke-Expression $src

$pass = 0; $fail = 0
function Check($name, $cond, $detail = '') {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else { $script:fail++; Write-Host "  FAIL  $name  $detail" -ForegroundColor Red }
}
function CheckThrows($name, [scriptblock]$sb, [string]$expect) {
    try { & $sb; Check $name $false 'did not throw' }
    catch { Check $name ($_.Exception.Message -like "*$expect*") "message was: $($_.Exception.Message)" }
}

Write-Host "`n=== Encoding (whole release pipeline) ===" -ForegroundColor Cyan
# install.ps1 carries no BOM, so Windows PowerShell 5.1 decodes it as the system codepage rather than
# UTF-8. A UTF-8 em dash then arrives as three Windows-1252 chars ending in 0x94 = U+201D - a curly
# closing quote, and PowerShell honours curly quotes as string delimiters. One em dash inside a "..."
# string therefore terminates it early and silently mis-parses everything after it. So: hold it to ASCII.
$bytes = [System.IO.File]::ReadAllBytes($installPs1)
$nonAscii = @($bytes | Where-Object { $_ -gt 127 })
Check 'install.ps1 is pure ASCII' ($nonAscii.Count -eq 0) "$($nonAscii.Count) non-ASCII byte(s)"
Check 'install.ps1 has no UTF-8 BOM' (-not ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF))

Write-Host "`n=== Get-ExpectedHash (SHA256SUMS.txt parsing) ===" -ForegroundColor Cyan
$hash = ('a' * 64)
$manifest = @(
    "$hash  Sprig-win-Setup.exe"
    "$('b' * 64)  Sprig-0.4.0-full.nupkg"
) -join "`n"

Check 'pulls the hash for the named asset' `
    ((Get-ExpectedHash -Sums $manifest -Name 'Sprig-win-Setup.exe' -Tag 'v0.4.0') -eq $hash.ToUpperInvariant())

# sha256sum's binary-mode marker: "<hex> *<name>". install.ps1 must tolerate the leading '*'.
Check 'tolerates the binary-mode asterisk marker' `
    ((Get-ExpectedHash -Sums "$hash *Sprig-win-Setup.exe" -Name 'Sprig-win-Setup.exe' -Tag 'v0.4.0') -eq $hash.ToUpperInvariant())

CheckThrows 'throws when the asset is not listed' `
    { Get-ExpectedHash -Sums "$hash  something-else.exe" -Name 'Sprig-win-Setup.exe' -Tag 'v0.4.0' } 'no entry for'

Write-Host "`n=== Get-RepoUrl ===" -ForegroundColor Cyan
Check 'builds the repo URL' ((Get-RepoUrl 'ArcticGizmo/sprig') -eq 'https://github.com/ArcticGizmo/sprig')

Write-Host ''
if ($fail -gt 0) { Write-Host "$fail failed, $pass passed" -ForegroundColor Red; exit 1 }
Write-Host "All $pass checks passed" -ForegroundColor Green
