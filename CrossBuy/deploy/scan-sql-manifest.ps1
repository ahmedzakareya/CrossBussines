# =============================================================================================
# Stage 0 Batch B — deploy/sql manifest generator.
#
# Produces CrossBuy/deploy/sql/manifest.json: one entry per .sql file in the repository, with its
# SHA-256, a dependency-ordered rank, the idempotency guards actually found in the text, and any
# duplicate-name / duplicate-content collisions between the two deploy trees.
#
# WHY A GENERATOR RATHER THAN A HAND-WRITTEN MANIFEST: a hand-written list goes stale silently, and
# the whole point of the manifest is to be trustworthy about what is deployable. Re-run this after
# adding or editing any script:
#
#     powershell -NoProfile -ExecutionPolicy Bypass -File CrossBuy/deploy/scan-sql-manifest.ps1
#
# ENCODING: files are read with encoding DETECTION, not as UTF-8. deploy/sql/fresh/03_seed_config.sql
# is UTF-16LE; reading it as UTF-8 turns every keyword into mojibake and the guard scan silently sees
# an empty script. That defect is recorded in docs/architecture/CORRECTION-002.
# =============================================================================================
$ErrorActionPreference = 'Stop'

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent   # <repo>/CrossBuy/deploy -> <repo>
. (Join-Path $PSScriptRoot 'validate-sql-package-path.ps1')
$outFile = Join-Path $PSScriptRoot 'sql\manifest.json'

# ---------------------------------------------------------------------------------------------
# Guard patterns. Each is a real SQL Server idiom that makes a statement safe to re-run.
# ---------------------------------------------------------------------------------------------
$guardPatterns = [ordered]@{
    'object-null'      = 'IF\s+OBJECT_ID\s*\('
    'if-not-exists'    = 'IF\s+NOT\s+EXISTS\s*\('
    'if-exists'        = 'IF\s+EXISTS\s*\('
    'where-not-exists' = '(WHERE|AND)\s+NOT\s+EXISTS\s*\('
    'col-length'       = 'COL_LENGTH\s*\('
    'columnproperty'   = 'COLUMNPROPERTY\s*\('
    'db-id'            = 'IF\s+DB_ID\s*\('
    'create-or-alter'  = 'CREATE\s+OR\s+ALTER'
    'drop-if-exists'   = 'DROP\s+\w+\s+IF\s+EXISTS'
    'sys-catalog'      = 'FROM\s+sys\.'
    'merge'            = '^\s*MERGE\s'
}

# Statements that CHANGE something and therefore need a guard to be re-runnable.
$mutatingPatterns = [ordered]@{
    'create-table'  = '(?im)^\s*CREATE\s+TABLE\s'
    'alter-add'     = '(?im)(?:^\s*|^\s*IF\s+COL_LENGTH\([^\r\n]*?\)\s+IS\s+NULL\s+)ALTER\s+TABLE\s+\S+\s+ADD\s'
    'alter-drop'    = '(?im)^\s*ALTER\s+TABLE\s+\S+\s+DROP\s'
    'create-index'  = '(?im)^\s*CREATE\s+(UNIQUE\s+)?(CLUSTERED\s+|NONCLUSTERED\s+)?INDEX\s'
    'insert'        = '(?im)^\s*INSERT\s+INTO\s'
    'update'        = '(?im)^\s*UPDATE\s'
    'delete'        = '(?im)^\s*DELETE\s'
    'drop'          = '(?im)^\s*DROP\s+(TABLE|INDEX|VIEW|PROCEDURE)\s'
    'exec-ddl'      = '(?im)^\s*EXEC\s*\(\s*N?''ALTER'
    'create-proc'   = '(?im)^\s*CREATE\s+(OR\s+ALTER\s+)?(PROCEDURE|PROC|VIEW|FUNCTION|TRIGGER)\s'
    # ALTER VIEW/PROCEDURE mutates too. Without this the scanner would see a batch containing only an
    # ALTER VIEW as non-mutating and report the file clean — a silent gap, which is worse than a flag.
    'alter-module'  = '(?im)^\s*ALTER\s+(PROCEDURE|PROC|VIEW|FUNCTION|TRIGGER)\s'
    'sp-rename'     = '(?im)sp_rename'
}

