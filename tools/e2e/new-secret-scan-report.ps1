param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [Parameter(Mandatory = $true)]
    [string]$CommandOutputRoot,
    [string]$PlayerLogPath = "$env:USERPROFILE\AppData\LocalLow\Conchship\The Scroll of Taiwu\Player.log",
    [string]$LlmMetricsPath = "$env:LOCALAPPDATA\JianghuYouling\llm_metrics.jsonl",
    [Parameter(Mandatory = $true)]
    [string[]]$ChatExportRoots,
    [string]$SettingsRoot = 'C:\Program Files (x86)\Steam\steamapps\common\The Scroll Of Taiwu\TaiWu-JianghuYouling-Logs\Settings'
)

# Generates the six-scope release secret report from actual bytes. The exact
# DPAPI-backed llm/tts/minimax credentials are decrypted only in memory and are
# never written, printed, fingerprinted, or passed on a process command line.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'RealGameE2eSafety.ps1')

function New-SingleFileRow([string]$Identity, [string]$Path, [string]$Where) {
    $full = Get-E2eFullPath $Path $Where
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "$Where is missing" }
    return [pscustomobject][ordered]@{ identity = $Identity; path = $full }
}

function New-RequiredWithOptionalFileSet([string]$RequiredIdentity, [string]$RequiredPath,
    [string]$RequiredWhere, [string]$OptionalIdentity, [string]$OptionalPath,
    [string]$OptionalWhere) {
    $required = New-SingleFileRow $RequiredIdentity $RequiredPath $RequiredWhere
    $optionalFull = Get-E2eFullPath $OptionalPath $OptionalWhere
    if ($required.path.Equals($optionalFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$OptionalWhere aliases the required scan input"
    }
    $rows = New-Object 'System.Collections.Generic.List[object]'
    $roots = New-Object 'System.Collections.Generic.List[string]'
    $rows.Add($required)
    $roots.Add([string]$required.path)
    if (Test-Path -LiteralPath $optionalFull) {
        if (-not (Test-Path -LiteralPath $optionalFull -PathType Leaf)) {
            throw "$OptionalWhere exists but is not a file"
        }
        $rows.Add((New-SingleFileRow $OptionalIdentity $optionalFull $OptionalWhere))
        $roots.Add($optionalFull)
    }
    return [pscustomobject][ordered]@{
        rows = $rows.ToArray()
        roots = $roots.ToArray()
    }
}

function Assert-FileScopeMatches([object]$Actual, [object]$Expected, [string]$Where) {
    foreach ($name in @('fileCount', 'totalBytes', 'snapshotSha256', 'findings', 'status')) {
        if ([string]$Actual.$name -cne [string]$Expected.$name) { throw "$Where changed while it was scanned" }
    }
}

try {
    Assert-E2eGameProcessesClosed
    $outputFull = [IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $outputFull) { throw 'Secret report output already exists' }
    $repository = Get-E2eFullPath $RepositoryRoot 'repository root'
    $commandRoot = Get-E2eFullPath $CommandOutputRoot 'command-output root'
    if (-not (Test-Path -LiteralPath $commandRoot -PathType Container)) { throw 'Command-output root is missing' }
    if (Test-E2ePathWithin $outputFull $commandRoot -AllowEqual) {
        throw 'Secret report output must be outside the command-output scan root'
    }
    $playerLog = Get-E2eFullPath $PlayerLogPath 'Player.log'
    $metrics = Get-E2eFullPath $LlmMetricsPath 'llm metrics'
    $playerPrevious = Get-E2eFullPath (Join-Path (Split-Path -Parent $playerLog) 'Player-prev.log') 'Player-prev.log'
    $metricsRotated = Get-E2eFullPath ($metrics + '.1') 'rotated LLM metrics'
    foreach ($scanInput in @($playerLog, $playerPrevious, $metrics, $metricsRotated)) {
        if ($outputFull.Equals($scanInput, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Secret report cannot overwrite a current or rotated scan input'
        }
    }
    $playerFiles = New-RequiredWithOptionalFileSet 'Player.log' $playerLog 'Player.log' `
        'Player-prev.log' $playerPrevious 'Player-prev.log'
    $metricFiles = New-RequiredWithOptionalFileSet 'llm_metrics.jsonl' $metrics 'llm metrics' `
        'llm_metrics.jsonl.1' $metricsRotated 'rotated LLM metrics'
    $settings = Get-E2eFullPath $SettingsRoot 'settings root'

    $resolvedExports = New-Object 'System.Collections.Generic.List[string]'
    $seenExports = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($rootValue in @($ChatExportRoots)) {
        $root = Get-E2eFullPath $rootValue 'chat-export root'
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Chat-export root is missing' }
        if (-not $seenExports.Add($root)) { throw 'Chat-export roots contain a duplicate' }
        if (Test-E2ePathWithin $outputFull $root -AllowEqual) {
            throw 'Secret report output must be outside every chat-export root'
        }
        foreach ($existing in $resolvedExports.ToArray()) {
            if ((Test-E2ePathWithin $root $existing -AllowEqual) -or
                (Test-E2ePathWithin $existing $root -AllowEqual)) {
                throw 'Chat-export scan roots must not overlap'
            }
        }
        $resolvedExports.Add($root)
    }
    if ($resolvedExports.Count -eq 0) { throw 'At least one chat-export root is required' }
    $exportRootArray = @($resolvedExports.ToArray() | Sort-Object)

    $git = (Get-Command git -ErrorAction Stop).Source
    $gitSha = (& $git -C $repository rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitSha)) { throw 'Cannot resolve repository Git HEAD' }
    $gitSha = ([string]$gitSha).Trim()

    $configured = Get-E2eConfiguredSecrets $settings
    $scopes = New-Object 'System.Collections.Generic.List[object]'

    $repositoryRows = @(Get-E2eRepositoryFileRows $repository)
    $repositoryScope = Get-E2eFileSetSecretScope 'repository' $repositoryRows $configured.values @($repository)
    $scopes.Add($repositoryScope)

    $historyScope = Get-E2eGitHistorySecretScope $repository $configured.values
    $scopes.Add($historyScope)

    $commandRows = @(Get-E2eRecursiveFileRows $commandRoot '')
    $commandScope = Get-E2eFileSetSecretScope 'command-output' $commandRows $configured.values @($commandRoot)
    $scopes.Add($commandScope)

    $playerScope = Get-E2eFileSetSecretScope 'player-log' $playerFiles.rows $configured.values $playerFiles.roots
    $scopes.Add($playerScope)

    $metricScope = Get-E2eFileSetSecretScope 'llm-metrics' $metricFiles.rows $configured.values $metricFiles.roots
    $scopes.Add($metricScope)

    $exportRows = New-Object 'System.Collections.Generic.List[object]'
    $exportIndex = 0
    foreach ($root in $exportRootArray) {
        $exportIndex++
        foreach ($row in @(Get-E2eRecursiveFileRows $root ('root-{0:d2}' -f $exportIndex))) {
            $exportRows.Add($row)
        }
    }
    $exportScope = Get-E2eFileSetSecretScope 'chat-exports' $exportRows.ToArray() $configured.values $exportRootArray
    $scopes.Add($exportScope)

    # Re-snapshot mutable file scopes before signing the report. This rejects a
    # log/export that changed between its scan and report publication.
    Assert-FileScopeMatches (Get-E2eFileSetSecretScope 'repository' (Get-E2eRepositoryFileRows $repository) $configured.values @($repository)) $repositoryScope 'repository scope'
    Assert-FileScopeMatches (Get-E2eFileSetSecretScope 'command-output' (Get-E2eRecursiveFileRows $commandRoot '') $configured.values @($commandRoot)) $commandScope 'command-output scope'
    $playerFilesAgain = New-RequiredWithOptionalFileSet 'Player.log' $playerLog 'Player.log' `
        'Player-prev.log' $playerPrevious 'Player-prev.log'
    Assert-FileScopeMatches (Get-E2eFileSetSecretScope 'player-log' $playerFilesAgain.rows $configured.values $playerFilesAgain.roots) $playerScope 'Player log scope'
    $metricFilesAgain = New-RequiredWithOptionalFileSet 'llm_metrics.jsonl' $metrics 'llm metrics' `
        'llm_metrics.jsonl.1' $metricsRotated 'rotated LLM metrics'
    Assert-FileScopeMatches (Get-E2eFileSetSecretScope 'llm-metrics' $metricFilesAgain.rows $configured.values $metricFilesAgain.roots) $metricScope 'LLM metrics scope'
    $exportRowsAgain = New-Object 'System.Collections.Generic.List[object]'
    $exportIndex = 0
    foreach ($root in $exportRootArray) {
        $exportIndex++
        foreach ($row in @(Get-E2eRecursiveFileRows $root ('root-{0:d2}' -f $exportIndex))) { $exportRowsAgain.Add($row) }
    }
    Assert-FileScopeMatches (Get-E2eFileSetSecretScope 'chat-exports' $exportRowsAgain.ToArray() $configured.values $exportRootArray) $exportScope 'chat-export scope'

    $findings = 0
    foreach ($scope in $scopes.ToArray()) { $findings += [int]$scope.findings }
    $report = [pscustomobject][ordered]@{
        schemaVersion = 2
        kind = $script:E2eSecretReportKind
        status = if ($findings -eq 0) { 'PASS' } else { 'FAIL' }
        findings = $findings
        gitSha = $gitSha
        scannedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        scanPolicy = 'generic-pattern-and-exact-dpapi-configured-values-v3'
        settingsRoot = $settings
        exactSecretCount = $configured.exactSecretCount
        protectedConfigReplicaCount = $configured.protectedConfigReplicaCount
        credentialSources = $configured.sources
        scopes = $scopes.ToArray()
    }
    Write-E2eJsonAtomic $outputFull $report
    if ($findings -ne 0) {
        [Console]::Error.WriteLine(('SECRET SCAN FAIL findings={0}; values suppressed' -f $findings))
        exit 2
    }
    Write-Host ('SECRET SCAN PASS scopes=6 files={0} exactConfiguredValues={1} report={2} sha256={3}' -f
        (($scopes.ToArray() | Measure-Object -Property fileCount -Sum).Sum),
        $configured.exactSecretCount, $outputFull,
        (Get-FileHash -LiteralPath $outputFull -Algorithm SHA256).Hash)
}
catch {
    $typeName = if ($null -ne $_.Exception) { $_.Exception.GetType().Name } else { 'UnknownError' }
    [Console]::Error.WriteLine(('SECRET SCAN FAILED CLOSED ({0}); credential values and file contents were suppressed' -f $typeName))
    exit 1
}
