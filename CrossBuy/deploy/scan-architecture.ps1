# =============================================================================================
# Stage 0 Batch B — architecture evidence regenerator.
#
# Rebuilds the MECHANICAL evidence CSVs under docs/architecture/evidence from source:
#
#     Endpoint-Inventory.csv       every controller action, its HTTP verb and its guards
#     Permission-Coverage.csv      the same set, projected onto the authorization question
#     Controller-Inventory.csv     one row per controller
#     Worker-Inventory.csv         one row per BackgroundService
#     SQL-Script-Inventory.csv     projected from deploy/sql/manifest.json (ONE source of truth)
#
# WHY THIS EXISTS AS A CHECKED-IN SCRIPT
#
# The first architecture discovery was produced by ad-hoc scans that were not kept. Three of them had
# defects, and one of those defects produced a FALSE Critical security finding (see
# docs/architecture/CORRECTION-001 and CORRECTION-002). An un-kept scanner cannot be reviewed, cannot be
# re-run, and cannot be corrected — so the evidence it produces has to be taken on trust. This script is
# the fix for that: the derivation is now readable, re-runnable and diffable.
#
# THE THREE CORRECTED DEFECTS, stated where they were wrong:
#
#   1. ATTRIBUTE WALK. The original regex was '^\s*\[([^\]]+)\]\s*$' — one attribute per line, nothing
#      after it. Real code writes [HttpPost][ValidateAntiForgeryToken][InvPerm("doc")] on ONE line, and
#      the walk aborted at the first line it could not match, recording "(none)". That is what produced
#      the false claim that Manufacturing work-order writes were unguarded. The walk below collects ALL
#      bracket groups on a line and keeps walking through comments and blank lines.
#
#   2. WRITES. The original heuristic searched the method body for '.Add(' — which matched
#      List.Add/Dictionary.Add in read-only report actions, and MISSED real POSTs that mutate through a
#      service. "Mutating" is now the HTTP verb, which is what actually decides whether a request can
#      change state, and it is not a heuristic at all.
#
#   3. SQL. Both a too-narrow guard regex and a UTF-8-only read (deploy/sql/fresh/03_seed_config.sql is
#      UTF-16LE, so it scanned as mojibake). SQL is no longer scanned here: this script PROJECTS
#      deploy/sql/manifest.json, so the SQL verdict has exactly one derivation.
#
# NOT REGENERATED HERE, deliberately: Business-Object-Capabilities, Module-Dependency-Matrix,
# Approval-Silo-Matrix, Entity/DbSet/Service/Screen/Hub inventories and the event matrix. Those involve
# judgement (what counts as a capability, which dependency is real) or were unaffected by the three
# defects; re-deriving them mechanically would trade reviewed content for fresh risk.
#
#     powershell -NoProfile -ExecutionPolicy Bypass -File CrossBuy/deploy/scan-architecture.ps1
# =============================================================================================
$ErrorActionPreference = 'Stop'

$repo     = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$evidence = Join-Path $repo 'docs\architecture\evidence'
$manifest = Join-Path $PSScriptRoot 'sql\manifest.json'
if (-not (Test-Path $evidence)) { New-Item -ItemType Directory -Path $evidence -Force | Out-Null }

