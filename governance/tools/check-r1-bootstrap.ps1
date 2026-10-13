<# One-time integration-owner import of the explicitly approved immutable source snapshot.
   Ordinary tab ownership is unchanged. No application edits are permitted after that snapshot. #>
[CmdletBinding()]
param([string]$Base, [string]$Branch, [string]$Actor, [string]$RepositoryOwner,
      [string]$SourceRepository, [string]$TargetRepository)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$approved = '6abc412fdaa9f35a0b082204ee6baf8db1d98ef0'
$initialBase = '480c3d49f7b862b0662af161db824360e80ff5df'
$repository = 'ahmedzakareya/CrossBussines'
function Read-Git([string[]]$Arguments) {
    $result = & git -C $repo -c "safe.directory=$repo" -c core.safecrlf=false @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git validation failed: $($Arguments[0])" }
    return $result
}
try {
    if ($Base -ne $initialBase -or $Branch -cne 'tab/tab-1-r1-qualified-20261013' -or
        $RepositoryOwner -ne 'ahmedzakareya' -or $Actor -ne $RepositoryOwner -or
        $SourceRepository -ne $repository -or $TargetRepository -ne $repository) {
        throw 'Bootstrap is limited to the repository owner, original integration base and same-repository source.'
    }
    Read-Git -Arguments @('merge-base', '--is-ancestor', $approved, 'HEAD') | Out-Null
    # Governance repairs necessary to deliver this snapshot, never module implementation changes.
    $followup = @(
        '.github/workflows/integration-gate.yml',
        'governance/tools/integration-gate.ps1',
        'governance/tools/check-r1-bootstrap.ps1',
        'governance/tests/r1-bootstrap-tests.py',
        'docs/deployment/R1-Integration-Bootstrap.md'
    )
    $changed = @(Read-Git -Arguments @('diff', '--name-only', $approved, 'HEAD'))
    foreach ($path in $changed) {
        if ($path -in $followup) { continue }
        # The deterministic generator may normalize existing output line endings, but not change content.
        if ($path.StartsWith('docs/roadmap-v2/') -or $path.StartsWith('governance/registry/')) {
            $before = @(Read-Git -Arguments @('show', "${approved}:$path")) -join "`n"
            $after = @(Read-Git -Arguments @('show', "HEAD:$path")) -join "`n"
            if ($before -ceq $after) { continue }
        }
        throw "Unapproved post-snapshot edit: $path"
    }
    # Ownership assignments themselves must remain semantically identical, including all shared rules.
    foreach ($path in @('governance/registry/tab-ownership.json', 'governance/registry/shared-files.json')) {
        $before = (@(Read-Git -Arguments @('show', "${approved}:$path")) -join "`n" | ConvertFrom-Json | ConvertTo-Json -Depth 100 -Compress)
        $after = (@(Read-Git -Arguments @('show', "HEAD:$path")) -join "`n" | ConvertFrom-Json | ConvertTo-Json -Depth 100 -Compress)
        if ($before -cne $after) { throw "Ownership or shared approval rule changed: $path" }
    }
    Write-Host "PASS: repository-owner bootstrap; approved source $approved unchanged; $($changed.Count) confined governance follow-ups."
    Write-Host 'Existing shared/legacy/other-tab source is imported as the approved snapshot, not reassigned to TAB-1.'
    exit 0
} catch {
    Write-Host "FAIL: $_" -ForegroundColor Red
    exit 1
}
