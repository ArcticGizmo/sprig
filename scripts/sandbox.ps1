<#
.SYNOPSIS
    Build, sync or remove the examples sandbox: a throwaway sprig store whose repos are copies of examples/.

.DESCRIPTION
    examples/repos/* are plain folders inside sprig's own repo, so sprig can't use them directly: it would
    resolve them to sprig's git root and make worktrees of the whole checkout. This script copies each one
    out to its own tiny git repo under the sandbox root, gives it local bare "remotes" (so fetch, push and
    fork cases work offline), registers it in the sandbox's own store and imports examples/maps/*.

    It also sets SPRIG_STORE for the current session, so `sprig` and the app (both Debug and Release) use
    the sandbox until you run with -Off or close the terminal.

.PARAMETER Sync
    Copy changes from examples/ into the existing sandbox: each repo's tracked files are replaced by the
    example's, committed and pushed to its origin, and maps are re-imported. New examples are added.

.PARAMETER Reset
    Delete the sandbox (stopping its containers first) and build it again.

.PARAMETER Remove
    Delete the sandbox and clear SPRIG_STORE.

.PARAMETER Off
    Clear SPRIG_STORE for this session, leaving the sandbox on disk.

.EXAMPLE
    ./scripts/sandbox.ps1
    sprig pool checkout polyrepo --branch try-it --ni
#>
[CmdletBinding(DefaultParameterSetName = 'Build')]
param(
    [Parameter(ParameterSetName = 'Sync')][switch]$Sync,
    [Parameter(ParameterSetName = 'Reset')][switch]$Reset,
    [Parameter(ParameterSetName = 'Remove')][switch]$Remove,
    [Parameter(ParameterSetName = 'Off')][switch]$Off,
    [string]$Root
)

$ErrorActionPreference = 'Stop'

$onWindows = $env:OS -eq 'Windows_NT'
if (-not $Root) {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Join-Path $HOME '.local/share' }
    $Root = Join-Path $base 'sprig (Sandbox)'
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$examples = Join-Path $repoRoot 'examples'
$marker = Join-Path $Root '.sprig-sandbox'
$reposDir = Join-Path $Root 'repos'
$remotesDir = Join-Path $Root 'remotes'

# The api example gets an upstream as well as an origin, with branches arranged so `ws switch --branch`
# can be tried against a branch on both remotes (origin should win) and one only on upstream.
$forkedRepo = 'api'

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "'$Exe $($Arguments -join ' ')' failed (exit $LASTEXITCODE)" }
}

function Invoke-Git {
    param([string]$Dir, [string[]]$Arguments)
    Invoke-Native git (@('-C', $Dir, '-c', 'user.name=sprig-sandbox', '-c', 'user.email=sandbox@sprig.invalid') + $Arguments)
}

# Stored in each repo's own config (not passed per command) because a push runs receive-pack inside the
# bare remote, which never sees our -c flags. longpaths: object paths under a deep profile folder pass
# Windows' 260-character limit. autocrlf off: these are throwaway copies, so skip the conversion warnings.
function Set-SandboxGitConfig {
    param([string]$Dir)
    Invoke-Native git @('-C', $Dir, 'config', 'core.longpaths', 'true')
    Invoke-Native git @('-C', $Dir, 'config', 'core.autocrlf', 'false')
}

function Get-SprigExe {
    Write-Host 'Building the sprig CLI...'
    # Out-Host so the build's output isn't returned from this function along with the path.
    Invoke-Native dotnet @('build', (Join-Path $repoRoot 'src/Sprig.Cli/Sprig.Cli.csproj'), '-nologo', '-v', 'q') | Out-Host
    $exe = Join-Path $repoRoot ('src/Sprig.Cli/bin/Debug/net10.0/' + $(if ($onWindows) { 'sprig.exe' } else { 'sprig' }))
    if (-not (Test-Path $exe)) { throw "built CLI not found at $exe" }
    return $exe
}

function Invoke-Sprig {
    param([string[]]$Arguments)
    Invoke-Native $script:sprig $Arguments
}

function Enable-Sandbox {
    $env:SPRIG_STORE = $Root
    Write-Host ''
    Write-Host "SPRIG_STORE is set to the sandbox for this session: $Root" -ForegroundColor Green
    Write-Host '  try:   sprig map ls'
    Write-Host '         sprig pool checkout polyrepo --branch try-it --ni'
    Write-Host '  done:  scripts/sandbox -Off   (or close the terminal)'
}

function Disable-Sandbox {
    Remove-Item Env:SPRIG_STORE -ErrorAction SilentlyContinue
    Write-Host 'SPRIG_STORE cleared; sprig is back on your normal store.'
}

function Remove-SandboxRoot {
    if (-not (Test-Path $Root)) { return }
    if (-not (Test-Path $marker)) {
        throw "refusing to delete '$Root': it has no $([IO.Path]::GetFileName($marker)) marker, so it may not be a sandbox"
    }

    # Containers outlive the folder, so stop each workspace's compose project first. Best-effort: Docker
    # may not be installed or running, and a workspace may never have been brought up.
    $instances = Join-Path $Root 'instances'
    if ((Test-Path $instances) -and (Get-Command docker -ErrorAction SilentlyContinue)) {
        # Only workspaces with a generated compose file have infra to stop.
        foreach ($ws in Get-ChildItem $instances -Directory | Where-Object { Get-ChildItem $_.FullName -Filter '*.sprig.yml' }) {
            Write-Host "Stopping containers for $($ws.Name)..."
            & docker compose -p "sprig-$($ws.Name)" down -v --remove-orphans | Out-Null
        }
    }

    Write-Host "Deleting $Root..."
    Remove-Item -LiteralPath $Root -Recurse -Force
}

function Copy-ExampleFiles {
    param([string]$Source, [string]$Destination)
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    # -Force so dotfiles (.sprig.json, .gitignore) are included on every platform.
    Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
}

function New-BareRemote {
    param([string]$Repo, [string]$Remote)
    $path = Join-Path $remotesDir "$Repo-$Remote.git"
    Invoke-Native git @('init', '--bare', '-q', '-b', 'main', $path) | Out-Host
    Set-SandboxGitConfig $path
    Invoke-Git (Join-Path $reposDir $Repo) @('remote', 'add', $Remote, $path) | Out-Host
}

# A branch committed and pushed to the given remotes, then deleted locally so it exists only remotely.
function Add-RemoteOnlyBranch {
    param([string]$Repo, [string]$Branch, [string[]]$Remotes)
    $dir = Join-Path $reposDir $Repo
    Invoke-Git $dir @('switch', '-q', '-c', $Branch)
    Set-Content -Path (Join-Path $dir 'FEATURE.md') -Value "Work on $Branch, pushed to: $($Remotes -join ', ')"
    Invoke-Git $dir @('add', '-A')
    Invoke-Git $dir @('commit', '-q', '-m', "Work on $Branch")
    foreach ($remote in $Remotes) { Invoke-Git $dir @('push', '-q', $remote, $Branch) }
    Invoke-Git $dir @('switch', '-q', 'main')
    Invoke-Git $dir @('branch', '-q', '-D', $Branch)
}

function New-SandboxRepo {
    param([string]$Name)
    $dir = Join-Path $reposDir $Name
    Write-Host "Creating repo $Name..."
    Copy-ExampleFiles (Join-Path $examples "repos/$Name") $dir
    Invoke-Native git @('init', '-q', '-b', 'main', $dir)
    Set-SandboxGitConfig $dir
    Invoke-Git $dir @('add', '-A')
    Invoke-Git $dir @('commit', '-q', '-m', "Initial copy of examples/repos/$Name")

    New-BareRemote $Name 'origin'
    Invoke-Git $dir @('push', '-q', '-u', 'origin', 'main')

    if ($Name -eq $forkedRepo) {
        New-BareRemote $Name 'upstream'
        Invoke-Git $dir @('push', '-q', 'upstream', 'main')
        Add-RemoteOnlyBranch $Name 'shared-feature' @('origin', 'upstream')
        Add-RemoteOnlyBranch $Name 'upstream-only' @('upstream')
        Invoke-Git $dir @('fetch', '-q', '--all')
    }

    Invoke-Sprig @('repo', 'add', $dir)
}

# Replace a sandbox repo's tracked files with the example's and commit the difference, the way a teammate
# pushing a config change would. Untracked files (a generated .env) are left alone.
function Sync-SandboxRepo {
    param([string]$Name)
    $dir = Join-Path $reposDir $Name
    $branch = (& git -C $dir branch --show-current).Trim()
    if ($branch -ne 'main') {
        Write-Warning "skipping $Name - its source repo is on '$branch', not main (switch it back to sync)"
        return
    }

    foreach ($file in (& git -C $dir ls-files)) {
        Remove-Item -LiteralPath (Join-Path $dir $file) -Force -ErrorAction SilentlyContinue
    }
    Copy-ExampleFiles (Join-Path $examples "repos/$Name") $dir
    Invoke-Git $dir @('add', '-A')
    if (& git -C $dir status --porcelain) {
        Invoke-Git $dir @('commit', '-q', '-m', "Sync from examples/repos/$Name")
        Invoke-Git $dir @('push', '-q', 'origin', 'main')
        Write-Host "Synced $Name (committed and pushed to origin)."
    }
    else {
        Write-Host "$Name is already up to date."
    }
}

function Import-Maps {
    foreach ($map in Get-ChildItem (Join-Path $examples 'maps') -Filter '*.json') {
        Invoke-Sprig @('map', 'import', $map.FullName)
    }
}

function Get-ExampleNames {
    Get-ChildItem (Join-Path $examples 'repos') -Directory | ForEach-Object Name
}

# The store must be pointed at the sandbox before any `sprig` call, or repos would be registered in your real store.
function Use-SandboxStore { $env:SPRIG_STORE = $Root }

if ($Off) { Disable-Sandbox; return }
if ($Remove) { Remove-SandboxRoot; Disable-Sandbox; return }
if ($Reset) { Remove-SandboxRoot }

if ($Sync) {
    if (-not (Test-Path $marker)) { throw "no sandbox at '$Root' - run without -Sync to build one" }
    $script:sprig = Get-SprigExe
    Use-SandboxStore
    foreach ($name in Get-ExampleNames) {
        if (Test-Path (Join-Path $reposDir $name)) { Sync-SandboxRepo $name } else { New-SandboxRepo $name }
    }
    Import-Maps
    Enable-Sandbox
    return
}

if (Test-Path $marker) {
    Write-Host "A sandbox already exists at $Root. Use -Sync to pull in changes from examples/, or -Reset to rebuild it."
    Enable-Sandbox
    return
}
if ((Test-Path $Root) -and (Get-ChildItem $Root -Force | Select-Object -First 1)) {
    throw "'$Root' exists, is not empty and is not a sandbox - choose another -Root"
}

$script:sprig = Get-SprigExe
New-Item -ItemType Directory -Force -Path $reposDir, $remotesDir | Out-Null
Set-Content -Path $marker -Value 'This directory is the sprig examples sandbox. scripts/sandbox -Reset or -Remove deletes all of it.'
Use-SandboxStore
foreach ($name in Get-ExampleNames) { New-SandboxRepo $name }
Import-Maps
Enable-Sandbox