function Rel([string]$full) { $full.Substring($repo.Length).TrimStart('\', '/').Replace('\', '/') }

# ---------------------------------------------------------------------------------------------
# Module attribution, from the controller name. Deliberately a lookup with an explicit fallback
# rather than a clever inference: a wrong module label silently mis-files a security finding.
# ---------------------------------------------------------------------------------------------
function Get-Module([string]$controller) {
    switch -Regex ($controller) {
        '^Ai'                     { return 'AI' }
        '^Account$|^Account'      { return 'Identity' }
        '^Accounting|^Currency'   { return 'Accounting' }
        '^Admin|^Service$|^People' { return 'HR' }
        '^Announcements|^Comm|^Chat|^Notifications|^Calendar|^FileManager|^Library' { return 'Communication' }
        '^Approvals'              { return 'Workflow' }
        '^Brand|^Store'           { return 'Retail' }
        '^BusinessEventMonitor|^PlatformTimeline' { return 'Platform' }
        '^Comments'               { return 'Communication' }
        '^Crm'                    { return 'CRM' }
        '^DevSeed'                { return 'DevTools' }
        '^Home$|^Portal'          { return 'Shell' }
        '^Inventory|^Manuf'       { return 'Inventory' }
        '^Pos'                    { return 'POS' }
        '^Project'                { return 'Projects' }
        '^Tasks'                  { return 'Tasks' }
        default                   { return 'Other' }
    }
}

# Guards we care about, and what each one actually proves.
#
# PosLaneActivityGuard and DevOnly are included because they ARE real IActionFilter gates, not decoration:
# the lane guard whitelists which activity a cashier lane may serve, and DevOnly 404s /api/dev/* outside
# Development. Omitting them counted their actions as unguarded. `PosPerm`/`HrPerm`/`ProjectPerm` do not
# exist yet and are listed so that adding one is picked up automatically.
# CORRECTION-004 (Stage 1 Batch B / B6): PosLaneActivityGuard and DevOnly were REMOVED from this list.
#
# Neither is a permission:
#   * PosLaneActivityGuardAttribute compares the branch's ActivityPresetCode against the lane (restaurant vs
#     hypermarket) and redirects on a mismatch. It checks NO role, and when the session is absent or malformed it
#     calls next() — so it does not even require authentication. Counting it credited 44 mutating POS actions
#     (36 PosAppController + 8 HyperPosController) as "protected by a module permission".
#   * DevOnly is an environment gate. It happened to match 0 mutating actions, so it changed no number, but it had
#     no business being on a permission list.
#
# The 44 POS actions ARE authorized — in-body, via IPosAccessService, which an attribute scanner cannot see. They
# are therefore counted separately below ($inBodyAuthorized) rather than dropped into the gap, which is why the
# headline backlog stays 189 while its BASIS changes. See docs/architecture/CORRECTION-004-*.md.
$permissionAttributes = @('AccPerm', 'InvPerm', 'CrmPerm', 'PlatformOps', 'PosPerm', 'HrPerm', 'ProjectPerm', 'ApiPerm')
# Stage 1 F1 (scanner modernization): ApiPerm joined the list. It is the API-SAFE guard added in Batch D1 Wave 1
# for JSON endpoints (401/403 in the project's JSON shape instead of an MVC redirect). It asks the SAME access
# service for the SAME action as AccPerm/InvPerm, so omitting it made five genuinely-protected endpoints read as
# unprotected. PlatformOps was already here, and was verified to check a real role before being credited.

# =====================================================================================================
# Stage 1 F1 - the authorization call-graph resolver
#
#   Test-DirectAuthorization  does this body ASK an authorization authority?
#   Get-AuthorizingHelpers    which private methods in this file do, transitively (to a fixpoint)?
#   Test-AuthorizingBody      does an action authorize directly, or call such a helper?
#
# The authority patterns name real authorities - an access service, the platform permission provider, the Hotfix A.1
# guard, the role directory. A method merely CONTAINING "Auth" is never credited: that was CORRECTION-004's lesson
# about crediting a control that checks nothing.
# =====================================================================================================

$script:AuthorityPatterns = @(
    '_access\.(Can|Is|Has)[A-Za-z]*\s*\(',
    '\b(CanAsync|RolesAsync|AnyConfiguredAsync)\s*\(',
    '_guard\.AuthorizeAsync\s*\(',
    '_permissions\(\)\.CanAsync\s*\(',
    '_posAccess\.',
    '_projectsAccess\.',
    '_tasksAccess\.',
    '_accounting\.CanAsync\s*\(',
    'HrAccess\.CanAsync\s*\('
)

function Test-DirectAuthorization {
    param([string]$Body)
    if ([string]::IsNullOrWhiteSpace($Body)) { return $false }
    foreach ($p in $script:AuthorityPatterns) { if ($Body -match $p) { return $true } }
    return $false
}

function Get-AuthorizingHelpers {
    param($Lines)

    $methods = @{}
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $m = [regex]::Match($Lines[$i], '^\s{0,12}(private|protected|internal)\s[^=;]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*\(')
        if (-not $m.Success) { continue }
        $name = $m.Groups[2].Value
        if (@('if','for','foreach','while','switch','return','get','set','lock','using') -contains $name) { continue }
        $end = $i
        for ($j = $i + 1; $j -lt [Math]::Min($Lines.Count, $i + 61); $j++) {
            if ($Lines[$j] -match '^\s{0,12}(public|private|protected|internal)\s') { break }
            $end = $j
        }
        $body = ($Lines[$i..$end] -join "`n")
        if ($methods.ContainsKey($name)) { $methods[$name] = $methods[$name] + "`n" + $body } else { $methods[$name] = $body }
    }

    $authorizing = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($k in @($methods.Keys)) { if (Test-DirectAuthorization -Body $methods[$k]) { [void]$authorizing.Add($k) } }

    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($k in @($methods.Keys)) {
            if ($authorizing.Contains($k)) { continue }
            foreach ($a in @($authorizing)) {
                if ($methods[$k] -match ('\b' + [regex]::Escape($a) + '\s*\(')) {
                    [void]$authorizing.Add($k); $changed = $true; break
                }
            }
        }
    }
    return $authorizing
}

