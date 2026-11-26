[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$script:CanonicalRoot = 'C:\CrossBuy\wt-pb-cycle-01'
$script:ExpectedBranch = 'guide/capture-base'
$script:Baseline = '480c3d4'

function Test-CanonicalSource {
    param([string]$Root, [string]$Branch, [bool]$BaselineIsAncestor)
    return (([IO.Path]::GetFullPath($Root).TrimEnd('\') -ieq $script:CanonicalRoot) -and
        ($Branch -ceq $script:ExpectedBranch) -and $BaselineIsAncestor)
}

function Test-FeatureDatabasePair {
    param([string]$Database, [string]$FeatureFlag)
    return ($Database -ceq 'CrossBuyDB2') -and ($FeatureFlag -ceq 'true')
}

function Test-PortAvailable {
    param([int]$ListenerCount)
    return $ListenerCount -eq 0
}

function Assert-CanonicalWorktree {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
    $branch = (& git -C $root branch --show-current 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the current Git branch.' }
    $null = & git -C $root merge-base --is-ancestor $script:Baseline HEAD 2>$null
    $baselineIsAncestor = $LASTEXITCODE -eq 0
    if (-not (Test-CanonicalSource -Root $root -Branch $branch -BaselineIsAncestor $baselineIsAncestor)) {
        throw "Refusing to launch: expected $script:CanonicalRoot on $script:ExpectedBranch, containing baseline $script:Baseline."
    }
    return [pscustomobject]@{ Root = $root; Branch = $branch; Head = (& git -C $root rev-parse HEAD).Trim() }
}

function Get-ProfileConfiguration {
    param([string]$ProjectRoot)
    $settingsPath = Join-Path $ProjectRoot 'Properties\launchSettings.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) { throw 'Official HTTPS launch settings were not found.' }
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $profile = $settings.profiles.https
    if (-not $profile) { throw 'Official HTTPS launch profile is missing.' }
    $url = [string]$profile.applicationUrl
    $environment = $profile.environmentVariables
    $connection = [string]$environment.ConnectionStrings__DefaultConnection
    $featureFlag = [string]$environment.CommunicationPlatform__TaskLinksProvisioned
    if ($url -notmatch 'https://localhost:7183(?:;|$)') { throw 'HTTPS launch profile must bind https://localhost:7183.' }
    if ($connection -notmatch '(?i)(?:^|;)Database=CrossBuyDB2(?:;|$)') { throw 'HTTPS launch profile must explicitly target CrossBuyDB2.' }
    if (-not (Test-FeatureDatabasePair -Database 'CrossBuyDB2' -FeatureFlag $featureFlag)) {
        throw 'Task linking requires CommunicationPlatform__TaskLinksProvisioned=true with CrossBuyDB2.'
    }
    return [pscustomobject]@{ Url = 'https://localhost:7183'; Database = 'CrossBuyDB2'; FeatureFlag = $featureFlag; ProfilePath = $settingsPath }
}

function Get-PortListeners {
    try {
        $connections = @(Get-NetTCPConnection -State Listen -LocalPort 7183 -ErrorAction Stop |
            Select-Object -ExpandProperty OwningProcess -Unique)
    }
    catch {
        $netstat = Get-Command netstat.exe -ErrorAction SilentlyContinue
        if (-not $netstat) { throw 'Cannot verify port 7183 ownership; refusing to launch.' }
        $lines = @(& $netstat.Source -ano -p tcp 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot verify port 7183 ownership; refusing to launch.' }
        $connections = @($lines | ForEach-Object {
            if ($_ -match '^\s*TCP\s+\S+:7183\s+\S+\s+LISTENING\s+(\d+)\s*$') { [int]$Matches[1] }
        } | Select-Object -Unique)
    }
    foreach ($processId in $connections) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$processId" -ErrorAction SilentlyContinue
        [pscustomobject]@{
            PID = [int]$processId
            Image = if ($process) { $process.Name } else { 'NOT READABLE' }
            CommandLine = if ($process) { $process.CommandLine } else { 'NOT READABLE' }
        }
    }
}

function Test-CommTaskLinkSchema {
    $connection = [System.Data.SqlClient.SqlConnection]::new(
        'Server=.;Database=CrossBuyDB2;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=5')
    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandTimeout = 5
        $command.CommandText = @'
SELECT
    (SELECT COUNT(*) FROM sys.tables WHERE name = 'CommTaskLinks' AND schema_id = SCHEMA_ID('dbo')) +
    (SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_CommTaskLinks_LiveConversation' AND object_id = OBJECT_ID('dbo.CommTaskLinks') AND is_unique = 1 AND has_filter = 1) +
    (SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_CommTaskLinks_LiveMessage' AND object_id = OBJECT_ID('dbo.CommTaskLinks') AND is_unique = 1 AND has_filter = 1) +
    (SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_CommTaskLinks_Thread' AND object_id = OBJECT_ID('dbo.CommTaskLinks')) +
    (SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_CommTaskLinks_Task' AND object_id = OBJECT_ID('dbo.CommTaskLinks')) +
    (SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_CommTaskLinks_Origin' AND parent_object_id = OBJECT_ID('dbo.CommTaskLinks')) +
    (SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_CommTaskLinks_Thread' AND parent_object_id = OBJECT_ID('dbo.CommTaskLinks'));
'@
        $count = [int]$command.ExecuteScalar()
        if ($count -ne 7) { throw "CommTaskLinks schema is incomplete: found $count of 7 required objects." }
        return $true
    }
    finally {
        if ($connection.State -ne [System.Data.ConnectionState]::Closed) { $connection.Close() }
        $connection.Dispose()
    }
}

function Invoke-PolicySelfTest {
    $checks = @(
        (Test-CanonicalSource -Root $script:CanonicalRoot -Branch $script:ExpectedBranch -BaselineIsAncestor $true),
        (-not (Test-CanonicalSource -Root 'C:\CrossBuy\CrossBuy' -Branch $script:ExpectedBranch -BaselineIsAncestor $true)),
        (-not (Test-CanonicalSource -Root $script:CanonicalRoot -Branch 'foundation-v2' -BaselineIsAncestor $true)),
        (-not (Test-CanonicalSource -Root $script:CanonicalRoot -Branch $script:ExpectedBranch -BaselineIsAncestor $false)),
        (Test-FeatureDatabasePair -Database 'CrossBuyDB2' -FeatureFlag 'true'),
        (-not (Test-FeatureDatabasePair -Database 'CrossBuyDev' -FeatureFlag 'true')),
        (-not (Test-FeatureDatabasePair -Database 'CrossBuyDB2' -FeatureFlag '')),
        (Test-PortAvailable -ListenerCount 0),
        (-not (Test-PortAvailable -ListenerCount 1))
    )
    if ($checks -contains $false) { throw 'Canonical launcher policy self-test failed.' }
    Write-Output 'PASS: 9 canonical source, database pairing, and port safety checks.'
}

if ($SelfTest) {
    Invoke-PolicySelfTest
    exit 0
}

$identity = Assert-CanonicalWorktree
$projectRoot = Join-Path $identity.Root 'CrossBuy'
$projectFile = Join-Path $projectRoot 'CrossBuy.csproj'
$profile = Get-ProfileConfiguration -ProjectRoot $projectRoot
$schemaReady = Test-CommTaskLinkSchema
$listeners = @(Get-PortListeners)

Write-Output ("SOURCE={0}; BRANCH={1}; HEAD={2}" -f $identity.Root, $identity.Branch, $identity.Head)
Write-Output ("URL={0}; DATABASE={1}; TASK_LINKS={2}; SCHEMA=verified" -f $profile.Url, $profile.Database, $profile.FeatureFlag)
if ($listeners.Count -gt 0) {
    foreach ($listener in $listeners) {
        Write-Output ("PORT_7183_OWNER PID={0}; IMAGE={1}; COMMAND={2}" -f $listener.PID, $listener.Image, $listener.CommandLine)
    }
}
if ($CheckOnly) {
    Write-Output 'PASS: canonical source, profile, database schema, and current listener inventory verified; no process started.'
    exit 0
}
if (-not (Test-PortAvailable -ListenerCount $listeners.Count)) {
    throw 'Refusing to start because port 7183 is occupied. Identify and coordinate with its owner; this launcher never stops a process.'
}

$evidence = [ordered]@{
    capturedAtUtc = [DateTime]::UtcNow.ToString('o')
    source = $identity.Root
    branch = $identity.Branch
    head = $identity.Head
    url = $profile.Url
    database = $profile.Database
    taskLinksProvisioned = $profile.FeatureFlag
    commTaskLinksSchemaVerified = $schemaReady
    listenerBeforeLaunch = $listeners.Count
    launchProfile = 'https'
}
$evidencePath = Join-Path $env:TEMP ('crossbuy-7183-launch-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.json')
$evidence | ConvertTo-Json | Set-Content -LiteralPath $evidencePath -Encoding UTF8
Write-Output "NON_SENSITIVE_EVIDENCE=$evidencePath"

$temporaryEnvironment = @{
    Kestrel__Certificates__Default__Subject = $env:Kestrel__Certificates__Default__Subject
    Kestrel__Certificates__Default__Store = $env:Kestrel__Certificates__Default__Store
    Kestrel__Certificates__Default__Location = $env:Kestrel__Certificates__Default__Location
    Kestrel__Certificates__Default__AllowInvalid = $env:Kestrel__Certificates__Default__AllowInvalid
}
try {
    $env:Kestrel__Certificates__Default__Subject = 'localhost'
    $env:Kestrel__Certificates__Default__Store = 'My'
    $env:Kestrel__Certificates__Default__Location = 'LocalMachine'
    $env:Kestrel__Certificates__Default__AllowInvalid = 'false'
    & dotnet run --project $projectFile --launch-profile https
    if ($LASTEXITCODE -ne 0) { throw "Canonical application exited with code $LASTEXITCODE." }
}
finally {
    foreach ($name in $temporaryEnvironment.Keys) {
        if ($null -eq $temporaryEnvironment[$name]) { Remove-Item "Env:$name" -ErrorAction SilentlyContinue }
        else { Set-Item "Env:$name" $temporaryEnvironment[$name] }
    }
}
