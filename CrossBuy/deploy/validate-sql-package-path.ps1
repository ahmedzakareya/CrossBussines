# Packaging-only path validation. Does not execute SQL or connect to a database.
function Assert-SqlPackageRootPath([string]$repo, [string]$relative, [string]$declaredRoot) {
    if ([string]::IsNullOrWhiteSpace($relative)) { throw 'Missing SQL package origin path' }
    $root = [IO.Path]::GetFullPath((Join-Path $repo $declaredRoot))
    $full = [IO.Path]::GetFullPath((Join-Path $repo $relative))
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'SQL origin path escapes its declared root'
    }
    # GetFullPath is lexical: junctions/symlinks can still redirect a path outside this root.
    # Reject reparse points from the file through the repository root before any read or copy.
    $cursor = $full
    while ($cursor -and $cursor.Length -ge $repo.Length) {
        if (Test-Path -LiteralPath $cursor) {
            $attributes = [IO.File]::GetAttributes($cursor)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'SQL package origin traverses a reparse point'
            }
        }
        if ($cursor -eq $repo) { break }
        $cursor = Split-Path -Path $cursor -Parent
    }
    return $full
}
