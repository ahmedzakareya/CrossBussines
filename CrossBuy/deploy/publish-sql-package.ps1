<#
R1: generated compatibility SQL packaging. No SQL execution or database connections.
Default mode validates. -Apply preflights every origin before writing any package file.
#>
[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path (Split-Path $PSScriptRoot -Parent) -Parent))
. (Join-Path $PSScriptRoot 'validate-sql-package-path.ps1')
$originPath = Join-Path $PSScriptRoot 'package-sql-origin.json'
$origins = Get-Content -LiteralPath $originPath -Raw -Encoding UTF8 | ConvertFrom-Json
$seen = @{}
$plan = New-Object 'System.Collections.Generic.List[object]'
# PRE-FLIGHT ONLY: invalid later input must not partially refresh earlier files.
foreach ($entry in $origins.copies) {
    $canonical = Assert-SqlPackageRootPath $repo $entry.canonicalPath 'CrossBuy\deploy\sql'
    $package = Assert-SqlPackageRootPath $repo $entry.packagePath 'deploy\sql'
    # Compare resolved lexical paths, including slash/case/segment aliases.
    if ($seen.ContainsKey($package)) { throw 'Duplicate package origin' }
    $seen[$package] = $true
    if (-not (Test-Path -LiteralPath $canonical -PathType Leaf)) { throw 'Canonical SQL file missing' }
    if (Test-Path -LiteralPath $package -PathType Container) { throw 'SQL package destination is a directory' }
    if (-not (Test-Path -LiteralPath (Split-Path $package -Parent) -PathType Container)) {
        throw 'SQL package destination directory missing'
    }
    $hash = (Get-FileHash -LiteralPath $canonical -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $Apply -and ($hash -ne $entry.rawSha256 -or
        -not (Test-Path -LiteralPath $package -PathType Leaf) -or
        (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash)) {
        throw "Generated SQL package drift: $($entry.packagePath); review canonical change and republish"
    }
    $plan.Add([pscustomobject]@{ Canonical = $canonical; Package = $package; Hash = $hash; Entry = $entry })
}
# WRITE PHASE: reached only after every path, duplicate and input has passed preflight.
if ($Apply) {
    foreach ($item in $plan) {
        # Recheck before use; this is not an atomic transaction against a hostile filesystem.
        $null = Assert-SqlPackageRootPath $repo $item.Entry.canonicalPath 'CrossBuy\deploy\sql'
        $null = Assert-SqlPackageRootPath $repo $item.Entry.packagePath 'deploy\sql'
        if ((Get-FileHash -LiteralPath $item.Canonical -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.Hash) {
            throw 'Canonical SQL changed after preflight'
        }
        Copy-Item -LiteralPath $item.Canonical -Destination $item.Package -Force
        if ((Get-FileHash -LiteralPath $item.Package -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.Hash) {
            throw "Generated SQL package copy failed: $($item.Entry.packagePath)"
        }
        $item.Entry.rawSha256 = $item.Hash
    }
    $origins | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $originPath -Encoding UTF8
}
Write-Output ("Verified {0} generated SQL package copies; SQL executed: 0" -f $seen.Count)