function Test-AuthorizingBody {
    param([string]$Body, $Helpers)
    if (Test-DirectAuthorization -Body $Body) { return $true }
    if ($null -eq $Helpers) { return $false }
    foreach ($h in @($Helpers)) {
        if ($Body -match ('\b' + [regex]::Escape($h) + '\s*\(')) { return $true }
    }
    return $false
}

$authAttributes       = @('Authorize', 'SessionValidation', 'AllowAnonymous', 'PosLaneActivityGuard', 'DevOnly')

# ---------------------------------------------------------------------------------------------
# Collects every attribute NAME from a block of attribute text, handling all the real shapes:
#   [HttpPost]
#   [HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]
#   [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
#   [Route("api/x"), Produces("application/json")]
# Nested brackets and quoted commas make a naive split wrong, so the scan is character-wise.
# ---------------------------------------------------------------------------------------------
function Split-Attributes([string]$text) {
    $result = @()
    $depth = 0; $current = ''; $inString = $false; $verbatim = $false
    for ($i = 0; $i -lt $text.Length; $i++) {
        $ch = $text[$i]
        if ($inString) {
            $current += $ch
            if ($ch -eq '"') {
                if ($verbatim -and $i + 1 -lt $text.Length -and $text[$i + 1] -eq '"') { $current += $text[++$i]; continue }
                $inString = $false; $verbatim = $false
            }
            continue
        }
        switch ($ch) {
            '"' { $inString = $true; if ($i -gt 0 -and $text[$i - 1] -eq '@') { $verbatim = $true }; $current += $ch }
            '[' { $depth++; if ($depth -eq 1) { $current = '' } else { $current += $ch } }
            ']' {
                $depth--
                if ($depth -eq 0) { if ($current.Trim()) { $result += $current.Trim() } ; $current = '' }
                else { $current += $ch }
            }
            ',' {
                # A comma at bracket depth 1 separates attributes; deeper, it is an argument separator.
                if ($depth -eq 1 -and ($current -notmatch '\($') -and (($current.ToCharArray() | Where-Object { $_ -eq '(' }).Count -eq (($current.ToCharArray() | Where-Object { $_ -eq ')' }).Count))) {
                    if ($current.Trim()) { $result += $current.Trim() }
                    $current = ''
                } else { $current += $ch }
            }
            default { if ($depth -ge 1) { $current += $ch } }
        }
    }
    return $result
}

function Get-AttributeName([string]$attribute) {
    $name = ($attribute -split '\(')[0].Trim()
    # Strip any namespace qualifier. This codebase writes BOTH forms, in the same file:
    #     [InvPerm("doc")]
    #     [CrossBuy.Models.AccPerm("post")]
    # Not stripping it was defect #6: 85 real permission attributes (46 AccPerm, 37 CrmPerm,
    # 2 PosLaneActivityGuard) were invisible, so their actions were counted as UNGUARDED and the
    # security backlog was overstated. See CORRECTION-003.
    if ($name.Contains('.')) { $name = $name.Substring($name.LastIndexOf('.') + 1) }
    if ($name.EndsWith('Attribute')) { $name = $name.Substring(0, $name.Length - 'Attribute'.Length) }
    return $name
}

# ---------------------------------------------------------------------------------------------
# Walks UPWARD from a member line, gathering attributes. This is the corrected defect #1: it does not
# stop at a line it cannot parse, it accepts several attributes per line, and it steps over comments
# and blank lines (both of which routinely sit between an attribute and its method).
# ---------------------------------------------------------------------------------------------
# Removes a trailing line comment that sits AFTER the last ']' on the line. Without this,
#     [CrossBuy.Models.DevOnly]   // SECURITY: all seeding endpoints return 404 outside Development
# does not end with ']', the upward walk aborts, and the attribute is never seen — which is exactly how
# DevSeedController's [DevOnly] went missing from the inventory. Defect #7, same family as
# CORRECTION-001's defect 1. Only a '//' after the final ']' is stripped, so '//' inside an attribute
# argument string is left alone.
function Remove-TrailingComment([string]$line) {
    $lastClose = $line.LastIndexOf(']')
    if ($lastClose -lt 0) { return $line }
    $comment = $line.IndexOf('//', $lastClose)
    if ($comment -lt 0) { return $line }
    return $line.Substring(0, $comment).TrimEnd()
}