# Reads a file, honouring its byte-order mark. Without this, UTF-16 content scans as garbage.
function Read-SqlText([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        return @{ text = [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3); encoding = 'utf-8-bom' }
    }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        return @{ text = [System.Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2); encoding = 'utf-16le-bom' }
    }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        return @{ text = [System.Text.Encoding]::BigEndianUnicode.GetString($bytes, 2, $bytes.Length - 2); encoding = 'utf-16be-bom' }
    }
    # No BOM. A UTF-16LE file without a BOM shows as alternating NUL bytes; detect that rather than guess.
    $nulls = 0
    for ($i = 1; $i -lt [Math]::Min($bytes.Length, 200); $i += 2) { if ($bytes[$i] -eq 0) { $nulls++ } }
    if ($nulls -gt 40) { return @{ text = [System.Text.Encoding]::Unicode.GetString($bytes); encoding = 'utf-16le-nobom' } }
    return @{ text = [System.Text.Encoding]::UTF8.GetString($bytes); encoding = 'utf-8' }
}

# Strips comments so a guard mentioned in prose is not counted as a guard in code.
function Remove-SqlComments([string]$text) {
    $text = [regex]::Replace($text, '/\*.*?\*/', ' ', 'Singleline')
    $text = [regex]::Replace($text, '(?m)--.*$', ' ')
    return $text
}

