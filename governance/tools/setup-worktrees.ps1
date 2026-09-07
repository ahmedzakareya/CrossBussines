<#
=============================================================================================
R1 - per-tab git worktree setup.

Creates the protected integration branch and one worktree per tab, so four tabs stop sharing one
mutable working tree.

WHY WORKTREES RATHER THAN CLONES: the four tabs share one SQL slice registry and one ADR
registry. A second clone lets those registries diverge exactly the way deploy/sql already has -
two copies of one concept, drifting. Worktrees share the object store and the registries stay
single-sourced, while each tab still gets a real working directory it can build in.

WHY NOT SEQUENTIAL TABS: four tabs would run at a quarter speed.
WHY NOT THE SHARED TREE: that is the observed failure - five build breaks from three tabs.

SAFETY:
  * DRY RUN IS THE DEFAULT. Nothing is created unless -Apply is passed.
  * It never commits, never pushes, never deletes a branch, and never touches the working tree
    you are standing in.
  * It refuses to run if the current tree has uncommitted changes, unless -AllowDirty is passed -
    creating branches around uncommitted work is how work gets lost.

EXIT CODES  0 ok / 1 refused (dirty tree, existing branch) / 3 could not run

USAGE
    powershell -File governance/tools/setup-worktrees.ps1
    powershell -File governance/tools/setup-worktrees.ps1 -Apply
    powershell -File governance/tools/setup-worktrees.ps1 -Apply -Root C:\cb-worktrees
=============================================================================================
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$Root,
    [string]$IntegrationBranch = 'integration',
    [string]$BaseBranch,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$tabsPath = Join-Path $repo 'governance\registry\tab-ownership.json'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Write-Host 'git not on PATH' -ForegroundColor Red; exit 3 }
if (-not (Test-Path $tabsPath)) { Write-Host "registry not found: $tabsPath" -ForegroundColor Red; exit 3 }

if (-not $Root) { $Root = Join-Path (Split-Path $repo -Parent) 'crossbuy-worktrees' }

Push-Location $repo
try {
    $current = (git rev-parse --abbrev-ref HEAD 2>$null)
    if (-not $BaseBranch) { $BaseBranch = $current }
    $dirty = @(git status --porcelain 2>$null | Where-Object { $_ })

    Write-Host ''
    Write-Host 'R1 WORKTREE SETUP' -ForegroundColor Cyan
    Write-Host '================='
    Write-Host ("  repository        : {0}" -f $repo)
    Write-Host ("  current branch    : {0}" -f $current)
    Write-Host ("  base branch       : {0}" -f $BaseBranch)
    Write-Host ("  integration branch: {0}" -f $IntegrationBranch)
    Write-Host ("  worktree root     : {0}" -f $Root)
    Write-Host ("  uncommitted files : {0}" -f $dirty.Count)
    if ($Apply) { Write-Host '  mode              : APPLY' -ForegroundColor Yellow }
    else        { Write-Host '  mode              : DRY RUN (pass -Apply)' -ForegroundColor Green }

    if ($dirty.Count -and -not $AllowDirty) {
        Write-Host ''
        Write-Host ("REFUSED: {0} uncommitted change(s) in the working tree." -f $dirty.Count) -ForegroundColor Red
        Write-Host 'Every tab must commit its own work first (R1 exit criterion 1).' -ForegroundColor Red
        Write-Host 'This is the single largest process risk on record - the whole platform tree is untracked.' -ForegroundColor Yellow
        Write-Host 'Pass -AllowDirty only if you understand the branch will be created around this state.' -ForegroundColor Yellow
        exit 1
    }

    $tabs = @((Get-Content $tabsPath -Raw -Encoding UTF8 | ConvertFrom-Json).tabs |
              Where-Object { $_.tabId -ne 'TAB-0' })

    # branch name per tab, derived from the registry so it cannot drift from ownership
    function Get-BranchName($tab) {
        $slug = ($tab.tab -replace '^\s*\w+\s+Tab\s*-\s*', '') -replace '[^A-Za-z0-9]+', '-'
        return ('tab/' + $tab.tabId.ToLower() + '-' + $slug.Trim('-').ToLower())
    }

    Write-Host ''
    Write-Host 'PLAN'
    Write-Host ("  1. create branch '{0}' from '{1}' (if absent)" -f $IntegrationBranch, $BaseBranch)
    $n = 1
    foreach ($t in $tabs) {
        $n++
        $branch = Get-BranchName $t
        $dir = Join-Path $Root $t.tabId.ToLower()
        Write-Host ("  {0}. worktree {1,-46} branch {2}" -f $n, $dir, $branch)
    }

    if (-not $Apply) {
        Write-Host ''
        Write-Host 'DRY RUN - nothing created.' -ForegroundColor Green
        Write-Host ''
        Write-Host 'AFTER RUNNING WITH -Apply, protect the integration branch on the remote:' -ForegroundColor Cyan
        Write-Host '  - require a pull request; no direct pushes'
        Write-Host '  - require the integration-gate CI check to pass'
        Write-Host '  - require the branch to be up to date before merge'
        Write-Host '  - disallow force-push and deletion'
        Write-Host '  See governance/branch-protection.md for the exact settings.'
        exit 0
    }

    # ---- integration branch --------------------------------------------------------------
    $exists = (git branch --list $IntegrationBranch 2>$null)
    if ($exists) { Write-Host ("  branch '{0}' already exists - left alone" -f $IntegrationBranch) -ForegroundColor Yellow }
    else {
        git branch $IntegrationBranch $BaseBranch 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Host ("  created branch '{0}'" -f $IntegrationBranch) -ForegroundColor Green }
        else { Write-Host ("  FAILED to create '{0}'" -f $IntegrationBranch) -ForegroundColor Red; exit 1 }
    }

    if (-not (Test-Path $Root)) { New-Item -ItemType Directory -Path $Root -Force | Out-Null }

    foreach ($t in $tabs) {
        $branch = Get-BranchName $t
        $dir = Join-Path $Root $t.tabId.ToLower()
        if (Test-Path $dir) { Write-Host ("  worktree exists: {0}" -f $dir) -ForegroundColor Yellow; continue }
        $bExists = (git branch --list $branch 2>$null)
        if ($bExists) { git worktree add $dir $branch 2>&1 | Out-Null }
        else { git worktree add -b $branch $dir $IntegrationBranch 2>&1 | Out-Null }
        if ($LASTEXITCODE -eq 0) { Write-Host ("  worktree ready: {0}  [{1}]" -f $dir, $branch) -ForegroundColor Green }
        else { Write-Host ("  FAILED worktree: {0}" -f $dir) -ForegroundColor Red }
    }

    Write-Host ''
    git worktree list
    Write-Host ''
    Write-Host 'Now protect the integration branch on the remote - see governance/branch-protection.md' -ForegroundColor Cyan
    exit 0
}
finally { Pop-Location }