function Get-AttributesAbove([string[]]$lines, [int]$memberIndex) {
    $collected = New-Object System.Collections.ArrayList
    $i = $memberIndex - 1
    $pendingClose = 0
    while ($i -ge 0) {
        $line = Remove-TrailingComment $lines[$i].Trim()
        if ($line -eq '') { $i--; continue }
        if ($line.StartsWith('//') -or $line.StartsWith('///') -or $line.StartsWith('*') -or $line.StartsWith('/*')) { $i--; continue }

        # A multi-line attribute: the closing ] is on a later line than the opening [.
        $opens  = ($line.ToCharArray() | Where-Object { $_ -eq '[' }).Count
        $closes = ($line.ToCharArray() | Where-Object { $_ -eq ']' }).Count

        if ($pendingClose -gt 0) {
            # We are inside a multi-line attribute, walking up towards its '['.
            [void]$collected.Insert(0, $line)
            $pendingClose += $closes - $opens
            $i--
            continue
        }

        if ($line.EndsWith(']')) {
            [void]$collected.Insert(0, $line)
            if ($closes -gt $opens) { $pendingClose = $closes - $opens }
            $i--
            continue
        }
        break   # not an attribute line, and not mid-attribute: the block is finished
    }
    if ($collected.Count -eq 0) { return @() }
    return Split-Attributes (($collected -join ' '))
}

# ---------------------------------------------------------------------------------------------
# Controllers
# ---------------------------------------------------------------------------------------------
$controllerFiles = Get-ChildItem -Path (Join-Path $repo 'CrossBuy\Controllers') -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object FullName

$endpoints   = New-Object System.Collections.ArrayList
$controllers = New-Object System.Collections.ArrayList

# ---------------------------------------------------------------------------------------------
# PASS 1 — locate the controller class in each file, and merge class-level attributes BY CONTROLLER
# NAME across files.
#
# Two defects this pass exists to avoid, both of which understated coverage:
#
#   * Taking the FIRST class in the file. AccountingController.cs and CrmController.cs declare helper
#     types before the controller, so a first-class-only match found a non-controller, bailed out, and
#     recorded ZERO actions for 197 real endpoints. The class whose name ends in Controller is chosen
#     explicitly, preferring the one matching the file name.
#   * Ignoring `partial`. AdminController is split across four files and its [SessionValidation] sits on
#     one of them; reading each file in isolation would report the other three files' actions as having
#     no inherited guard, which is simply false.
# ---------------------------------------------------------------------------------------------
$classInfo = @{}       # controller name -> merged info
$fileClass = @{}       # file full path   -> controller name

foreach ($file in $controllerFiles) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    $text  = [string]::Join("`n", $lines)

    $candidates = @([regex]::Matches($text,
        '(?m)^[\t ]*(?:public|internal)\s+(?:sealed\s+|abstract\s+|partial\s+|static\s+)*class\s+(\w+Controller)\b\s*(?::\s*([^\r\n{]+))?'))
    if ($candidates.Count -eq 0) { continue }

    $expected = [System.IO.Path]::GetFileNameWithoutExtension($file.Name)
    $chosen = $candidates | Where-Object { $_.Groups[1].Value -eq $expected } | Select-Object -First 1
    if (-not $chosen) { $chosen = $candidates[0] }

    $controller = $chosen.Groups[1].Value
    $baseType   = $chosen.Groups[2].Value.Trim()
    $classLineIndex = ($text.Substring(0, $chosen.Index) -split "`n").Count - 1
    $classAttributes = @(Get-AttributesAbove $lines $classLineIndex)

    $fileClass[$file.FullName] = $controller
    if (-not $classInfo.ContainsKey($controller)) {
        $classInfo[$controller] = [pscustomobject]@{
            attributes = New-Object System.Collections.ArrayList
            base       = $baseType
            files      = New-Object System.Collections.ArrayList
        }
    }
    foreach ($a in $classAttributes) { if (-not $classInfo[$controller].attributes.Contains($a)) { [void]$classInfo[$controller].attributes.Add($a) } }
    if ($baseType -and -not $classInfo[$controller].base) { $classInfo[$controller].base = $baseType }
    [void]$classInfo[$controller].files.Add((Rel $file.FullName))
}

# ---------------------------------------------------------------------------------------------
# PASS 2 — actions.
# ---------------------------------------------------------------------------------------------