function Get-CanonicalSqlBytes([string]$path) {
    $rawBytes = [System.IO.File]::ReadAllBytes($path)
    $canonical = New-Object 'System.Collections.Generic.List[byte]'
    for ($index = 0; $index -lt $rawBytes.Length; $index++) {
        if ($rawBytes[$index] -eq 13 -and $index + 1 -lt $rawBytes.Length -and $rawBytes[$index + 1] -eq 10) { continue }
        $canonical.Add($rawBytes[$index])
    }
    return ,$canonical.ToArray()
}
function Get-Sha256([string]$path, [switch]$Canonical) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        [byte[]]$bytes = if ($Canonical) { Get-CanonicalSqlBytes $path } else { [System.IO.File]::ReadAllBytes($path) }
        return ([System.BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

# ---------------------------------------------------------------------------------------------
# Dependency ranking. Lower rank must be applied first. Ranks are coarse on purpose: within a rank
# the scripts are independent (each guards its own objects), so ordering inside a rank is free.
# ---------------------------------------------------------------------------------------------
function Get-Rank([string]$relative, [string]$name) {
    if ($relative -like '*deploy/sql/fresh/*') {
        switch -Wildcard ($name) {
            '00_create_database.sql' { return 0 }
            '01_schema.sql'          { return 1 }
            '02_foreign_keys.sql'    { return 2 }
            '03_seed_config.sql'     { return 3 }
        }
        return 3
    }
    # Platform kernel: slice 1 is a hard prerequisite of slice 2 AND of the sale/reversal path.
    if ($name -eq 'platform_business_events.sql')            { return 10 }
    if ($name -eq 'platform_business_events_slice_002.sql')  { return 11 }
    # Base module schemas that later deltas alter.
    if ($name -in @('pos_schema_master.sql', 'pos_setup.sql', 'manuf_schema_4x.sql', 'projects_contracting.sql',
                    'notifications_hub_p1.sql', 'comm_mail.sql', 'tasks_tm1.sql', 'crm_schema_updates_3x.sql',
                    'brand_foundation.sql', 'schema_sync_2026-07.sql')) { return 20 }
    # Additive deltas on top of a base.
    if ($name -eq 'comm_outbox_slice_003.sql')               { return 31 }
    if ($name -like 'notification_mutes*')                   { return 31 }
    if ($name -like 'pos_*' -or $name -like 'hm*' -or $name -like 'hyper_*') { return 30 }
    if ($name -like 'tasks_*' -or $name -like 'projects_*' -or $name -like 'crm_*' -or
        $name -like 'hr*' -or $name -like 'pricing_*' -or $name -like 'manuf_*') { return 30 }
    # Utility / operational scripts that are never part of an ordinary deploy.
    if ($name -in @('00_backup_dev_db.sql', '10_purge_for_production.sql', 'script.sql')) { return 90 }
    return 40
}

# ---------------------------------------------------------------------------------------------
# MANUAL REVIEW LEDGER.
#
# The automated scan flags a mutating batch with no RECOGNISED guard. That is a signal, not a verdict:
# a plain "UPDATE t SET c = x WHERE c IS NULL" is perfectly re-runnable — the second run matches zero
# rows — and no textual guard pattern will ever see it. Rather than widen the regex until it stops
# flagging things (which is how a scanner starts lying), each flagged file is READ and its verdict
# recorded here with the evidence. Anything flagged and NOT in this ledger stays 'review' in the
# manifest and must not be deployed until someone reads it.
#
# Reviewed 2026-08-03 for Stage 0 Batch B. Re-review whenever the scan flags a new batch.
# ---------------------------------------------------------------------------------------------
$reviewLedger = @{
    'CrossBuy/deploy/sql/hr_document_attachments.sql' = @{
        verdict = 'idempotent'
        sha256 = '49224c72aa826cff09ed4e09f4c7c146eadb751036052e66e645fd9d2d9558f6'
        canonicalHash = $true
        dynamicMutations = $true
        evidence = 'R1 revision 03: dynamic DDL and company-aware attachment backfill, serialized by transaction-owned application lock and table locks. Qualified by 33 isolated SQL Server cases, with verified cleanup; governance/evidence/r1-accounting-hr-sql-matrix.json. Not a no-mutation script.'
    }
    'deploy/sql/hr_document_attachments.sql' = @{
        verdict = 'idempotent'
        sha256 = '49224c72aa826cff09ed4e09f4c7c146eadb751036052e66e645fd9d2d9558f6'
        canonicalHash = $true
        dynamicMutations = $true
        evidence = 'Generated compatibility copy of the qualified revision 03 canonical HR successor; dynamic backfill is mutating and must not be classified as no-mutation.'
    }
    'CrossBuy/deploy/sql/crm_schema_updates_3x.sql' = @{
        verdict = 'idempotent'
        sha256 = '56abbcda4f516335e36651edc7c8206aefd66c9950668c42bb4ac45e075568b3'
        evidence = 'Canonical R1 migration: byte-identical to the hash-verified reviewed package input. Tables/columns are guarded (OBJECT_ID / COL_LENGTH, including a sp_executesql loop). The flagged batch backfills Activities with "WHERE EntityType IS NULL", and the migration INSERT carries WHERE NOT EXISTS. All predicate-idempotent.'
    }

    'CrossBuy/deploy/sql/notifications_hub_p1.sql' = @{
        verdict = 'idempotent'
        sha256 = '3f318e0860c934d3c01ae38a0c98932a128e8857983fd68f483c06ef55eab50d'
        evidence = 'Canonical R1 migration: byte-identical to the hash-verified reviewed package input. Indexes are IF NOT EXISTS guarded. The flagged batch is "UPDATE n SET n.CompanyID = e.EmpCompanyID ... WHERE n.CompanyID IS NULL" — predicate-idempotent.'
    }

    'CrossBuy/deploy/sql/pos_fix_preset_names.sql' = @{
        verdict = 'idempotent'
        sha256 = '7ecb000a4457b9858956281213171d36aa3f5cf5ede590e2e3e2fd9a7391fb8d'
        evidence = 'Canonical R1 migration: byte-identical to the hash-verified reviewed package input. Four UPDATEs that assign a CONSTANT value keyed on Code. Re-running writes the identical value, so the end state is the same however many times it runs. Requires sqlcmd -f 65001 or the Arabic literals are stored as mojibake.'
    }

    'CrossBuy/deploy/sql/i18n_english_name_columns.sql' = @{
        verdict = 'idempotent'
        sha256 = '34e8c4216beaa29617aa56ee0bf74e560307257ea24d4553498874dab089e913'
        evidence = 'Canonical R1 migration: byte-identical to the hash-verified reviewed package input. Reviewed R1: ten column additions use COL_LENGTH; Type repairs target only the old Arabic codes, and English seed-title backfills require TitleEn IS NULL and exact known titles. Second apply changes no repaired rows. Not applied by this review.'
    }

    'CrossBuy/deploy/sql/platform_ai_projections_slice_003.sql' = @{
        verdict = 'idempotent'
        sha256 = 'a0746a04d8bc3cacd2fcd6aadf6be5fa7925bcc2dab24ba470c8db83cbb90209'
        evidence = 'Reviewed R1 without execution: guarded nullable column additions; deterministic expiry backfill affects only ExpiresAtUtc IS NULL and never extends an existing expiry; index and constraint additions have catalog guards. Requires the existing AiProjections schema. AI remains deferred; this technical rerun review does not authorize activation or prove fresh-database prerequisites.'
    }
    'CrossBuy/deploy/sql/hm16_rename_grni.sql' = @{
        verdict = 'idempotent'
        sha256 = 'f2578a330bf091234ee01b8adafb37a6bd12003ef3e9d3c41106a58800ed867b'
        evidence = 'Reviewed R1: UPDATE is constrained to Code210203 and NameEn different from the constant target; after that assignment a second run matches no rows. Trailing SELECT is read-only. This is a technical rerun review, not authorization to rename live accounts.'
    }
    'CrossBuy/deploy/sql/comm_chat_identity_001.sql' = @{
        verdict = 'idempotent'
        sha256 = '2605ac2af0e921c09b70453f7e06833dac45cf8f2687e4bfb5ce0a7972e2366d'
        evidence = 'Reviewed R1: column/index additions are guarded; direct-pair backfill affects only rows whose keys remain NULL and obtains a deterministic MIN/MAX pair from exactly two distinct members. Duplicate-pair refusal remains. Reapply does not merge/delete conversations. Requires owning tables and QUOTED_IDENTIFIER ON; not applied by this review.'
    }
    'deploy/sql/i18n_english_name_columns.sql' = @{
        verdict = 'idempotent'
        sha256 = '34e8c4216beaa29617aa56ee0bf74e560307257ea24d4553498874dab089e913'
        evidence = 'Reviewed R1: ten column additions use COL_LENGTH; Type repairs target only the old Arabic codes, and English seed-title backfills require TitleEn IS NULL and exact known titles. Second apply changes no repaired rows. Not applied by this review.'
    }

    'CrossBuy/deploy/sql/hm_d34_relabel_shell_company_refs.sql' = @{
        verdict = 'idempotent'
        evidence = 'Four UPDATEs keyed on membership of the shell-company set (CompanyID IN (65,71,79)). The first run empties that set, so a second run matches zero rows. The trailing SELECTs are read-only verification.'
    }
    'CrossBuy/deploy/sql/hm_d38_branch_tax.sql' = @{
        verdict = 'idempotent'
        evidence = 'Batch 1 is COLUMN-guarded via INFORMATION_SCHEMA.COLUMNS. Batch 2 is guarded by IF @branch IS NOT NULL AND @vatex IS NOT NULL (a variable null-check, which no guard pattern recognises) and the UPDATE predicate excludes rows already carrying the target value.'
    }
    'CrossBuy/deploy/sql/pos_delivery_c1.sql' = @{
        verdict = 'idempotent'
        evidence = 'Tables use IF OBJECT_ID IS NULL, columns use COL_LENGTH IS NULL, the seed uses IF NOT EXISTS. The flagged batch is "UPDATE BranchPosSettings SET DefaultDeliveryFee = 25 WHERE BranchId = 15 AND DefaultDeliveryFee IS NULL" — predicate-idempotent.'
    }
    'CrossBuy/deploy/sql/pos_station_nameen.sql' = @{
        verdict = 'idempotent'
        evidence = 'Column added under COL_LENGTH IS NULL. The four UPDATEs all carry "(NameEn IS NULL OR NameEn = '''')", so a second run matches zero rows.'
    }
    'deploy/sql/crm_schema_updates_3x.sql' = @{
        verdict = 'idempotent'
        evidence = 'Tables/columns are guarded (OBJECT_ID / COL_LENGTH, including a sp_executesql loop). The flagged batch backfills Activities with "WHERE EntityType IS NULL", and the migration INSERT carries WHERE NOT EXISTS. All predicate-idempotent.'
    }
    'deploy/sql/notifications_hub_p1.sql' = @{
        verdict = 'idempotent'
        evidence = 'Indexes are IF NOT EXISTS guarded. The flagged batch is "UPDATE n SET n.CompanyID = e.EmpCompanyID ... WHERE n.CompanyID IS NULL" — predicate-idempotent.'
    }
    'deploy/sql/pos_fix_preset_names.sql' = @{
        verdict = 'idempotent'
        evidence = 'Four UPDATEs that assign a CONSTANT value keyed on Code. Re-running writes the identical value, so the end state is the same however many times it runs. Requires sqlcmd -f 65001 or the Arabic literals are stored as mojibake.'
    }
    'script.sql' = @{
        verdict = 'not-deployable'
        evidence = 'Unversioned scratch script at the repository root: ~40 unguarded CREATE TABLE / CREATE INDEX batches. Excluded from every deployment; kept only as history.'
    }
}

# Scripts that must NEVER run as part of a routine deployment, with the reason.
$excluded = @{
    '00_backup_dev_db.sql'      = 'Dev-only backup helper. Not part of any deployment.'
    '10_purge_for_production.sql' = 'DESTRUCTIVE: purges demo data. Manual, deliberate, backup first.'
    'script.sql'                = 'Unversioned scratch script at the repository root. Not deployable.'
}

# ---------------------------------------------------------------------------------------------
# Scan
# ---------------------------------------------------------------------------------------------
# Exact unresolved Class-D paths are held outside the deployable authored register.
$debtPath = Join-Path $PSScriptRoot 'sql\manifest-known-debt.json'
$deferredPaths = @()
if (Test-Path -LiteralPath $debtPath) {
    $deferredPaths = @((Get-Content -LiteralPath $debtPath -Raw -Encoding UTF8 | ConvertFrom-Json).debt | Where-Object { $_.class -eq 'D' } | ForEach-Object { $_.path })
}

$files = Get-ChildItem -Path $repo -Recurse -Filter *.sql -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|node_modules|\.git)\\' } |
    Where-Object { $_.FullName -notmatch '\\tools\\testdata\\' } |
    Sort-Object FullName

$entries = @()
$packageOrigins = @{}
$originPath = Join-Path $PSScriptRoot 'package-sql-origin.json'
if (Test-Path -LiteralPath $originPath) {
    foreach ($origin in (Get-Content -LiteralPath $originPath -Raw -Encoding UTF8 | ConvertFrom-Json).copies) {
        if ($packageOrigins.ContainsKey($origin.packagePath)) { throw 'Duplicate generated SQL package path' }
        if ($origin.packagePath -notlike 'deploy/sql/*.sql' -or $origin.canonicalPath -notlike 'CrossBuy/deploy/sql/*.sql' -or
            $origin.packagePath.Contains('..') -or $origin.canonicalPath.Contains('..')) { throw 'Invalid SQL package origin path' }
        $canonical = Assert-SqlPackageRootPath $repo $origin.canonicalPath 'CrossBuy\deploy\sql'
        $null = Assert-SqlPackageRootPath $repo $origin.packagePath 'deploy\sql'
        if ((Get-Sha256 $canonical) -ne $origin.rawSha256 -or
            (Get-Sha256 (Join-Path $repo $origin.packagePath)) -ne $origin.rawSha256) { throw 'Generated SQL package drift; republish before scanning' }
        $packageOrigins[$origin.packagePath] = $origin
    }
}
foreach ($f in $files) {
    $relative = $f.FullName.Substring($repo.Length).TrimStart('\', '/').Replace('\', '/')
    if ($deferredPaths -contains $relative) { continue }
    $read = Read-SqlText $f.FullName
    $code = Remove-SqlComments $read.text

    $guards = @()
    foreach ($k in $guardPatterns.Keys) {
        if ([regex]::IsMatch($code, $guardPatterns[$k], 'IgnoreCase')) { $guards += $k }
    }
    $mutations = @()
    foreach ($k in $mutatingPatterns.Keys) {
        if ([regex]::IsMatch($code, $mutatingPatterns[$k])) { $mutations += $k }
    }

    # Batch counting: a GO-separated batch that mutates and contains no guard is the thing that makes a
    # script non-re-runnable, so guards are counted PER BATCH rather than per file. A file-level scan
    # would call a script safe because some OTHER batch in it happened to have a guard.
    $unguardedBatches = @()
    $batchIndex = 0
    foreach ($batch in [regex]::Split($code, '(?im)^\s*GO\s*$')) {
        $batchIndex++
        if ([string]::IsNullOrWhiteSpace($batch)) { continue }
        $batchMutates = $false
        foreach ($k in $mutatingPatterns.Keys) { if ([regex]::IsMatch($batch, $mutatingPatterns[$k])) { $batchMutates = $true; break } }
        if (-not $batchMutates) { continue }
        $batchGuarded = $false
        foreach ($k in $guardPatterns.Keys) { if ([regex]::IsMatch($batch, $guardPatterns[$k], 'IgnoreCase')) { $batchGuarded = $true; break } }
        if (-not $batchGuarded) { $unguardedBatches += $batchIndex }
    }

    $status = if ($mutations.Count -eq 0) { 'no-mutation' }
              elseif ($unguardedBatches.Count -eq 0) { 'guarded' }
              else { 'review' }

    # A flagged file that has been read and judged carries its verdict forward. Everything else stays
    # 'review' — the ledger can only DOWNGRADE the alarm for a file someone actually looked at.
    $reviewVerdict = $null
    $reviewEvidence = $null
    # Lexical patterns do not see DDL/DML inside dynamic SQL strings. A reviewed
    # dynamic contract is mutating; changed bytes fail closed until reviewed again.
    if ($status -eq 'no-mutation' -and $reviewLedger.ContainsKey($relative) -and
        $reviewLedger[$relative].dynamicMutations) {
        $status = 'review'
        $mutations += 'dynamic-sql-contract'
    }
    if ($status -eq 'review' -and $reviewLedger.ContainsKey($relative) -and
        (-not $reviewLedger[$relative].sha256 -or $reviewLedger[$relative].sha256 -eq
            (Get-Sha256 $f.FullName -Canonical:([bool]$reviewLedger[$relative].canonicalHash)))) {
        $reviewVerdict = $reviewLedger[$relative].verdict
        $reviewEvidence = $reviewLedger[$relative].evidence
        if ($reviewVerdict -eq 'idempotent') { $status = 'guarded-by-predicate' }
        elseif ($reviewVerdict -eq 'not-deployable') { $status = 'not-deployable' }
    }

    # [pscustomobject] on top of [ordered] is deliberate, not decoration: an OrderedDictionary looks like it has
    # properties but Group-Object cannot read them, and it silently groups EVERY row under one empty key instead of
    # erroring. Casting to a PSCustomObject keeps the key order for the JSON while giving real properties.
    $entries += [pscustomobject][ordered]@{
        path              = $relative
        name              = $f.Name
        bytes             = (Get-CanonicalSqlBytes $f.FullName).Length
        rawBytes          = $f.Length
        encoding          = $read.encoding
        sha256            = Get-Sha256 $f.FullName -Canonical
        rawSha256         = Get-Sha256 $f.FullName
        rank              = Get-Rank $relative $f.Name
        guards            = $guards
        mutations         = $mutations
        unguardedBatches  = $unguardedBatches
        idempotency       = $status
        reviewVerdict     = $reviewVerdict
        reviewEvidence    = $reviewEvidence
        authoredSourcePath = if ($packageOrigins.ContainsKey($relative)) { $packageOrigins[$relative].canonicalPath } else { $relative }
        isGeneratedPackageCopy = $packageOrigins.ContainsKey($relative)
        excludedFromDeploy = $excluded.ContainsKey($f.Name) -or $packageOrigins.ContainsKey($relative)
        excludedReason    = if ($packageOrigins.ContainsKey($relative)) { 'Generated compatibility copy; deploy its canonical authored source exactly once.' } elseif ($excluded.ContainsKey($f.Name)) { $excluded[$f.Name] } else { $null }
    }
}

# ---------------------------------------------------------------------------------------------
# Collisions. Two kinds, and they mean different things:
#   duplicateNames   — the SAME file name in two trees with DIFFERENT content. Dangerous: "apply
#                      pos_setup.sql" is ambiguous, and the two files are not interchangeable.
#   duplicateContent — byte-identical files under different names/paths. Harmless but wasteful.
# ---------------------------------------------------------------------------------------------
$duplicateNames = @()
# Self-check: if grouping ever collapses to a single empty key again, fail loudly instead of publishing a
# manifest that claims there are no duplicates.
$nameGroups = @($entries | Group-Object -Property name)
if ($nameGroups.Count -le 1 -and $entries.Count -gt 1) {
    throw "duplicate-name grouping produced $($nameGroups.Count) group(s) for $($entries.Count) scripts — the property scan is broken."
}
foreach ($g in ($nameGroups | Where-Object Count -gt 1)) {
    $hashes = $g.Group.sha256 | Select-Object -Unique
    $duplicateNames += [ordered]@{
        name           = $g.Name
        paths          = @($g.Group.path)
        identical      = ($hashes.Count -eq 1)
        distinctHashes = $hashes.Count
    }
}
$duplicateContent = @()
foreach ($g in ($entries | Group-Object -Property sha256 | Where-Object Count -gt 1)) {
    $duplicateContent += [ordered]@{ sha256 = $g.Name; paths = @($g.Group.path) }
}

$manifest = [ordered]@{
    '$comment' = @(
        'Generated by CrossBuy/deploy/scan-sql-manifest.ps1 — do not hand-edit; re-run the generator.',
        'rank = apply order (ascending). Scripts sharing a rank are independent of each other.',
        'idempotency values:',
        '  guarded              = every mutating GO-batch carries a recognised re-run guard.',
        '  guarded-by-predicate = a batch has no textual guard but was READ and judged re-runnable',
        '                         (e.g. UPDATE ... WHERE col IS NULL). See reviewEvidence.',
        '  review               = a mutating batch has no guard and nobody has read it. DO NOT DEPLOY.',
        '  not-deployable       = read and judged unsafe to re-run; excluded from every deployment.',
        '  no-mutation          = the file changes nothing (comments, SELECTs, PRINTs only).',
        'excludedFromDeploy scripts must never run as part of a routine deployment.',
        'sha256 and bytes are CRLF-normalized authored identities; rawSha256/rawBytes identify the exact deployment file bytes.',
        'duplicateNames with identical=false are the real hazard: the same file name in two trees with',
        'different content, so "apply <name>.sql" is ambiguous.'
    )
    generatedFrom = 'repository scan'
    totalScripts  = $entries.Count
    counts        = [ordered]@{
        guarded            = @($entries | Where-Object { $_.idempotency -eq 'guarded' }).Count
        guardedByPredicate = @($entries | Where-Object { $_.idempotency -eq 'guarded-by-predicate' }).Count
        review             = @($entries | Where-Object { $_.idempotency -eq 'review' }).Count
        notDeployable      = @($entries | Where-Object { $_.idempotency -eq 'not-deployable' }).Count
        noMutation         = @($entries | Where-Object { $_.idempotency -eq 'no-mutation' }).Count
        excluded           = @($entries | Where-Object { $_.excludedFromDeploy }).Count
    }
    duplicateNames   = $duplicateNames
    duplicateContent = $duplicateContent
    scripts          = @($entries | Sort-Object rank, path)
}

$json = $manifest | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText($outFile, $json, (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("manifest: {0}" -f $outFile)
Write-Host ("  scripts              : {0}" -f $manifest.totalScripts)
Write-Host ("  guarded              : {0}" -f $manifest.counts.guarded)
Write-Host ("  guarded-by-predicate : {0}" -f $manifest.counts.guardedByPredicate)
Write-Host ("  review (UNREAD)      : {0}" -f $manifest.counts.review)
Write-Host ("  not-deployable       : {0}" -f $manifest.counts.notDeployable)
Write-Host ("  no-mutation          : {0}" -f $manifest.counts.noMutation)
Write-Host ("  excluded from deploy : {0}" -f $manifest.counts.excluded)
Write-Host ("  duplicate names      : {0}" -f $duplicateNames.Count)
Write-Host ("  duplicate content    : {0}" -f $duplicateContent.Count)

foreach ($d in ($duplicateNames | Where-Object { -not $_.identical })) {
    Write-Host ("`nNAME COLLISION with DIFFERENT content: {0}" -f $d.name) -ForegroundColor Yellow
    $d.paths | ForEach-Object { Write-Host ("    " + $_) }
}

if ($manifest.counts.review -gt 0) {
    Write-Host "`nNEEDS REVIEW — a mutating batch with no guard that nobody has read. Read it, then add a" -ForegroundColor Yellow
    Write-Host "verdict to `$reviewLedger in this script. Do NOT widen the guard regex to silence it." -ForegroundColor Yellow
    $entries | Where-Object { $_.idempotency -eq 'review' } | ForEach-Object {
        Write-Host ("  {0}  batches={1}  mutations={2}" -f $_.path, ($_.unguardedBatches -join ','), ($_.mutations -join ','))
    }
}
