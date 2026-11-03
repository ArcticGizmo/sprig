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

.EXAMPLE
  pwsh -File scripts/testbed.ps1
  pwsh -File scripts/testbed.ps1 -Path D:\scratch\testbed -Force
#>
[CmdletBinding()]
param(
    [string] $Path,
    [switch] $Force
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

foreach ($template in Get-ChildItem -LiteralPath $templates -Directory) {
    $repo = Join-Path $Path $template.Name
    Copy-Template $template.FullName $repo

    # A fixed identity, so the testbed's history doesn't depend on (or record) whoever built it.
    git -C $repo init --quiet --initial-branch=main
    git -C $repo -c core.safecrlf=false add --all
    git -C $repo -c user.name='Sprig Testbed' -c user.email='testbed@example.invalid' -c commit.gpgsign=false `
        commit --quiet -m "Initial testbed commit"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed in $repo" }
    Write-Host "  $($template.Name)  ->  $repo"
}

Write-Host ""
Write-Host "Testbed ready at $Path"
Write-Host "Run by hand: see each repo's README (docker compose up -d in shop-services, then dotnet run / npm ci && npm run dev)."