foreach ($file in $controllerFiles) {
    if (-not $fileClass.ContainsKey($file.FullName)) { continue }
    $lines = [System.IO.File]::ReadAllLines($file.FullName)
    $text  = [string]::Join("`n", $lines)

    # F1: the authorizing-helper set for THIS controller, resolved to a fixpoint before any action is judged.
    $authHelpers = Get-AuthorizingHelpers -Lines $lines

    $controller = $fileClass[$file.FullName]
    $info = $classInfo[$controller]
    $baseType = $info.base
    $classAttributes = @($info.attributes)
    $classAttributeNames = @($classAttributes | ForEach-Object { Get-AttributeName $_ })

    $module = Get-Module $controller
    $isApi = ($baseType -match 'ControllerBase') -or ($classAttributeNames -contains 'ApiController')
    $inherited = @($classAttributes | Where-Object { $n = Get-AttributeName $_; ($authAttributes + $permissionAttributes) -contains $n })

    $ctorMatch = [regex]::Match($text, [regex]::Escape("public $controller(") + '([^)]*)\)')
    $ctorDeps = if ($ctorMatch.Success) {
        (($ctorMatch.Groups[1].Value -split ',' | ForEach-Object { ($_.Trim() -split '\s+')[0] } | Where-Object { $_ }) -join ';')
    } else { '' }

    [void]$controllers.Add([pscustomobject]@{
        module            = $module
        controller        = $controller
        file              = Rel $file.FullName
        base              = $baseType
        is_api            = $isApi
        class_attributes  = ($classAttributes -join ';')
        ctor_dependencies = $ctorDeps
        direct_dbcontext  = ($ctorDeps -match 'CrossDbContext')
        uses_session      = ($text -match 'HttpContext\.Session|\.Session\.')
        uses_scopedtx     = ($text -match 'ScopedTx\.')
        uses_notify       = ($text -match 'NotifyAsync|NotifyRoleAsync')
        uses_businessevent = ($text -match 'RecordAsync|IBusinessEventService')
        lines             = $lines.Count
    })

    # ---- actions ----
    # Scanned LINE BY LINE rather than with one regex over the whole file, because of a defect this
    # replaces: a pattern anchored as '^\s{1,8}public' silently skipped every action written as
    #
    #     [HttpGet][InvPerm("read")] public async Task<IActionResult> WorkOrders()
    #
    # — attributes INLINE, before `public`, which is the dominant style in InventoryController. Those
    # actions were missing from the inventory altogether, so both the endpoint count and the
    # permission-coverage count were understated. Attributes are therefore gathered from TWO places and
    # merged: the ones inline on the declaration line, and the ones on the lines above it.
    for ($li = 0; $li -lt $lines.Count; $li++) {
        $raw = $lines[$li]
        $declaration = [regex]::Match($raw,
            '^(?<pre>[\t ]*(?:\[[^\r\n]*\][\t ]*)*)public\s+(?:async\s+)?(?:override\s+)?(?:virtual\s+)?[\w<>,\[\]\?\.\s]+?\s+(?<name>\w+)\s*\(')
        if (-not $declaration.Success) { continue }

        $action = $declaration.Groups['name'].Value
        if ($action -eq $controller) { continue }                      # constructor
        if ($action -in @('Dispose', 'OnActionExecuting', 'OnActionExecutionAsync')) { continue }
        # A FIELD, not a method. `public static readonly (string key, string ar, string en)[] Capabilities = new[]`
        # matched the declaration pattern because the tuple type contains parentheses, and the captured "name"
        # came out as the C# keyword `readonly`. Two such rows existed in PosController. Excluding C# keywords is
        # the narrow fix; excluding anything with `=` before `(` would also drop expression-bodied actions.
        if ($action -in @('readonly', 'const', 'static', 'new', 'return', 'if', 'while', 'foreach', 'switch')) { continue }
        if ($raw -match '^\s*public\s+(static\s+)?readonly\s') { continue }
        # Indentation guard: a member of the controller class, not something inside a nested type.
        $indent = ($declaration.Groups['pre'].Value -replace '\[[^\]]*\]', '')
        if ($indent.Length -gt 8) { continue }

        $inlineAttributes = @()
        $pre = (Remove-TrailingComment $declaration.Groups['pre'].Value).Trim()
        if ($pre.StartsWith('[')) { $inlineAttributes = Split-Attributes $pre }

        $attributes = @(Get-AttributesAbove $lines $li) + @($inlineAttributes)
        $names = @($attributes | ForEach-Object { Get-AttributeName $_ })
        $m = [pscustomobject]@{ Index = ($lines[0..$li] -join "`n").Length }

        # ---- HTTP verb. Defect #2's correction: the verb IS the mutation question. ----
        $verbs = @()
        foreach ($v in @('HttpGet', 'HttpPost', 'HttpPut', 'HttpDelete', 'HttpPatch', 'HttpHead')) {
            if ($names -contains $v) { $verbs += $v.Substring(4).ToUpperInvariant() }
        }
        if ($verbs.Count -eq 0) { $verbs = @('GET') }                   # MVC default for an un-attributed action
        $verb = ($verbs -join '|')
        $mutating = @('POST', 'PUT', 'DELETE', 'PATCH') | Where-Object { $verbs -contains $_ }

        $routeAttribute = ($attributes | Where-Object { (Get-AttributeName $_) -in @('Route', 'HttpGet', 'HttpPost', 'HttpPut', 'HttpDelete', 'HttpPatch') } |
            ForEach-Object { $r = [regex]::Match($_, '"([^"]*)"'); if ($r.Success) { $r.Groups[1].Value } } | Select-Object -First 1)

        $actionPermissions = @($attributes | Where-Object { (Get-AttributeName $_) -in $permissionAttributes })
        # A window of following lines stands in for the method body: good enough to spot ScopedTx /
        # NotifyAsync / RecordAsync usage, and it never needs a brace parser.
        $bodyEndLine = [Math]::Min($lines.Count - 1, $li + 120)
        $body = ($lines[$li..$bodyEndLine] -join "`n")

        # CORRECTION-004: IN-BODY authorization. Some actions — the POS lanes especially — call an access service
        # directly instead of carrying a permission attribute, e.g.
        #     if (!_access.CanOrder(c.Roles)) return Json(new { ok = false, ... });
        # An attribute scanner is blind to that, and calling such an action "unprotected" would be a false finding
        # as surely as crediting the lane guard was a false reassurance. So it is MEASURED and reported separately.
        #
        # The window stops at the next member declaration, capped at 40 lines, so one action cannot inherit its
        # neighbour's check — the 120-line window above is deliberately not reused here.
        $authWindowEnd = $li
        for ($bi = $li + 1; $bi -lt [Math]::Min($lines.Count, $li + 41); $bi++) {
            if ($lines[$bi] -match '^\s{0,8}(public|private|protected|internal)\s') { break }
            $authWindowEnd = $bi
        }
        $authBody = ($lines[$li..$authWindowEnd] -join "`n")
        # Stage 1 Hotfix A.1 adds a third in-body shape: a guard service that authorizes AND returns the validated
        # company (`_guard.AuthorizeAsync(...)` in AccountingApiController). It is recognised here for the same
        # reason the access-service calls are — calling it "unprotected" would be a false finding — and for the same
        # reason it is recognised NARROWLY: the pattern requires a guard/authorization-shaped call, not any method
        # whose name happens to contain "Auth".
        # Stage 1 F1 - IN-BODY AUTHORIZATION BY CALL GRAPH, not by a fixed pattern list.
        #
        # The old test was three hardcoded regexes. It went stale the moment Batch D1 Wave 1 moved the authorization
        # call OUT of each action and into a shared private gate (GateAsync / HrGateAsync / PosGateAsync /
        # TaskGateAsync). That refactor is the whole point - it stops an authorization predicate being copied into 33
        # action bodies - and it made the scanner report 38 genuinely protected endpoints as unprotected. A detector
        # that punishes the correct structure is a measurement defect, not a code defect.
        #
        # Authorization is now resolved TRANSITIVELY: an action counts if it asks an authority itself, OR calls a
        # method in this controller that (transitively) does. $authHelpers is computed to a FIXPOINT per file above.
        #
        # HONEST LIMITATION, declared rather than implied: this is a SYNTACTIC call graph over ONE file, not Roslyn
        # semantic binding. It cannot follow an authorization call through an interface, a base class or another file,
        # and it cannot distinguish two same-named methods. That is why the finalization report recommends a Roslyn
        # analyzer as the Stage 2 starting point rather than claiming semantic analysis here.
        $inBodyAuth = [bool](Test-AuthorizingBody -Body $authBody -Helpers $authHelpers)

        [void]$endpoints.Add([pscustomobject]@{
            module               = $module
            controller           = $controller
            action               = $action
            http_method          = $verb
            route_attribute      = if ($routeAttribute) { $routeAttribute } else { '' }
            is_api               = $isApi
            mutating             = [bool]$mutating
            antiforgery          = ($names -contains 'ValidateAntiForgeryToken')
            permission_attributes = if ($actionPermissions.Count) { ($actionPermissions -join ';') } else { '(none)' }
            inherited_permission = if ($inherited.Count) { ($inherited -join ';') } else { '(none)' }
            # CORRECTION-004: authorization the action performs ITSELF, which no attribute records.
            in_body_authorization = $inBodyAuth
            action_attributes    = ($attributes -join ';')
            uses_scopedtx        = ($body -match 'ScopedTx\.')
            uses_notify          = ($body -match 'NotifyAsync|NotifyRoleAsync')
            uses_businessevent   = ($body -match 'RecordAsync')
            file                 = Rel $file.FullName
        })
    }
}

