param(
    [string]$ManifestPath,
    [string]$ChecklistPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'docs\acceptance-checklist.md'),
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$SteamAppsRoot = 'C:\Program Files (x86)\Steam\steamapps',
    [ValidateRange(1, 720)][int]$MaxAgeHours = 72,
    [switch]$LibraryMode
)

# Strict, read-only release gate for REAL-GAME full-Mod E2E evidence.
# This script never backs up, restores, deletes, or modifies user save data.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$script:EvidenceKind = 'jianghu-youling-real-game-full-mod-e2e-evidence'
$script:SecretReportKind = 'jianghu-youling-secret-scan-report'
$script:BackupManifestKind = 'jianghu-youling-save-backup-manifest'
$script:RestoreReportKind = 'jianghu-youling-save-restore-verification'
$script:ShaPattern = '^[A-Fa-f0-9]{64}$'
$script:SecretPattern = '(?i)(sk-[A-Za-z0-9_-]{16,}|Bearer\s+[A-Za-z0-9._~+/-]{20,}|api[_-]?key["'']?\s*[:=]\s*["''][^"'']{12,}["''])'

function Resolve-DecompiledBase([string]$RepositoryRootPath) {
    if (-not [string]::IsNullOrWhiteSpace($env:JHYL_DECOMPILED_ROOT)) {
        return [IO.Path]::GetFullPath($env:JHYL_DECOMPILED_ROOT).TrimEnd('\', '/')
    }
    $legacy = [IO.Path]::GetFullPath((Join-Path $RepositoryRootPath '.decompiled')).TrimEnd('\', '/')
    if (Test-Path -LiteralPath $legacy) { return $legacy }
    return $legacy
}

. (Join-Path $PSScriptRoot 'e2e\RealGameE2eSafety.ps1')

function Get-RequiredProperty([object]$Object, [string]$Name, [string]$Where) {
    if ($null -eq $Object) { throw "$Where is null" }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "$Where is missing property '$Name'" }
    return $property.Value
}

function Get-RequiredString([object]$Object, [string]$Name, [string]$Where) {
    $value = Get-RequiredProperty $Object $Name $Where
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        throw "$Where.$Name must be a non-empty string"
    }
    return ([string]$value).Trim()
}

function Get-RequiredArray([object]$Object, [string]$Name, [string]$Where) {
    $value = Get-RequiredProperty $Object $Name $Where
    if ($null -eq $value) { return @() }
    return @($value)
}

function Assert-ExactValue([object]$Actual, [object]$Expected, [string]$Where) {
    if ([string]$Actual -cne [string]$Expected) {
        throw "$Where must be '$Expected' (actual '$Actual')"
    }
}

function Assert-Sha256Text([string]$Value, [string]$Where) {
    if ($Value -cnotmatch $script:ShaPattern) { throw "$Where must be a 64-hex SHA-256" }
}

function ConvertTo-UtcTimestamp([string]$Value, [string]$Where) {
    $parsed = [DateTimeOffset]::MinValue
    $style = [Globalization.DateTimeStyles]::AllowWhiteSpaces
    if (-not [DateTimeOffset]::TryParse($Value, [Globalization.CultureInfo]::InvariantCulture, $style, [ref]$parsed)) {
        throw "$Where must be an ISO-8601 timestamp"
    }
    if ($parsed.Offset -ne [TimeSpan]::Zero) { throw "$Where must explicitly use UTC (Z or +00:00)" }
    return $parsed.ToUniversalTime()
}

function Assert-TimestampInRun([DateTimeOffset]$Value, [DateTimeOffset]$Started,
    [DateTimeOffset]$Finished, [string]$Where) {
    if ($Value -lt $Started.AddMinutes(-10) -or $Value -gt $Finished.AddMinutes(10)) {
        throw "$Where falls outside the recorded run window"
    }
}

function Get-FullEvidencePath([string]$ReportedPath, [string]$ManifestDirectory, [string]$Where) {
    if ([string]::IsNullOrWhiteSpace($ReportedPath)) { throw "$Where is empty" }
    $candidate = if ([IO.Path]::IsPathRooted($ReportedPath)) {
        [IO.Path]::GetFullPath($ReportedPath)
    } else {
        [IO.Path]::GetFullPath((Join-Path $ManifestDirectory $ReportedPath))
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "$Where does not exist as a file: $candidate"
    }
    return $candidate
}

function Assert-ArtifactHash([string]$ReportedPath, [string]$ExpectedSha,
    [string]$ManifestDirectory, [string]$Where) {
    Assert-Sha256Text $ExpectedSha "$Where.sha256"
    $path = Get-FullEvidencePath $ReportedPath $ManifestDirectory "$Where.path"
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($actual -cne $ExpectedSha.ToUpperInvariant()) {
        throw "$Where SHA-256 mismatch for $path"
    }
    return $path
}

function Test-SafeRelativePath([string]$Path, [string]$Where) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path)) {
        throw "$Where must be a non-empty relative path"
    }
    $segments = $Path.Replace('\', '/').Split('/')
    if ($segments -contains '..' -or $segments -contains '.') { throw "$Where contains traversal segments" }
}

function Test-IsPathWithin([string]$Candidate, [string]$Parent) {
    $candidateFull = [IO.Path]::GetFullPath($Candidate).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $candidateFull.StartsWith($parentFull, [StringComparison]::OrdinalIgnoreCase)
}

function Read-E2eChecklistIds([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Acceptance checklist missing: $Path" }
    $ids = New-Object 'System.Collections.Generic.List[string]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadAllLines([IO.Path]::GetFullPath($Path), [Text.Encoding]::UTF8)) {
        $lineNumber++
        $match = [regex]::Match($line, '^\s*-\s*\[[ xX]\]\s+(E2E-(?:T\d{2}|\d{3}))(?![A-Za-z0-9-])')
        if (-not $match.Success) { continue }
        $id = $match.Groups[1].Value
        if (-not $seen.Add($id)) { throw "Duplicate checklist ID '$id' at line $lineNumber" }
        $ids.Add($id)
    }
    if ($ids.Count -eq 0) { throw 'Acceptance checklist contains no E2E item IDs' }
    return $ids.ToArray()
}

