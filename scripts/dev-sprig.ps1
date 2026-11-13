<#
.SYNOPSIS
  Makes `sprig` in this terminal mean this checkout's Debug build, until the terminal closes or you run it with -Off.

.DESCRIPTION
  Builds Sprig.Cli (which brings sprig-host with it) and Sprig.App, then puts the CLI's build folder at the front of
  this session's PATH. Nothing is written to the user or machine PATH, so other terminals, and an installed release,
  are left as they are.

  Run it as a script in the terminal you'll test from (.\scripts\dev-sprig.ps1), not with `pwsh -File`, which would
  change a PATH that exits straight away.

  A Debug build keeps its store apart from an installed release's on its own. -Store moves it again, to a folder of
  your choosing, so a hand pass can start empty.

.PARAMETER Store
  Point SPRIG_STORE at this folder for the session. It's created if missing.

.PARAMETER NoBuild
  Use the build that's already there.

.PARAMETER Off
  Take the build folder off PATH and put SPRIG_STORE back the way it was.

.EXAMPLE
  .\scripts\dev-sprig.ps1
  .\scripts\dev-sprig.ps1 -Store ..\sprig-store-handpass
  .\scripts\dev-sprig.ps1 -Off
#>
[CmdletBinding()]
param(
    [string] $Store,
    [switch] $NoBuild,
    [switch] $Off
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cliBin = Join-Path $root 'src/Sprig.Cli/bin/Debug/net10.0'
$cliBin = [System.IO.Path]::GetFullPath($cliBin)
# An env var set to "" is dropped, so "there was no store before" needs a value of its own. dev-sprig.cmd uses the
# same one, since a cmd window started from this terminal inherits it.
$unset = '-unset-'

function Remove-FromPath([string] $dir) {
    $entries = $env:PATH -split ';' | Where-Object { $_ -and ($_.TrimEnd('\', '/') -ine $dir.TrimEnd('\', '/')) }
    $env:PATH = $entries -join ';'
}

if ($Off) {
    Remove-FromPath $cliBin
    # Kept in an env var because a script's own variables are gone by the time -Off runs.
    if ($env:SPRIG_DEV_PREVIOUS_STORE) {
        $env:SPRIG_STORE = if ($env:SPRIG_DEV_PREVIOUS_STORE -eq $unset) { $null } else { $env:SPRIG_DEV_PREVIOUS_STORE }
        $env:SPRIG_DEV_PREVIOUS_STORE = $null
    }
    $now = Get-Command sprig -CommandType Application, ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
    Write-Host "Dev sprig is off PATH. sprig is now: $(if ($now) { $now.Source } else { 'not found' })"
    return
}

if (-not $NoBuild) {
    Write-Host "Building src/Sprig.Cli ..."
    dotnet build (Join-Path $root 'src/Sprig.Cli') -c Debug -nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "building src/Sprig.Cli failed. If it couldn't copy sprig-host.exe, a dev host is still running: stop it and try again."
    }
    # The app is only needed for `sprig open`, and an open dev app locks its own exe, so it may stay as it was.
    Write-Host "Building src/Sprig.App ..."
    dotnet build (Join-Path $root 'src/Sprig.App') -c Debug -nologo -v quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "the app didn't rebuild (it's probably open); the CLI is up to date, and the app updates once it's closed and this runs again." }
}

if (-not (Test-Path (Join-Path $cliBin 'sprig.exe'))) { throw "no sprig.exe in $cliBin; run without -NoBuild." }

Remove-FromPath $cliBin
$env:PATH = "$cliBin;$env:PATH"

if ($Store) {
    $Store = [System.IO.Path]::GetFullPath($Store)
    New-Item -ItemType Directory -Force $Store | Out-Null
    if (-not $env:SPRIG_DEV_PREVIOUS_STORE) {
        $env:SPRIG_DEV_PREVIOUS_STORE = if ($env:SPRIG_STORE) { $env:SPRIG_STORE } else { $unset }
    }
    $env:SPRIG_STORE = $Store
}

$resolved = (Get-Command sprig -CommandType Application, ExternalScript | Select-Object -First 1).Source
if ($resolved -notlike "$cliBin*") { Write-Warning "sprig still resolves to $resolved, ahead of the dev build." }

Write-Host ""
Write-Host "sprig    -> $resolved"
Write-Host "store    -> $(if ($env:SPRIG_STORE) { $env:SPRIG_STORE } else { "%LOCALAPPDATA%\sprig (Dev)" })"
Write-Host "For this terminal only. Undo with: .\scripts\dev-sprig.ps1 -Off"