# ---------------------------------------------------------------------------------------------
# Workers
# ---------------------------------------------------------------------------------------------
$programText = [System.IO.File]::ReadAllText((Join-Path $repo 'CrossBuy\Program.cs'))
$workerFiles = Get-ChildItem -Path (Join-Path $repo 'CrossBuy') -Recurse -Filter *.cs -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Where-Object { (Select-String -Path $_.FullName -Pattern ':\s*BackgroundService' -Quiet) } | Sort-Object FullName

$workers = New-Object System.Collections.ArrayList
foreach ($file in $workerFiles) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    $name = [regex]::Match($text, 'class\s+(\w+)\s*:\s*BackgroundService').Groups[1].Value
    if (-not $name) { continue }
    [void]$workers.Add([pscustomobject]@{
        worker              = $name
        module              = if ($file.FullName -match '\\Platform\\') { 'Platform' } elseif ($file.FullName -match '\\Comm\\') { 'Communication' } else { 'Core' }
        file                = Rel $file.FullName
        registered          = ($programText -match ("AddHostedService<[\w\.]*" + [regex]::Escape($name) + ">"))
        uses_periodictimer  = ($text -match 'PeriodicTimer')
        creates_scope       = ($text -match 'CreateScope\(')
        # Stage 0 Batch B: the single-worker gate. A worker without it can run in two processes at once.
        uses_worker_gate    = ($text -match 'IWorkerGate|_gate\.WaitUntilAllowedAsync')
        # Stage 0 Batch A: the four workers that used to carry `private const int CompanyId = 1`.
        multi_company       = ($text -match 'IWorkerCompanyScope|ForEachCompanyAsync')
        hardcoded_company   = ($text -match 'const\s+int\s+CompanyId\s*=')
        logs_errors         = ($text -match 'LogError')
        graceful_shutdown   = ($text -match 'stoppingToken')
    })
}