function Read-JsonFile([string]$Path, [string]$Where) {
    try {
        $json = [IO.File]::ReadAllText($Path, $script:E2eStrictUtf8)
        if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) {
            return (ConvertFrom-Json -InputObject $json -DateKind String)
        }
        return (ConvertFrom-Json -InputObject $json)
    }
    catch { throw "$Where is not valid strict evidence JSON" }
}

function Assert-SecretScopeEqual([object]$Reported, [object]$Actual, [string]$Where) {
    foreach ($name in @('name', 'status', 'sourceKind', 'fileCount', 'totalBytes', 'snapshotSha256', 'findings')) {
        Assert-ExactValue (Get-RequiredProperty $Reported $name $Where) (Get-RequiredProperty $Actual $name "actual $Where") "$Where.$name"
    }
    $reportedRoots = @(Get-RequiredArray $Reported 'roots' $Where | ForEach-Object { Get-E2eFullPath ([string]$_) "$Where root" })
    $actualRoots = @(Get-RequiredArray $Actual 'roots' "actual $Where" | ForEach-Object { Get-E2eFullPath ([string]$_) "actual $Where root" })
    if ($reportedRoots.Count -ne $actualRoots.Count) { throw "$Where root count mismatch" }
    for ($index = 0; $index -lt $reportedRoots.Count; $index++) {
        if (-not $reportedRoots[$index].Equals($actualRoots[$index], [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Where root mismatch"
        }
    }
    if ((Get-RequiredString $Actual 'sourceKind' "actual $Where") -eq 'file-set') {
        $reportedFiles = @(Get-RequiredArray $Reported 'files' $Where)
        $actualFiles = @(Get-RequiredArray $Actual 'files' "actual $Where")
        if ($reportedFiles.Count -ne $actualFiles.Count) { throw "$Where file-row count mismatch" }
        for ($index = 0; $index -lt $reportedFiles.Count; $index++) {
            $reportedFile = $reportedFiles[$index]; $actualFile = $actualFiles[$index]
            foreach ($name in @('identity', 'length', 'sha256')) {
                Assert-ExactValue (Get-RequiredProperty $reportedFile $name "$Where file") (Get-RequiredProperty $actualFile $name "actual $Where file") "$Where file.$name"
            }
            $reportedPath = Get-E2eFullPath (Get-RequiredString $reportedFile 'path' "$Where file") "$Where file path"
            $actualPath = Get-E2eFullPath (Get-RequiredString $actualFile 'path' "actual $Where file") "actual $Where file path"
            if (-not $reportedPath.Equals($actualPath, [StringComparison]::OrdinalIgnoreCase)) { throw "$Where file path mismatch" }
        }
    }
    else {
        $reportedBlobs = @(Get-RequiredArray $Reported 'blobs' $Where)
        $actualBlobs = @(Get-RequiredArray $Actual 'blobs' "actual $Where")
        if ($reportedBlobs.Count -ne $actualBlobs.Count) { throw "$Where Git blob count mismatch" }
        for ($index = 0; $index -lt $reportedBlobs.Count; $index++) {
            Assert-ExactValue (Get-RequiredProperty $reportedBlobs[$index] 'oid' "$Where blob") (Get-RequiredProperty $actualBlobs[$index] 'oid' "actual $Where blob") "$Where blob.oid"
            Assert-ExactValue (Get-RequiredProperty $reportedBlobs[$index] 'length' "$Where blob") (Get-RequiredProperty $actualBlobs[$index] 'length' "actual $Where blob") "$Where blob.length"
        }
        Assert-ExactValue (Get-RequiredProperty $Reported 'gitObjectCount' $Where) (Get-RequiredProperty $Actual 'gitObjectCount' "actual $Where") "$Where.gitObjectCount"
    }
}

function Assert-SecretScopeShape([object]$Scope, [string]$ExpectedName) {
    Assert-ExactValue (Get-RequiredString $Scope 'name' "secret scope $ExpectedName") $ExpectedName "secret scope $ExpectedName.name"
    Assert-ExactValue (Get-RequiredString $Scope 'status' "secret scope $ExpectedName") 'PASS' "secret scope $ExpectedName.status"
    Assert-ExactValue (Get-RequiredProperty $Scope 'findings' "secret scope $ExpectedName") 0 "secret scope $ExpectedName.findings"
    $sourceKind = Get-RequiredString $Scope 'sourceKind' "secret scope $ExpectedName"
    if ($sourceKind -notin @('file-set', 'git-object-database')) { throw "secret scope $ExpectedName has an unsupported source kind" }
    Assert-Sha256Text (Get-RequiredString $Scope 'snapshotSha256' "secret scope $ExpectedName") "secret scope $ExpectedName.snapshotSha256"
    if ([long](Get-RequiredProperty $Scope 'fileCount' "secret scope $ExpectedName") -lt 0 -or
        [long](Get-RequiredProperty $Scope 'totalBytes' "secret scope $ExpectedName") -lt 0) {
        throw "secret scope $ExpectedName has invalid aggregate metadata"
    }
    [void](Get-RequiredArray $Scope 'roots' "secret scope $ExpectedName")
    if ($sourceKind -eq 'file-set') { [void](Get-RequiredArray $Scope 'files' "secret scope $ExpectedName") }
    else { [void](Get-RequiredArray $Scope 'blobs' "secret scope $ExpectedName") }
}

function Get-RequiredWithOptionalSecretFileSet([string]$RequiredIdentity, [string]$RequiredPath,
    [string]$RequiredWhere, [string]$OptionalIdentity, [string]$OptionalPath,
    [string]$OptionalWhere) {
    $requiredFull = Get-E2eFullPath $RequiredPath $RequiredWhere
    if (-not (Test-Path -LiteralPath $requiredFull -PathType Leaf)) { throw "$RequiredWhere is missing" }
    $optionalFull = Get-E2eFullPath $OptionalPath $OptionalWhere
    if ($requiredFull.Equals($optionalFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$OptionalWhere aliases the required scan input"
    }
    $rows = New-Object 'System.Collections.Generic.List[object]'
    $roots = New-Object 'System.Collections.Generic.List[string]'
    $rows.Add([pscustomobject][ordered]@{ identity = $RequiredIdentity; path = $requiredFull })
    $roots.Add($requiredFull)
    if (Test-Path -LiteralPath $optionalFull) {
        if (-not (Test-Path -LiteralPath $optionalFull -PathType Leaf)) {
            throw "$OptionalWhere exists but is not a file"
        }
        $rows.Add([pscustomobject][ordered]@{ identity = $OptionalIdentity; path = $optionalFull })
        $roots.Add($optionalFull)
    }
    return [pscustomobject][ordered]@{
        rows = $rows.ToArray()
        roots = $roots.ToArray()
    }
}

function Test-SecretScanReport([object]$Metadata, [string]$ManifestDirectory,
    [DateTimeOffset]$Started, [DateTimeOffset]$Finished, [string]$GitSha,
    [object]$AuthorityContext, [string]$ExpectedPlayerLogPath, [string]$RunSessionRoot,
    [switch]$SkipActualScopeRescan) {
    Assert-ExactValue (Get-RequiredString $Metadata 'status' 'secretScan') 'PASS' 'secretScan.status'
    $pathText = Get-RequiredString $Metadata 'reportPath' 'secretScan'
    $shaText = Get-RequiredString $Metadata 'reportSha256' 'secretScan'
    $path = Assert-ArtifactHash $pathText $shaText $ManifestDirectory 'secretScan.report'
    $report = Read-JsonFile $path 'secret scan report'
    Assert-ExactValue (Get-RequiredProperty $report 'schemaVersion' 'secret scan report') 2 'secret scan report.schemaVersion'
    Assert-ExactValue (Get-RequiredString $report 'kind' 'secret scan report') $script:SecretReportKind 'secret scan report.kind'
    Assert-ExactValue (Get-RequiredString $report 'status' 'secret scan report') 'PASS' 'secret scan report.status'
    Assert-ExactValue (Get-RequiredProperty $report 'findings' 'secret scan report') 0 'secret scan report.findings'
    Assert-ExactValue (Get-RequiredString $report 'gitSha' 'secret scan report') $GitSha 'secret scan report.gitSha'
    $scanned = ConvertTo-UtcTimestamp (Get-RequiredString $report 'scannedUtc' 'secret scan report') 'secret scan report.scannedUtc'
    Assert-TimestampInRun $scanned $Started $Finished 'secret scan report.scannedUtc'
    Assert-ExactValue (Get-RequiredString $report 'scanPolicy' 'secret scan report') 'generic-pattern-and-exact-dpapi-configured-values-v3' 'secret scan report.scanPolicy'
    $requiredNames = @('repository', 'git-history', 'command-output', 'player-log', 'llm-metrics', 'chat-exports')
    $reportedScopes = @(Get-RequiredArray $report 'scopes' 'secret scan report')
    if ($reportedScopes.Count -ne $requiredNames.Count) { throw 'secret scan report must contain exactly six scope records' }
    for ($index = 0; $index -lt $requiredNames.Count; $index++) { Assert-SecretScopeShape $reportedScopes[$index] $requiredNames[$index] }
    [void](Get-RequiredString $report 'settingsRoot' 'secret scan report')
    if ([int](Get-RequiredProperty $report 'exactSecretCount' 'secret scan report') -lt 0 -or
        [int](Get-RequiredProperty $report 'protectedConfigReplicaCount' 'secret scan report') -lt 0) {
        throw 'secret scan report has invalid configured-credential counts'
    }
    $credentialSources = @(Get-RequiredArray $report 'credentialSources' 'secret scan report')
    $expectedConfigFiles = @('llm.json', 'tts.json', 'minimax.json')
    if ($credentialSources.Count -ne $expectedConfigFiles.Count) { throw 'secret scan report must describe all three protected config kinds' }
    for ($index = 0; $index -lt $expectedConfigFiles.Count; $index++) {
        Assert-ExactValue (Get-RequiredString $credentialSources[$index] 'configFile' 'credential source') $expectedConfigFiles[$index] 'credential source.configFile'
        if ([int](Get-RequiredProperty $credentialSources[$index] 'replicaCount' 'credential source') -lt 0 -or
            [int](Get-RequiredProperty $credentialSources[$index] 'decryptedCredentialValues' 'credential source') -lt 0) {
            throw 'credential source has invalid counts'
        }
    }
    if ($SkipActualScopeRescan) { return }

    $repository = Get-E2eFullPath (Get-RequiredString $AuthorityContext 'repositoryRoot' 'authority context') 'repository root'
    $gameRoot = Get-E2eFullPath (Get-RequiredString $AuthorityContext 'gameRoot' 'authority context') 'game root'
    $settingsRoot = Get-E2eFullPath (Get-RequiredString $report 'settingsRoot' 'secret scan report') 'secret settings root'
    $expectedSettings = Get-E2eFullPath (Join-Path $gameRoot 'TaiWu-JianghuYouling-Logs\Settings') 'expected secret settings root'
    if (-not $settingsRoot.Equals($expectedSettings, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'secret scan report is bound to a non-authoritative Settings root'
    }
    $configured = Get-E2eConfiguredSecrets $settingsRoot
    Assert-ExactValue (Get-RequiredProperty $report 'exactSecretCount' 'secret scan report') $configured.exactSecretCount 'secret scan report.exactSecretCount'
    Assert-ExactValue (Get-RequiredProperty $report 'protectedConfigReplicaCount' 'secret scan report') $configured.protectedConfigReplicaCount 'secret scan report.protectedConfigReplicaCount'
    $actualCredentialSources = @($configured.sources)
    for ($index = 0; $index -lt $actualCredentialSources.Count; $index++) {
        Assert-ExactValue (Get-RequiredProperty $credentialSources[$index] 'replicaCount' 'credential source') $actualCredentialSources[$index].replicaCount 'credential source.replicaCount'
        Assert-ExactValue (Get-RequiredProperty $credentialSources[$index] 'decryptedCredentialValues' 'credential source') $actualCredentialSources[$index].decryptedCredentialValues 'credential source.decryptedCredentialValues'
    }

    $actualRepository = Get-E2eFileSetSecretScope 'repository' (Get-E2eRepositoryFileRows $repository) $configured.values @($repository)
    Assert-SecretScopeEqual $reportedScopes[0] $actualRepository 'secret scope repository'
    $actualHistory = Get-E2eGitHistorySecretScope $repository $configured.values
    Assert-SecretScopeEqual $reportedScopes[1] $actualHistory 'secret scope git-history'

    $commandRoots = @(Get-RequiredArray $reportedScopes[2] 'roots' 'secret scope command-output')
    if ($commandRoots.Count -ne 1) { throw 'command-output secret scope must have exactly one root' }
    $commandRoot = Get-E2eFullPath ([string]$commandRoots[0]) 'command-output root'
    $expectedCommandRoot = Get-E2eFullPath (Join-Path $RunSessionRoot 'command-output') 'expected command-output root'
    if (-not $commandRoot.Equals($expectedCommandRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'command-output secret scope is not the run-bound command-output directory'
    }
    $actualCommand = Get-E2eFileSetSecretScope 'command-output' (Get-E2eRecursiveFileRows $commandRoot '') $configured.values @($commandRoot)
    Assert-SecretScopeEqual $reportedScopes[2] $actualCommand 'secret scope command-output'

    $playerLog = Get-E2eFullPath $ExpectedPlayerLogPath 'expected Player.log'
    $playerPrevious = Join-Path (Split-Path -Parent $playerLog) 'Player-prev.log'
    $playerFiles = Get-RequiredWithOptionalSecretFileSet 'Player.log' $playerLog 'expected Player.log' `
        'Player-prev.log' $playerPrevious 'expected Player-prev.log'
    $actualPlayer = Get-E2eFileSetSecretScope 'player-log' $playerFiles.rows $configured.values $playerFiles.roots
    Assert-SecretScopeEqual $reportedScopes[3] $actualPlayer 'secret scope player-log'

    $metricsProperty = $AuthorityContext.PSObject.Properties['llmMetricsPath']
    $metricsValue = if ($null -ne $metricsProperty) { [string]$metricsProperty.Value } else { Join-Path $env:LOCALAPPDATA 'JianghuYouling\llm_metrics.jsonl' }
    $metrics = Get-E2eFullPath $metricsValue 'expected LLM metrics'
    $metricFiles = Get-RequiredWithOptionalSecretFileSet 'llm_metrics.jsonl' $metrics 'expected LLM metrics' `
        'llm_metrics.jsonl.1' ($metrics + '.1') 'expected rotated LLM metrics'
    $actualMetrics = Get-E2eFileSetSecretScope 'llm-metrics' $metricFiles.rows $configured.values $metricFiles.roots
    Assert-SecretScopeEqual $reportedScopes[4] $actualMetrics 'secret scope llm-metrics'

    $preservedExportRoot = Get-E2eFullPath (Join-Path $RunSessionRoot 'chat-exports') 'preserved chat-export root'
    if (-not (Test-Path -LiteralPath $preservedExportRoot -PathType Container)) {
        throw 'The restore workflow did not preserve test-run chat exports for post-restore scanning'
    }
    [void](Get-E2eTreeSnapshot $preservedExportRoot)
    $exportRoots = @(Get-ChildItem -LiteralPath $preservedExportRoot -Directory -Force |
        Sort-Object FullName | ForEach-Object { $_.FullName })
    if ($exportRoots.Count -eq 0) { throw 'No preserved test-run chat-export directories exist for scanning' }
    $exportRows = New-Object 'System.Collections.Generic.List[object]'
    for ($index = 0; $index -lt $exportRoots.Count; $index++) {
        foreach ($row in @(Get-E2eRecursiveFileRows $exportRoots[$index] ('root-{0:d2}' -f ($index + 1)))) { $exportRows.Add($row) }
    }
    $actualExports = Get-E2eFileSetSecretScope 'chat-exports' $exportRows.ToArray() $configured.values $exportRoots
    Assert-SecretScopeEqual $reportedScopes[5] $actualExports 'secret scope chat-exports'
}

function Assert-RepositoryContainsNoSecrets([string]$Root) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $rootPrefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    $git = (Get-Command git -ErrorAction Stop).Source
    $relativeFiles = @(& $git -C $rootFull -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed while preparing the repository secret scan' }
    $hits = New-Object 'System.Collections.Generic.List[string]'
    foreach ($relative in @($relativeFiles | Sort-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace([string]$relative)) { continue }
        $full = [IO.Path]::GetFullPath((Join-Path $rootFull ([string]$relative)))
        if (-not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "git ls-files returned a path outside the repository: $relative"
        }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
        $file = Get-Item -LiteralPath $full
        if ($file.Length -gt 8MB) { continue }
        try {
            $text = [IO.File]::ReadAllText($file.FullName)
            if ($text -match $script:SecretPattern) {
                $hits.Add(([string]$relative).Replace('\', '/'))
            }
        } catch { }
    }
    if ($hits.Count -gt 0) {
        throw ('Potential credential material found in repository files (values suppressed): ' + [string]::Join(', ', $hits))
    }
}

function Test-BackupAndRestore([object]$BackupMetadata, [object]$RestoreMetadata,
    [string]$ManifestDirectory, [DateTimeOffset]$Started, [DateTimeOffset]$Finished,
    [object]$AuthorityContext) {
    Assert-ExactValue (Get-RequiredString $BackupMetadata 'status' 'backup') 'PASS' 'backup.status'
    $backupPath = Assert-ArtifactHash (Get-RequiredString $BackupMetadata 'manifestPath' 'backup') (Get-RequiredString $BackupMetadata 'manifestSha256' 'backup') $ManifestDirectory 'backup.manifest'
    $backupHash = (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash
    $backup = Read-JsonFile $backupPath 'backup manifest'
    Assert-ExactValue (Get-RequiredProperty $backup 'schemaVersion' 'backup manifest') 2 'backup manifest.schemaVersion'
    Assert-ExactValue (Get-RequiredString $backup 'kind' 'backup manifest') $script:BackupManifestKind 'backup manifest.kind'
    Assert-ExactValue (Get-RequiredString $backup 'status' 'backup manifest') 'PASS' 'backup manifest.status'
    $created = ConvertTo-UtcTimestamp (Get-RequiredString $backup 'createdUtc' 'backup manifest') 'backup manifest.createdUtc'
    Assert-TimestampInRun $created $Started $Finished 'backup manifest.createdUtc'

    $sessionRoot = Get-E2eFullPath (Get-RequiredString $backup 'sessionRoot' 'backup manifest') 'backup session root'
    $backupDataRoot = Get-E2eFullPath (Get-RequiredString $backup 'backupDataRoot' 'backup manifest') 'backup data root'
    $gameRoot = Get-E2eFullPath (Get-RequiredString $AuthorityContext 'gameRoot' 'authority context') 'authority game root'
    Assert-ExactValue (Get-E2eFullPath (Get-RequiredString $backup 'gameRoot' 'backup manifest') 'backup game root') $gameRoot 'backup manifest.gameRoot'
    if (-not (Test-E2ePathWithin $backupDataRoot $sessionRoot) -or
        [IO.Path]::GetFileName($backupDataRoot) -cne 'backup-data') {
        throw 'backup manifest backupDataRoot is outside its session or has the wrong leaf'
    }
    if (Test-Path -LiteralPath $backupDataRoot) {
        throw 'backup copies still exist; E2E-137 cleanup is incomplete'
    }

    $rootRows = @(Get-RequiredArray $backup 'roots' 'backup manifest')
    if ($rootRows.Count -ne 2) { throw 'backup manifest must contain exactly SaveGames and ModLogs roots' }
    $requiredLabels = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    [void]$requiredLabels.Add('SaveGames'); [void]$requiredLabels.Add('ModLogs')
    $snapshots = @{}
    foreach ($rootRow in $rootRows) {
        $label = Get-RequiredString $rootRow 'label' 'backup manifest root'
        if (-not $requiredLabels.Remove($label)) { throw "backup manifest has duplicate or unknown root label '$label'" }
        $sourceRoot = Get-RequiredString $rootRow 'sourceRoot' "backup root $label"
        $backupRoot = Get-RequiredString $rootRow 'backupRoot' "backup root $label"
        if (-not [IO.Path]::IsPathRooted($sourceRoot) -or -not [IO.Path]::IsPathRooted($backupRoot)) {
            throw "backup root $label must use explicit absolute sourceRoot and backupRoot paths"
        }
        $sourceRoot = [IO.Path]::GetFullPath($sourceRoot)
        $backupRoot = [IO.Path]::GetFullPath($backupRoot)
        $expectedLeaf = if ($label -eq 'SaveGames') { 'SaveGames' } else { 'TaiWu-JianghuYouling-Logs' }
        $expectedSource = Get-E2eFullPath (Join-Path $gameRoot $expectedLeaf) "expected source root $label"
        if (-not $sourceRoot.Equals($expectedSource, [StringComparison]::OrdinalIgnoreCase)) {
            throw "backup root $label sourceRoot is not the authority-bound game path"
        }
        $expectedBackup = Get-E2eFullPath (Join-Path $backupDataRoot $expectedLeaf) "expected backup root $label"
        if (-not $backupRoot.Equals($expectedBackup, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-E2ePathWithin $backupRoot $backupDataRoot)) {
            throw "backup root $label backupRoot is outside the bound backup-data directory"
        }
        if ((Test-IsPathWithin $sourceRoot $backupRoot) -or (Test-IsPathWithin $backupRoot $sourceRoot)) {
            throw "backup root $label source and backup trees overlap"
        }
        $sourceSnapshot = Get-E2eTreeSnapshot $sourceRoot
        $manifestSnapshot = Get-E2eManifestSnapshot $rootRow "backup root $label"
        Assert-E2eSnapshotsEqual $sourceSnapshot $manifestSnapshot "backup root $label restored source"
        if (Test-Path -LiteralPath $backupRoot) { throw "backup root $label was not deleted after restore" }
        $snapshots[$label] = $manifestSnapshot
    }
    if ($requiredLabels.Count -ne 0) { throw 'backup manifest is missing a required root' }

    Assert-ExactValue (Get-RequiredString $RestoreMetadata 'status' 'restore') 'PASS' 'restore.status'
    $restorePath = Assert-ArtifactHash (Get-RequiredString $RestoreMetadata 'verificationPath' 'restore') (Get-RequiredString $RestoreMetadata 'verificationSha256' 'restore') $ManifestDirectory 'restore.verification'
    $restore = Read-JsonFile $restorePath 'restore verification'
    Assert-ExactValue (Get-RequiredProperty $restore 'schemaVersion' 'restore verification') 2 'restore verification.schemaVersion'
    Assert-ExactValue (Get-RequiredString $restore 'kind' 'restore verification') $script:RestoreReportKind 'restore verification.kind'
    Assert-ExactValue (Get-RequiredString $restore 'status' 'restore verification') 'PASS' 'restore verification.status'
    Assert-ExactValue (Get-RequiredString $restore 'backupManifestSha256' 'restore verification') $backupHash 'restore verification.backupManifestSha256'
    Assert-ExactValue (Get-RequiredProperty $restore 'orphanTemporaryFiles' 'restore verification') 0 'restore verification.orphanTemporaryFiles'
    Assert-ExactValue (Get-RequiredProperty $restore 'backupCopiesDeleted' 'restore verification') $true 'restore verification.backupCopiesDeleted'
    Assert-ExactValue (Get-RequiredProperty $restore 'quarantineDirectoriesDeleted' 'restore verification') $true 'restore verification.quarantineDirectoriesDeleted'
    $reportedBackupDataRoot = Get-E2eFullPath (Get-RequiredString $restore 'backupDataRoot' 'restore verification') 'restore backup data root'
    if (-not $reportedBackupDataRoot.Equals($backupDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'restore verification refers to a different backup data root'
    }
    $preserved = Get-RequiredProperty $restore 'preservedChatExports' 'restore verification'
    $preservedRoot = Get-E2eFullPath (Get-RequiredString $preserved 'rootPath' 'preserved chat exports') 'preserved chat-export root'
    $expectedPreservedRoot = Get-E2eFullPath (Join-Path $sessionRoot 'chat-exports') 'expected preserved chat-export root'
    if (-not $preservedRoot.Equals($expectedPreservedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'restore verification preserved chat-export root is outside its session'
    }
    $preservedSnapshot = Get-E2eTreeSnapshot $preservedRoot
    if ([int](Get-RequiredProperty $preserved 'sourceDirectoryCount' 'preserved chat exports') -lt 1 -or
        $preservedSnapshot.fileCount -lt 1) {
        throw 'restore verification preserved no real test-run chat exports'
    }
    foreach ($name in @('fileCount', 'directoryCount', 'totalBytes', 'treeSha256')) {
        Assert-ExactValue (Get-RequiredProperty $preserved $name 'preserved chat exports') $preservedSnapshot.$name "preserved chat exports.$name"
    }
    $verified = ConvertTo-UtcTimestamp (Get-RequiredString $restore 'verifiedUtc' 'restore verification') 'restore verification.verifiedUtc'
    Assert-TimestampInRun $verified $Started $Finished 'restore verification.verifiedUtc'
    if ($verified -lt $created) { throw 'restore verification predates the backup manifest' }
    $restoreRoots = @(Get-RequiredArray $restore 'roots' 'restore verification')
    if ($restoreRoots.Count -ne 2) { throw 'restore verification must contain exactly two roots' }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($row in $restoreRoots) {
        $label = Get-RequiredString $row 'label' 'restore root'
        if (-not $snapshots.ContainsKey($label) -or -not $seen.Add($label)) {
            throw "restore verification has duplicate or unknown root '$label'"
        }
        Assert-ExactValue (Get-RequiredString $row 'status' "restore root $label") 'PASS' "restore root $label.status"
        Assert-ExactValue (Get-RequiredString $row 'treeSha256' "restore root $label") $snapshots[$label].treeSha256 "restore root $label.treeSha256"
        Assert-ExactValue (Get-RequiredProperty $row 'fileCount' "restore root $label") $snapshots[$label].fileCount "restore root $label.fileCount"
        Assert-ExactValue (Get-RequiredProperty $row 'directoryCount' "restore root $label") $snapshots[$label].directoryCount "restore root $label.directoryCount"
        Assert-ExactValue (Get-RequiredProperty $row 'totalBytes' "restore root $label") $snapshots[$label].totalBytes "restore root $label.totalBytes"
    }
    if ($seen.Count -ne 2) { throw 'restore verification is incomplete' }
    $orphans = @(Get-E2eOrphanTemporaryFiles @($gameRoot, $sessionRoot))
    if ($orphans.Count -ne 0) { throw 'E2E-owned orphan temporary files exist despite the PASS restore report' }
    return [pscustomobject][ordered]@{
        sessionRoot = $sessionRoot
        preservedChatExportsRoot = $preservedRoot
    }
}

function Test-CaseEvidence([object]$Case, [string]$ManifestDirectory,
    [DateTimeOffset]$Started, [DateTimeOffset]$Finished,
    [Collections.Generic.HashSet[string]]$EvidenceSlices) {
    $id = Get-RequiredString $Case 'id' 'case'
    Assert-ExactValue (Get-RequiredString $Case 'status' "case $id") 'PASS' "case $id.status"
    $verified = ConvertTo-UtcTimestamp (Get-RequiredString $Case 'verifiedUtc' "case $id") "case $id.verifiedUtc"
    Assert-TimestampInRun $verified $Started $Finished "case $id.verifiedUtc"
    $levels = @(Get-RequiredArray $Case 'levels' "case $id")
    if ($levels.Count -eq 0) { throw "case $id must declare at least one evidence level" }
    $levelSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($levelValue in $levels) {
        $level = ([string]$levelValue).Trim()
        if ($level -notin @('U', 'I', 'F', 'G', 'D')) { throw "case $id has unknown evidence level '$level'" }
        if (-not $levelSet.Add($level)) { throw "case $id repeats evidence level '$level'" }
    }
    $evidence = @(Get-RequiredArray $Case 'evidence' "case $id")
    if ($evidence.Count -eq 0) { throw "case $id has no concrete evidence" }
    $index = 0
    foreach ($item in $evidence) {
        $index++
        $where = "case $id evidence[$index]"
        $kind = Get-RequiredString $item 'kind' $where
        if ($kind -notin @('log', 'screenshot', 'metric', 'export', 'hash-manifest', 'game-state', 'video', 'test-report')) {
            throw "$where has unsupported kind '$kind'"
        }
        $pathText = Get-RequiredString $item 'artifactPath' $where
        $shaText = Get-RequiredString $item 'sha256' $where
        $path = Assert-ArtifactHash $pathText $shaText $ManifestDirectory $where
        $locator = Get-RequiredString $item 'locator' $where
        $observation = Get-RequiredString $item 'observation' $where
        if ($locator.Length -lt 6 -or $locator -match '(?i)^\s*(pass|ok|all|success|passed)\s*$') {
            throw "$where.locator is too broad to identify a concrete evidence slice"
        }
        if ($observation.Length -lt 16 -or $observation -match '(?i)^\s*(pass|ok|all tests? pass(?:ed)?|success|works?)\s*[.!]?\s*$') {
            throw "$where.observation is too broad; record the exact state and expected result"
        }
        $textKinds = @('log', 'metric', 'export', 'hash-manifest', 'game-state', 'test-report')
        if ($kind -in $textKinds) {
            $info = Get-Item -LiteralPath $path
            if ($info.Length -gt 16MB) {
                throw "$where textual artifact exceeds the 16 MiB inspectable evidence limit"
            }
            try { $artifactText = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) }
            catch { throw "$where textual artifact is not readable strict evidence text" }
            if ($artifactText.IndexOf($locator, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                throw "$where.locator does not occur in the hashed textual artifact"
            }
        }
        $captured = ConvertTo-UtcTimestamp (Get-RequiredString $item 'capturedUtc' $where) "$where.capturedUtc"
        Assert-TimestampInRun $captured $Started $Finished "$where.capturedUtc"
        $sliceKey = $path.ToLowerInvariant() + '|' + $locator.ToLowerInvariant()
        if (-not $EvidenceSlices.Add($sliceKey)) {
            throw "$where reuses the same artifact+locator evidence slice for more than one checklist item"
        }
        if ($kind -in $textKinds) {
            $info = Get-Item -LiteralPath $path
            if ($info.Length -le 16MB) {
                try {
                    if ([IO.File]::ReadAllText($path) -match $script:SecretPattern) {
                        throw "$where artifact contains potential credential material (value suppressed)"
                    }
                } catch {
                    if ($_.Exception.Message -like '*potential credential material*') { throw }
                }
            }
        }
    }
}

function Test-E2eEvidenceManifest([string]$EvidenceManifestPath, [string]$AcceptanceChecklistPath,
    [object]$AuthorityContext, [int]$AllowedAgeHours = 72, [switch]$SkipRepositorySecretScan) {
    $manifestFull = [IO.Path]::GetFullPath($EvidenceManifestPath)
    if (-not (Test-Path -LiteralPath $manifestFull -PathType Leaf)) { throw "Evidence manifest missing: $manifestFull" }
    $checklistFull = [IO.Path]::GetFullPath($AcceptanceChecklistPath)
    $manifestDirectory = Split-Path -Parent $manifestFull
    $requiredIds = @(Read-E2eChecklistIds $checklistFull)
    $manifest = Read-JsonFile $manifestFull 'real-game evidence manifest'
    Assert-ExactValue (Get-RequiredProperty $manifest 'schemaVersion' 'manifest') 1 'manifest.schemaVersion'
    Assert-ExactValue (Get-RequiredString $manifest 'kind' 'manifest') $script:EvidenceKind 'manifest.kind'
    $runId = Get-RequiredString $manifest 'runId' 'manifest'
    if ($runId.Length -lt 8) { throw 'manifest.runId is too short' }
    $started = ConvertTo-UtcTimestamp (Get-RequiredString $manifest 'startedUtc' 'manifest') 'manifest.startedUtc'
    $finished = ConvertTo-UtcTimestamp (Get-RequiredString $manifest 'finishedUtc' 'manifest') 'manifest.finishedUtc'
    if ($finished -lt $started) { throw 'manifest.finishedUtc predates startedUtc' }
    $now = [DateTimeOffset]::UtcNow
    if ($finished -gt $now.AddMinutes(10)) { throw 'manifest.finishedUtc is implausibly in the future' }
    if ($finished -lt $now.AddHours(-1 * [Math]::Abs($AllowedAgeHours))) {
        throw "manifest is older than the allowed $AllowedAgeHours-hour evidence window"
    }

    $source = Get-RequiredProperty $manifest 'source' 'manifest'
    $gitSha = Get-RequiredString $source 'gitSha' 'source'
    Assert-ExactValue $gitSha (Get-RequiredString $AuthorityContext 'gitSha' 'authority context') 'source.gitSha'
    if ($gitSha -cnotmatch '^[A-Fa-f0-9]{40}(?:[A-Fa-f0-9]{24})?$') { throw 'source.gitSha is not a full Git object ID' }
    Assert-ExactValue (Get-RequiredProperty $source 'workingTreeClean' 'source') $true 'source.workingTreeClean'
    Assert-ExactValue (Get-RequiredString $source 'buildConfiguration' 'source') 'Release' 'source.buildConfiguration'
    [void](Assert-ArtifactHash (Get-RequiredString $source 'releaseArtifactManifestPath' 'source') (Get-RequiredString $source 'releaseArtifactManifestSha256' 'source') $manifestDirectory 'source.releaseArtifactManifest')

    $game = Get-RequiredProperty $manifest 'game' 'manifest'
    Assert-ExactValue (Get-RequiredString $game 'buildId' 'game') (Get-RequiredString $AuthorityContext 'buildId' 'authority context') 'game.buildId'
    Assert-ExactValue (Get-RequiredString $game 'appManifestSha256' 'game') (Get-RequiredString $AuthorityContext 'appManifestSha256' 'authority context') 'game.appManifestSha256'
    $reportedAppManifest = [IO.Path]::GetFullPath((Get-RequiredString $game 'appManifestPath' 'game'))
    Assert-ExactValue $reportedAppManifest (Get-RequiredString $AuthorityContext 'appManifestPath' 'authority context') 'game.appManifestPath'
    [void](Assert-ArtifactHash (Get-RequiredString $game 'playerLogPath' 'game') (Get-RequiredString $game 'playerLogSha256' 'game') $manifestDirectory 'game.playerLog')

    $decompile = Get-RequiredProperty $manifest 'decompile' 'manifest'
    Assert-ExactValue ([IO.Path]::GetFullPath((Get-RequiredString $decompile 'rootPath' 'decompile'))) (Get-RequiredString $AuthorityContext 'decompileRoot' 'authority context') 'decompile.rootPath'
    Assert-ExactValue (Get-RequiredString $decompile 'completeMarkerSha256' 'decompile') (Get-RequiredString $AuthorityContext 'completeMarkerSha256' 'authority context') 'decompile.completeMarkerSha256'
    Assert-ExactValue (Get-RequiredString $decompile 'sourceAssembliesManifestSha256' 'decompile') (Get-RequiredString $AuthorityContext 'sourceAssembliesManifestSha256' 'authority context') 'decompile.sourceAssembliesManifestSha256'
    Assert-ExactValue (Get-RequiredProperty $decompile 'assemblyCount' 'decompile') (Get-RequiredProperty $AuthorityContext 'assemblyCount' 'authority context') 'decompile.assemblyCount'
    Assert-ExactValue (Get-RequiredProperty $decompile 'csFileCount' 'decompile') (Get-RequiredProperty $AuthorityContext 'csFileCount' 'authority context') 'decompile.csFileCount'

    $dataProtection = Test-BackupAndRestore (Get-RequiredProperty $manifest 'backup' 'manifest') (Get-RequiredProperty $manifest 'restore' 'manifest') $manifestDirectory $started $finished $AuthorityContext
    Test-SecretScanReport (Get-RequiredProperty $manifest 'secretScan' 'manifest') $manifestDirectory $started $finished $gitSha `
        $AuthorityContext (Get-RequiredString $game 'playerLogPath' 'game') $dataProtection.sessionRoot `
        -SkipActualScopeRescan:$SkipRepositorySecretScan
    if (-not $SkipRepositorySecretScan) {
        Assert-RepositoryContainsNoSecrets (Get-RequiredString $AuthorityContext 'repositoryRoot' 'authority context')
    }
    $cases = @(Get-RequiredArray $manifest 'cases' 'manifest')
    $caseById = @{}
    foreach ($case in $cases) {
        $id = Get-RequiredString $case 'id' 'case'
        if ($id -cnotmatch '^E2E-(?:T\d{2}|\d{3})$') { throw "Unknown/malformed case ID '$id'" }
        if ($caseById.ContainsKey($id)) { throw "Duplicate case ID '$id'" }
        $caseById[$id] = $case
    }
    $requiredSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($id in $requiredIds) { [void]$requiredSet.Add($id) }
    foreach ($id in $caseById.Keys) {
        if (-not $requiredSet.Contains([string]$id)) { throw "Manifest contains unknown case ID '$id'" }
    }
    $missing = @($requiredIds | Where-Object { -not $caseById.ContainsKey($_) })
    if ($missing.Count -gt 0) { throw ('Manifest is missing checklist IDs: ' + [string]::Join(', ', $missing)) }
    if ($cases.Count -ne $requiredIds.Count) {
        throw "Manifest case count $($cases.Count) does not equal checklist count $($requiredIds.Count)"
    }
    $slices = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($id in $requiredIds) { Test-CaseEvidence $caseById[$id] $manifestDirectory $started $finished $slices }

    return [pscustomobject][ordered]@{
        runId = $runId
        gitSha = $gitSha
        buildId = Get-RequiredString $game 'buildId' 'game'
        checklistCount = $requiredIds.Count
        caseCount = $cases.Count
        uniqueEvidenceSlices = $slices.Count
        manifestSha256 = (Get-FileHash -LiteralPath $manifestFull -Algorithm SHA256).Hash
    }
}

function New-ProductionAuthorityContext([string]$Root, [string]$SteamRoot) {
    $rootFull = [IO.Path]::GetFullPath($Root)
    $git = (Get-Command git -ErrorAction Stop).Source
    $gitSha = (& $git -C $rootFull rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitSha)) { throw 'Cannot resolve repository Git HEAD' }
    $dirty = @(& $git -C $rootFull status --porcelain --untracked-files=all 2>$null)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect repository working tree state' }
    if ($dirty.Count -ne 0) { throw 'Real-game evidence gate requires a clean working tree tied to source.gitSha' }

    $appManifest = [IO.Path]::GetFullPath((Join-Path $SteamRoot 'appmanifest_838350.acf'))
    if (-not (Test-Path -LiteralPath $appManifest -PathType Leaf)) { throw "Taiwu appmanifest missing: $appManifest" }
    $buildLine = Select-String -LiteralPath $appManifest -Pattern '"buildid"\s+"([0-9]+)"' | Select-Object -First 1
    if ($null -eq $buildLine) { throw 'Cannot parse Taiwu buildid from appmanifest' }
    $buildId = [regex]::Match($buildLine.Line, '"buildid"\s+"([0-9]+)"').Groups[1].Value
    $decompileRoot = [IO.Path]::GetFullPath((Join-Path (Resolve-DecompiledBase $rootFull) ('taiwu_decomp_b' + $buildId)))
    $completeMarker = Join-Path $decompileRoot '_COMPLETE'
    $sourceManifest = Join-Path $decompileRoot 'SOURCE_ASSEMBLIES.sha256'
    if (-not (Test-Path -LiteralPath $completeMarker -PathType Leaf)) { throw "Decompile _COMPLETE missing: $completeMarker" }
    if (-not (Test-Path -LiteralPath $sourceManifest -PathType Leaf)) { throw "Decompile source manifest missing: $sourceManifest" }
    $gameRoot = Join-Path $SteamRoot 'common\The Scroll Of Taiwu'
    $assemblyCount = 0
    foreach ($row in @(Get-Content -LiteralPath $sourceManifest -Encoding UTF8 | Select-Object -Skip 1)) {
        if ([string]::IsNullOrWhiteSpace($row)) { continue }
        $parts = $row.Split('|')
        if ($parts.Length -ne 4) { throw 'Malformed SOURCE_ASSEMBLIES.sha256 row' }
        $scope = $parts[0].Split('/', 2)
        if ($scope.Length -ne 2) { throw 'Malformed SOURCE_ASSEMBLIES.sha256 scope' }
        $installed = if ($scope[0] -eq 'Backend') {
            Join-Path (Join-Path $gameRoot 'Backend') $scope[1]
        } else {
            Join-Path (Join-Path $gameRoot 'The Scroll of Taiwu_Data\Managed') $scope[1]
        }
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) { throw "Installed authority assembly missing: $($parts[0])" }
        $actual = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
        if ($actual -cne $parts[3].ToUpperInvariant()) { throw "Installed authority hash mismatch: $($parts[0])" }
        $assemblyCount++
    }
    if ($assemblyCount -ne 33) { throw "Authority must contain 33 verified assemblies (actual $assemblyCount)" }
    $csFileCount = @(Get-ChildItem -LiteralPath $decompileRoot -Filter '*.cs' -File -Recurse).Count
    if ($csFileCount -le 0) { throw 'Decompile authority contains no C# files' }
    return [pscustomobject][ordered]@{
        repositoryRoot = $rootFull
        gameRoot = [IO.Path]::GetFullPath($gameRoot)
        llmMetricsPath = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'JianghuYouling\llm_metrics.jsonl'))
        gitSha = ([string]$gitSha).Trim()
        buildId = $buildId
        appManifestPath = $appManifest
        appManifestSha256 = (Get-FileHash -LiteralPath $appManifest -Algorithm SHA256).Hash
        decompileRoot = $decompileRoot
        completeMarkerSha256 = (Get-FileHash -LiteralPath $completeMarker -Algorithm SHA256).Hash
        sourceAssembliesManifestSha256 = (Get-FileHash -LiteralPath $sourceManifest -Algorithm SHA256).Hash
        assemblyCount = $assemblyCount
        csFileCount = $csFileCount
    }
}

if (-not $LibraryMode) {
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        throw 'Provide -ManifestPath for the real-game full-Mod E2E evidence manifest.'
    }
    Write-Host 'REAL-GAME FULL-MOD E2E EVIDENCE GATE (read-only)' -ForegroundColor Cyan
    $context = New-ProductionAuthorityContext $RepositoryRoot $SteamAppsRoot
    $result = Test-E2eEvidenceManifest $ManifestPath $ChecklistPath $context $MaxAgeHours
    Write-Host ("PASS: {0} checklist items, {1} unique concrete evidence slices; build={2}; git={3}; manifest={4}" -f $result.caseCount, $result.uniqueEvidenceSlices, $result.buildId, $result.gitSha, $result.manifestSha256) -ForegroundColor Green
}
