<#
.SYNOPSIS
  Builds the sprig testbed: real git repos made from the templates in testbed/repos.

.DESCRIPTION
  Copies each template (shop-services, shop-web, shop-mobile) into its own folder under -Path, makes it a git repo
  on 'main' and commits it, so sprig can make worktrees and branches from it like any real repo. Build output and
  node_modules are never copied.

  The folder gets a .sprig-testbed marker. -Force only deletes a folder that has one, so a wrong -Path can't wipe
  anything else.

.PARAMETER Path
  Where to build the repos. Defaults to a sprig-testbed folder beside this checkout.

.PARAMETER Force
  Delete and rebuild an existing testbed.

.PARAMETER UsePackage
  Keep the definitions' '#:package Sprig.Sdk' lines and pack the SDK into artifacts/feed, with a nuget.config at
  the testbed root pointing there. Without it, each definition references this checkout's Sprig.Sdk project
  directly, so SDK changes show up without repacking.

.EXAMPLE
  pwsh -File scripts/testbed.ps1
  pwsh -File scripts/testbed.ps1 -Path D:\scratch\testbed -Force
#>
[CmdletBinding()]
param(
    [string] $Path,
    [switch] $Force,
    [switch] $UsePackage
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$templates = Join-Path $root 'testbed/repos'
if (-not $Path) { $Path = Join-Path (Split-Path -Parent $root) 'sprig-testbed' }
$Path = [System.IO.Path]::GetFullPath($Path)
$marker = Join-Path $Path '.sprig-testbed'

if (Test-Path $Path) {
    if (-not $Force) { throw "$Path already exists. Pass -Force to rebuild it." }
    if (-not (Test-Path $marker)) { throw "$Path exists but isn't a sprig testbed (no .sprig-testbed marker); not deleting it." }
    Remove-Item -Recurse -Force $Path -Confirm:$false
}
New-Item -ItemType Directory -Force $Path | Out-Null
Set-Content -Path $marker -Value "Built by sprig's scripts/testbed.ps1. Safe to delete."

$skip = @('node_modules', 'dist', 'bin', 'obj', '.vs')

function Copy-Template([string] $from, [string] $to) {
    New-Item -ItemType Directory -Force $to | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $from -Force) {
        if ($skip -contains $item.Name) { continue }
        $dest = Join-Path $to $item.Name
        if ($item.PSIsContainer) { Copy-Template $item.FullName $dest }
        else { Copy-Item -LiteralPath $item.FullName -Destination $dest }
    }
}

$sdkProject = Join-Path $root 'src/Sprig.Sdk/Sprig.Sdk.csproj'

function Set-SdkReference([string] $file) {
    if ($UsePackage) { return }
    $text = Get-Content -LiteralPath $file -Raw
    $text = [regex]::Replace($text, '(?m)^#:package Sprig\.Sdk@\S+', "#:project $sdkProject")
    Set-Content -LiteralPath $file -Value $text -NoNewline
}

if ($UsePackage) {
    $feed = Join-Path $root 'artifacts/feed'
    dotnet pack (Join-Path $root 'src/Sprig.Sdk') -c Release -o $feed -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "packing Sprig.Sdk failed" }
    Set-Content -Path (Join-Path $Path 'nuget.config') -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="sprig-local" value="$feed" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="sprig-local"><package pattern="Sprig.Sdk" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
}

foreach ($template in Get-ChildItem -LiteralPath $templates -Directory) {
    $repo = Join-Path $Path $template.Name
    Copy-Template $template.FullName $repo
    Get-ChildItem -LiteralPath $repo -Filter 'sprig.cs' | ForEach-Object { Set-SdkReference $_.FullName }

    # A fixed identity, so the testbed's history doesn't depend on (or record) whoever built it.
    git -C $repo init --quiet --initial-branch=main
    git -C $repo -c core.safecrlf=false add --all
    git -C $repo -c user.name='Sprig Testbed' -c user.email='testbed@example.invalid' -c commit.gpgsign=false `
        commit --quiet -m "Initial testbed commit"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed in $repo" }
    Write-Host "  $($template.Name)  ->  $repo"
}

# The trellis sits beside the repos it names, the way a team might keep one next to its checkouts.
foreach ($trellis in Get-ChildItem -LiteralPath (Join-Path $root 'testbed') -Filter '*.trellis.cs') {
    $dest = Join-Path $Path $trellis.Name
    Copy-Item -LiteralPath $trellis.FullName -Destination $dest
    Set-SdkReference $dest
    Write-Host "  $($trellis.Name)  ->  $dest"
}

# The same system as an Aspire AppHost, for comparison (docs/spikes/m6-aspire.md). Copied out so the sprig repo's
# Directory.Build.props doesn't apply to it.
$aspire = Join-Path $root 'testbed/aspire'
if (Test-Path -LiteralPath $aspire) {
    $dest = Join-Path $Path 'aspire'
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -Path (Join-Path $aspire '*') -Destination $dest
    Write-Host "  aspire/  ->  $dest"
}

Write-Host ""
Write-Host "Testbed ready at $Path"
Write-Host "Plan it:      sprig trellis add `"$(Join-Path $Path 'storefront.trellis.cs')`" ; sprig plan storefront"
Write-Host "Run by hand:  see each repo's README."