# ---------------------------------------------------------------------------------------------
# SQL — projected from the manifest so there is exactly ONE derivation of the SQL verdict.
# ---------------------------------------------------------------------------------------------
$sqlRows = New-Object System.Collections.ArrayList
if (Test-Path $manifest) {
    $m = Get-Content $manifest -Raw | ConvertFrom-Json
    foreach ($s in $m.scripts) {
        [void]$sqlRows.Add([pscustomobject]@{
            script            = $s.path
            rank              = $s.rank
            idempotency       = $s.idempotency
            safe_to_reapply   = ($s.idempotency -in @('guarded', 'guarded-by-predicate', 'no-mutation'))
            guards_found      = ($s.guards -join ';')
            mutations         = ($s.mutations -join ';')
            unguarded_batches = ($s.unguardedBatches -join ';')
            review_verdict    = $s.reviewVerdict
            review_evidence   = $s.reviewEvidence
            encoding          = $s.encoding
            bytes             = $s.bytes
            sha256            = $s.sha256
            excluded          = $s.excludedFromDeploy
            excluded_reason   = $s.excludedReason
        })
    }
} else {
    Write-Host "manifest.json missing — run scan-sql-manifest.ps1 first; SQL-Script-Inventory.csv not regenerated." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------------------------
# Write. UTF-8 WITH BOM, because these files carry Arabic screen names and Excel misreads BOM-less
# UTF-8 as ANSI — the original evidence files are BOM'd for the same reason.
# ---------------------------------------------------------------------------------------------
function Save-Csv($rows, [string]$name) {
    $path = Join-Path $evidence $name
    if (@($rows).Count -eq 0) { Write-Host "  (skipped, no rows) $name" -ForegroundColor Yellow; return }
    $csv = ($rows | ConvertTo-Csv -NoTypeInformation) -join "`r`n"
    [System.IO.File]::WriteAllText($path, $csv + "`r`n", (New-Object System.Text.UTF8Encoding($true)))
    Write-Host ("  {0,-38} {1,5} rows" -f $name, @($rows).Count)
}

Write-Host "architecture evidence -> $evidence"
Save-Csv $endpoints   'Endpoint-Inventory.csv'
Save-Csv ($endpoints | Select-Object module, controller, action, http_method, mutating, antiforgery, is_api,
    permission_attributes, inherited_permission, in_body_authorization, file) 'Permission-Coverage.csv'
Save-Csv $controllers 'Controller-Inventory.csv'
Save-Csv $workers     'Worker-Inventory.csv'
Save-Csv $sqlRows     'SQL-Script-Inventory.csv'

# ---------------------------------------------------------------------------------------------
# Headline numbers, printed so a reader can compare them against the documents without opening a CSV.
# ---------------------------------------------------------------------------------------------
$mutatingActions = @($endpoints | Where-Object { $_.mutating })
$unguarded = @($mutatingActions | Where-Object { $_.permission_attributes -eq '(none)' })
$noAntiforgery = @($mutatingActions | Where-Object { -not $_.antiforgery -and -not $_.is_api })

# BOTH coverage bases are printed, because they answer different questions and picking one silently is how a
# security number gets flattered. Action-level is the right measure where only SOME actions on a controller carry a
# permission; any-level is the right measure for a controller gated as a whole (e.g. [PlatformOps] on the class).
$effective = @($mutatingActions | Where-Object {
    $a = $_.permission_attributes; $i = $_.inherited_permission
    @($permissionAttributes | Where-Object { $a -like "*$_*" -or $i -like "*$_*" }).Count -gt 0 })

Write-Host ""
Write-Host "SUMMARY"
Write-Host ("  controller files / distinct classes : {0} / {1}" -f $controllers.Count, $classInfo.Count)
Write-Host ("  actions                            : {0}" -f $endpoints.Count)
Write-Host ("  mutating actions (POST/PUT/DELETE) : {0}" -f $mutatingActions.Count)
Write-Host ("  mutating WITHOUT an action-level permission attr : {0}" -f $unguarded.Count)
Write-Host ("  mutating WITH a permission ATTRIBUTE at any level: {0}" -f $effective.Count)

# CORRECTION-004: three categories, not two. The attribute-only split was reported as if it were the security
# position; it is not, in BOTH directions — the lane guard credited protection that did not exist, and an in-body
# access-service call provides protection no attribute records.
$attributeGap    = @($mutatingActions | Where-Object { $effective -notcontains $_ })
$inBodyOnly      = @($attributeGap | Where-Object { $_.in_body_authorization })
$noVisibleAuth   = @($attributeGap | Where-Object { -not $_.in_body_authorization })

Write-Host ("  mutating WITHOUT a permission attribute         : {0}" -f $attributeGap.Count)
Write-Host ("    ...of which authorized IN-BODY (access svc)   : {0}" -f $inBodyOnly.Count)
Write-Host ("    ...with NO authorization this scan can see    : {0}   <-- the backlog" -f $noVisibleAuth.Count)
Write-Host ("  mutating with antiforgery          : {0}" -f @($mutatingActions | Where-Object { $_.antiforgery }).Count)
Write-Host ("  mutating MVC without antiforgery   : {0}" -f $noAntiforgery.Count)
Write-Host ("  workers                            : {0}" -f $workers.Count)
Write-Host ("  workers behind the single-worker gate: {0}/{1}" -f @($workers | Where-Object { $_.uses_worker_gate }).Count, $workers.Count)
Write-Host ("  workers still hardcoding company 1 : {0}" -f @($workers | Where-Object { $_.hardcoded_company }).Count)
Write-Host ("  sql scripts                        : {0}" -f $sqlRows.Count)
Write-Host ("  sql NOT safe to re-apply           : {0}" -f @($sqlRows | Where-Object { -not $_.safe_to_reapply }).Count)

# The finding CORRECTION-001 was raised about. Re-verified on every run so the corrected fact is proven
# from source rather than remembered.
#
# The nine actions are NAMED rather than matched on 'WorkOrder': ProducePartial mutates a work order but
# does not carry the words in its name, and a name filter silently reported eight — the same class of
# understatement as the inline-attribute defect above.
$workOrderActions = @('CreateWorkOrder', 'SaveWorkOrder', 'ReleaseWorkOrder', 'CancelWorkOrder',
                      'CompleteWorkOrder', 'ProducePartial', 'AddWorkOrderLabor', 'RemoveWorkOrderLabor',
                      'GeneratePlanWorkOrders')
$workOrderWrites = @($endpoints | Where-Object {
    $_.controller -eq 'InventoryController' -and $_.action -in $workOrderActions -and $_.mutating })
Write-Host ""
Write-Host ("CORRECTION-001 re-check — Manufacturing work-order write actions found: {0} of {1} expected" -f `
    $workOrderWrites.Count, $workOrderActions.Count)
foreach ($w in $workOrderWrites) {
    Write-Host ("    {0,-24} {1,-6} perm={2,-20} antiforgery={3}" -f $w.action, $w.http_method, $w.permission_attributes, $w.antiforgery)
}
$missingWorkOrder = @($workOrderActions | Where-Object { $_ -notin @($workOrderWrites.action) })
if ($missingWorkOrder.Count) {
    Write-Host ("    NOT FOUND as a mutating action: {0}" -f ($missingWorkOrder -join ', ')) -ForegroundColor Yellow
}
$unguardedWorkOrder = @($workOrderWrites | Where-Object { $_.permission_attributes -eq '(none)' -or -not $_.antiforgery })
if ($unguardedWorkOrder.Count) {
    Write-Host ("    CORRECTION-001 IS NO LONGER TRUE: {0} of these lost a guard." -f $unguardedWorkOrder.Count) -ForegroundColor Red
}