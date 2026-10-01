param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Backup', 'Restore')]
    [string]$Mode,
    [Parameter(Mandatory = $true)]
    [string]$SessionRoot,
    [Parameter(Mandatory = $true)]
    [string]$AllowedBackupParent,
    [string]$GameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\The Scroll Of Taiwu',
    [string]$BackupManifestPath,
    [string]$ExpectedBackupManifestSha256,
    [string]$RestoreReportPath
)

# The only mutating real-game E2E helper. Backup and restore are separate,
# explicit invocations. Both require the Taiwu frontend and GameData backend to
# be closed. Restore deletes backup copies only after both source trees match
# the immutable per-file manifest.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

. (Join-Path $PSScriptRoot 'RealGameE2eSafety.ps1')

function Get-Property([object]$Object, [string]$Name, [string]$Where) {
    if ($null -eq $Object) { throw "$Where is null" }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "$Where is missing required metadata" }
    return $property.Value
}

function Get-String([object]$Object, [string]$Name, [string]$Where) {
    $value = Get-Property $Object $Name $Where
    if ($null -eq $value -or [string]::IsNullOrWhiteSpace([string]$value)) {
        throw "$Where has empty required metadata"
    }
    return ([string]$value).Trim()
}

function Get-BackupRoots([string]$ResolvedGameRoot) {
    return @(
        [pscustomobject][ordered]@{
            label = 'SaveGames'
            sourceRoot = Join-Path $ResolvedGameRoot 'SaveGames'
            backupLeaf = 'SaveGames'
        },
        [pscustomobject][ordered]@{
            label = 'ModLogs'
            sourceRoot = Join-Path $ResolvedGameRoot 'TaiWu-JianghuYouling-Logs'
            backupLeaf = 'TaiWu-JianghuYouling-Logs'
        }
    )
}

function Copy-SnapshotToRoot([object]$Snapshot, [string]$SourceRoot, [string]$DestinationRoot) {
    Copy-E2eSnapshotToRoot $Snapshot $SourceRoot $DestinationRoot
}

function New-RestoreVerification([string]$ManifestHash, [string]$BackupDataRoot,
    [string]$PreservedExportsRoot, [int]$PreservedSourceCount,
    [object[]]$ValidatedRoots, [string]$ResolvedGame, [string]$ResolvedSession) {
    $preservedSnapshot = Get-E2eTreeSnapshot $PreservedExportsRoot
    if ($PreservedSourceCount -lt 1 -or $preservedSnapshot.fileCount -lt 1) {
        throw 'No test-run chat exports were preserved before restore'
    }
    $restoreRoots = New-Object 'System.Collections.Generic.List[object]'
    foreach ($entry in $ValidatedRoots) {
        $restored = Get-E2eTreeSnapshot $entry.sourceRoot
        Assert-E2eSnapshotsEqual $restored $entry.expected ($entry.label + ' final source')
        $restoreRoots.Add([pscustomobject][ordered]@{
            label = $entry.label; status = 'PASS'
            treeSha256 = $restored.treeSha256; fileCount = $restored.fileCount
            directoryCount = $restored.directoryCount; totalBytes = $restored.totalBytes
        })
    }
    $orphanFiles = @(Get-E2eOrphanTemporaryFiles @($ResolvedGame, $ResolvedSession))
    if ($orphanFiles.Count -ne 0) { throw 'E2E-owned orphan temporary artifacts remain after restore' }
    return [pscustomobject][ordered]@{
        schemaVersion = 2
        kind = $script:E2eRestoreReportKind
        status = 'PASS'
        verifiedUtc = [DateTimeOffset]::UtcNow.ToString('o')
        backupManifestSha256 = $ManifestHash
        backupCopiesDeleted = $true
        backupDataRoot = $BackupDataRoot
        orphanTemporaryFiles = 0
        quarantineDirectoriesDeleted = $true
        preservedChatExports = [pscustomobject][ordered]@{
            rootPath = (Get-E2eFullPath $PreservedExportsRoot 'preserved chat-export root')
            sourceDirectoryCount = $PreservedSourceCount
            fileCount = $preservedSnapshot.fileCount
            directoryCount = $preservedSnapshot.directoryCount
            totalBytes = $preservedSnapshot.totalBytes
            treeSha256 = $preservedSnapshot.treeSha256
        }
        roots = $restoreRoots.ToArray()
    }
}

function Remove-KnownRestoreArtifacts([string]$ResolvedGame) {
    $pattern = '^\.(?:SaveGames|TaiWu-JianghuYouling-Logs)\.jyl-(?:restore|testdata)-[A-Fa-f0-9]{32}$'
    foreach ($directory in @(Get-ChildItem -LiteralPath $ResolvedGame -Directory -Force)) {
        if ($directory.Name -notmatch $pattern) { continue }
        Remove-E2eContainedTree $directory.FullName $ResolvedGame $directory.Name
    }
}

function Invoke-Backup([string]$ResolvedSession, [string]$ResolvedParent, [string]$ResolvedGame,
    [string]$ResolvedManifest) {
    if (Test-Path -LiteralPath $ResolvedSession) { throw 'Backup session already exists' }
    [IO.Directory]::CreateDirectory($ResolvedSession) | Out-Null
    Assert-E2ePathChainHasNoReparsePoint $ResolvedSession 'backup session'
    $backupDataRoot = Join-Path $ResolvedSession 'backup-data'
    [IO.Directory]::CreateDirectory($backupDataRoot) | Out-Null

    $manifestRoots = New-Object 'System.Collections.Generic.List[object]'
    foreach ($definition in @(Get-BackupRoots $ResolvedGame)) {
        $sourceRoot = Get-E2eFullPath $definition.sourceRoot ($definition.label + ' source')
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
            throw ($definition.label + ' source tree is missing')
        }
        if (-not (Test-E2ePathWithin $sourceRoot $ResolvedGame)) { throw 'Source tree escapes the game root' }
        $before = Get-E2eTreeSnapshot $sourceRoot
        $backupRoot = Join-Path $backupDataRoot $definition.backupLeaf
        Copy-SnapshotToRoot $before $sourceRoot $backupRoot
        $after = Get-E2eTreeSnapshot $sourceRoot
        $copied = Get-E2eTreeSnapshot $backupRoot
        Assert-E2eSnapshotsEqual $after $before ($definition.label + ' changed during backup')
        Assert-E2eSnapshotsEqual $copied $before ($definition.label + ' backup')
        $manifestRoots.Add([pscustomobject][ordered]@{
            label = $definition.label
            sourceRoot = $sourceRoot
            backupRoot = (Get-E2eFullPath $backupRoot 'backup root')
            fileCount = $before.fileCount
            directoryCount = $before.directoryCount
            totalBytes = $before.totalBytes
            treeSha256 = $before.treeSha256
            directories = @($before.directories)
            files = @($before.files)
        })
    }

    $manifest = [pscustomobject][ordered]@{
        schemaVersion = 2
        kind = $script:E2eBackupManifestKind
        status = 'PASS'
        runId = [IO.Path]::GetFileName($ResolvedSession)
        createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
        sessionRoot = $ResolvedSession
        backupDataRoot = (Get-E2eFullPath $backupDataRoot 'backup data root')
        gameRoot = $ResolvedGame
        roots = $manifestRoots.ToArray()
    }
    Write-E2eJsonAtomic $ResolvedManifest $manifest
    $sha256 = (Get-FileHash -LiteralPath $ResolvedManifest -Algorithm SHA256).Hash
    Write-Host ('BACKUP PASS manifest={0} sha256={1}' -f $ResolvedManifest, $sha256)
}

function Invoke-Restore([string]$ResolvedSession, [string]$ResolvedParent, [string]$ResolvedGame,
    [string]$ResolvedManifest, [string]$ResolvedReport) {
    if (-not (Test-Path -LiteralPath $ResolvedSession -PathType Container)) { throw 'Backup session is missing' }
    if ($ExpectedBackupManifestSha256 -cnotmatch $script:E2eShaPattern) {
        throw 'Restore requires the exact 64-hex backup manifest SHA-256 captured at backup time'
    }
    if (-not (Test-Path -LiteralPath $ResolvedManifest -PathType Leaf)) { throw 'Backup manifest is missing' }
    $manifestHash = (Get-FileHash -LiteralPath $ResolvedManifest -Algorithm SHA256).Hash
    if ($manifestHash -cne $ExpectedBackupManifestSha256.ToUpperInvariant()) {
        throw 'Backup manifest SHA-256 does not match the independently captured value'
    }
    try { $manifest = [IO.File]::ReadAllText($ResolvedManifest, $script:E2eStrictUtf8) | ConvertFrom-Json }
    catch { throw 'Backup manifest is not strict UTF-8 JSON' }
    if ([int](Get-Property $manifest 'schemaVersion' 'backup manifest') -ne 2 -or
        (Get-String $manifest 'kind' 'backup manifest') -cne $script:E2eBackupManifestKind -or
        (Get-String $manifest 'status' 'backup manifest') -cne 'PASS') {
        throw 'Backup manifest has an unsupported schema or status'
    }
    if ((Get-E2eFullPath (Get-String $manifest 'sessionRoot' 'backup manifest') 'manifest session') -cne $ResolvedSession -or
        (Get-E2eFullPath (Get-String $manifest 'gameRoot' 'backup manifest') 'manifest game root') -cne $ResolvedGame) {
        throw 'Backup manifest is bound to a different session or game root'
    }
    $backupDataRoot = Get-E2eFullPath (Get-String $manifest 'backupDataRoot' 'backup manifest') 'backup data root'
    if ($backupDataRoot -cne (Join-Path $ResolvedSession 'backup-data') -or
        -not (Test-E2ePathWithin $backupDataRoot $ResolvedSession)) {
        throw 'Backup data root is outside the bound session'
    }
    $backupCopiesExist = Test-Path -LiteralPath $backupDataRoot -PathType Container
    if (-not $backupCopiesExist -and (Test-Path -LiteralPath $backupDataRoot)) {
        throw 'Backup data root exists but is not a directory'
    }

    $manifestRows = @((Get-Property $manifest 'roots' 'backup manifest'))
    if ($manifestRows.Count -ne 2) { throw 'Backup manifest must have exactly two roots' }
    $validated = New-Object 'System.Collections.Generic.List[object]'
    $definitions = @{}
    foreach ($definition in @(Get-BackupRoots $ResolvedGame)) { $definitions[$definition.label] = $definition }
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($row in $manifestRows) {
        $label = Get-String $row 'label' 'backup root'
        if (-not $definitions.ContainsKey($label) -or -not $seen.Add($label)) {
            throw 'Backup manifest has a duplicate or unknown root'
        }
        $definition = $definitions[$label]
        $sourceRoot = Get-E2eFullPath (Get-String $row 'sourceRoot' 'backup root') 'source root'
        $expectedSource = Get-E2eFullPath $definition.sourceRoot 'expected source root'
        if ($sourceRoot -cne $expectedSource) { throw 'Backup manifest source root is not authoritative' }
        $backupRoot = Get-E2eFullPath (Get-String $row 'backupRoot' 'backup root') 'backup root'
        $expectedBackup = Get-E2eFullPath (Join-Path $backupDataRoot $definition.backupLeaf) 'expected backup root'
        if ($backupRoot -cne $expectedBackup -or -not (Test-E2ePathWithin $backupRoot $backupDataRoot)) {
            throw 'Backup manifest backup root is not authoritative'
        }
        $expected = Get-E2eManifestSnapshot $row ('backup root ' + $label)
        if ($backupCopiesExist) {
            $actualBackup = Get-E2eTreeSnapshot $backupRoot
            Assert-E2eSnapshotsEqual $actualBackup $expected ($label + ' backup pre-restore')
        }
        $validated.Add([pscustomobject][ordered]@{
            label = $label; sourceRoot = $sourceRoot; backupRoot = $backupRoot
            expected = $expected; backupLeaf = $definition.backupLeaf
        })
    }
    if ($seen.Count -ne 2) { throw 'Backup manifest is incomplete' }

    # A crash after backup cleanup but before final report publication is safely
    # resumable from the immutable manifest plus the restored source trees.
    if (-not $backupCopiesExist) {
        $preservedExportsRoot = Join-Path $ResolvedSession 'chat-exports'
        if (-not (Test-Path -LiteralPath $preservedExportsRoot -PathType Container)) {
            throw 'Backup copies are absent but preserved chat-export evidence is missing'
        }
        $preservedSourceCount = @(Get-ChildItem -LiteralPath $preservedExportsRoot -Directory -Force).Count
        $report = New-RestoreVerification $manifestHash $backupDataRoot $preservedExportsRoot `
            $preservedSourceCount $validated.ToArray() $ResolvedGame $ResolvedSession
        Write-E2eJsonAtomic $ResolvedReport $report
        $reportHash = (Get-FileHash -LiteralPath $ResolvedReport -Algorithm SHA256).Hash
        Write-Host ('RESTORE FINALIZE PASS report={0} sha256={1} backupCopiesDeleted=true' -f $ResolvedReport, $reportHash)
        return
    }

    $sourcesAlreadyRestored = $true
    foreach ($entry in $validated.ToArray()) {
        try { Assert-E2eSnapshotsEqual (Get-E2eTreeSnapshot $entry.sourceRoot) $entry.expected 'restore recovery source' }
        catch { $sourcesAlreadyRestored = $false; break }
    }
    $publishedExportsRoot = Join-Path $ResolvedSession 'chat-exports'
    if ($sourcesAlreadyRestored -and (Test-Path -LiteralPath $publishedExportsRoot -PathType Container)) {
        $publishedSourceCount = @(Get-ChildItem -LiteralPath $publishedExportsRoot -Directory -Force).Count
        $publishedSnapshot = Get-E2eTreeSnapshot $publishedExportsRoot
        if ($publishedSourceCount -lt 1 -or $publishedSnapshot.fileCount -lt 1) {
            throw 'Restore recovery found incomplete preserved chat-export evidence'
        }
        Remove-KnownRestoreArtifacts $ResolvedGame
        Remove-E2eContainedTree $backupDataRoot $ResolvedSession 'backup-data'
        $report = New-RestoreVerification $manifestHash $backupDataRoot $publishedExportsRoot `
            $publishedSourceCount $validated.ToArray() $ResolvedGame $ResolvedSession
        Write-E2eJsonAtomic $ResolvedReport $report
        $reportHash = (Get-FileHash -LiteralPath $ResolvedReport -Algorithm SHA256).Hash
        Write-Host ('RESTORE RECOVERY PASS report={0} sha256={1} backupCopiesDeleted=true' -f $ResolvedReport, $reportHash)
        return
    }

    # The post-restore scanner can independently decrypt the restored settings.
    # Prove that the test run did not rotate to a different credential which could
    # leak into evidence and then disappear with the test tree.
    $modLogsForCredentialCheck = @($validated.ToArray() | Where-Object { $_.label -eq 'ModLogs' })[0]
    $testSecrets = Get-E2eConfiguredSecrets (Join-Path $modLogsForCredentialCheck.sourceRoot 'Settings')
    $backupSecrets = Get-E2eConfiguredSecrets (Join-Path $modLogsForCredentialCheck.backupRoot 'Settings')
    $testSecretSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($value in @($testSecrets.values)) { [void]$testSecretSet.Add([string]$value) }
    $backupSecretSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($value in @($backupSecrets.values)) { [void]$backupSecretSet.Add([string]$value) }
    if ($testSecretSet.Count -ne $backupSecretSet.Count) {
        throw 'The E2E run changed configured credentials; post-restore exact scanning would be incomplete'
    }
    foreach ($value in $testSecretSet) {
        if (-not $backupSecretSet.Contains($value)) {
            throw 'The E2E run changed configured credentials; post-restore exact scanning would be incomplete'
        }
    }
    $value = $null; $testSecrets = $null; $backupSecrets = $null
    $testSecretSet.Clear(); $backupSecretSet.Clear()

    # Preserve the exact chat exports produced by the test run before the ModLogs
    # tree is replaced by the user's original backup. The final secret gate scans
    # these immutable copies after restore, so a leaking test export cannot vanish
    # merely because E2E-137 correctly removed the test data tree.
    $preservedExportsRoot = Join-Path $ResolvedSession 'chat-exports'
    $modLogsEntry = @($validated.ToArray() | Where-Object { $_.label -eq 'ModLogs' })[0]
    $exportSources = @()
    if (Test-Path -LiteralPath $modLogsEntry.sourceRoot -PathType Container) {
        [void](Get-E2eTreeSnapshot $modLogsEntry.sourceRoot)
        $exportSources = @(Get-ChildItem -LiteralPath $modLogsEntry.sourceRoot -Directory -Recurse -Force |
            Where-Object { $_.Name -ceq 'Exports' } | Sort-Object FullName)
    }
    if ($exportSources.Count -lt 1) { throw 'The E2E test tree contains no chat-export directory to preserve' }
    if (Test-Path -LiteralPath $preservedExportsRoot) {
        if (-not (Test-Path -LiteralPath $preservedExportsRoot -PathType Container)) {
            throw 'Preserved chat-export evidence path is not a directory'
        }
        $publishedRoots = @(Get-ChildItem -LiteralPath $preservedExportsRoot -Directory -Force | Sort-Object Name)
        if ($publishedRoots.Count -ne $exportSources.Count) { throw 'Published chat-export evidence is incomplete' }
        for ($index = 0; $index -lt $exportSources.Count; $index++) {
            $expectedLeaf = 'root-{0:d2}' -f ($index + 1)
            if ($publishedRoots[$index].Name -cne $expectedLeaf) { throw 'Published chat-export evidence layout is invalid' }
            Assert-E2eSnapshotsEqual (Get-E2eTreeSnapshot $publishedRoots[$index].FullName) `
                (Get-E2eTreeSnapshot $exportSources[$index].FullName) 'published chat-export evidence'
        }
    }
    else {
        $preserveLeaf = '.chat-exports.jyl-copy-' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $preserveStage = Join-Path $ResolvedSession $preserveLeaf
        try {
            [IO.Directory]::CreateDirectory($preserveStage) | Out-Null
            for ($index = 0; $index -lt $exportSources.Count; $index++) {
                $snapshot = Get-E2eTreeSnapshot $exportSources[$index].FullName
                $destination = Join-Path $preserveStage ('root-{0:d2}' -f ($index + 1))
                Copy-SnapshotToRoot $snapshot $exportSources[$index].FullName $destination
                Assert-E2eSnapshotsEqual (Get-E2eTreeSnapshot $destination) $snapshot 'preserved chat-export evidence'
            }
            [IO.Directory]::Move($preserveStage, $preservedExportsRoot)
        }
        finally {
            if (Test-Path -LiteralPath $preserveStage) {
                Remove-E2eContainedTree $preserveStage $ResolvedSession $preserveLeaf
            }
        }
    }
    [void](Get-E2eTreeSnapshot $preservedExportsRoot)

    $stages = New-Object 'System.Collections.Generic.List[object]'
    $transactionCommitted = $false
    try {
        foreach ($entry in $validated.ToArray()) {
            $stageLeaf = '.' + $entry.backupLeaf + '.jyl-restore-' + [Guid]::NewGuid().ToString('N')
            $stageRoot = Join-Path $ResolvedGame $stageLeaf
            $state = [pscustomobject][ordered]@{
                entry = $entry; stageRoot = $stageRoot; stageLeaf = $stageLeaf
                quarantineRoot = $null; quarantineLeaf = $null; swapped = $false
            }
            $stages.Add($state)
            Copy-SnapshotToRoot $entry.expected $entry.backupRoot $stageRoot
            $actualStage = Get-E2eTreeSnapshot $stageRoot
            Assert-E2eSnapshotsEqual $actualStage $entry.expected ($entry.label + ' restore stage')
        }

        foreach ($state in $stages.ToArray()) {
            $sourceRoot = $state.entry.sourceRoot
            if (Test-Path -LiteralPath $sourceRoot) { [void](Get-E2eTreeSnapshot $sourceRoot) }
            $quarantineLeaf = '.' + $state.entry.backupLeaf + '.jyl-testdata-' + [Guid]::NewGuid().ToString('N')
            $quarantineRoot = Join-Path $ResolvedGame $quarantineLeaf
            $state.quarantineRoot = $quarantineRoot
            $state.quarantineLeaf = $quarantineLeaf
            if (Test-Path -LiteralPath $sourceRoot) { [IO.Directory]::Move($sourceRoot, $quarantineRoot) }
            [IO.Directory]::Move($state.stageRoot, $sourceRoot)
            $restored = Get-E2eTreeSnapshot $sourceRoot
            Assert-E2eSnapshotsEqual $restored $state.entry.expected ($state.entry.label + ' restored source')
            $state.swapped = $true
        }
        $transactionCommitted = $true
    }
    catch {
        $rollbackSucceeded = $true
        for ($index = $stages.Count - 1; $index -ge 0; $index--) {
            $state = $stages[$index]
            $sourceRoot = $state.entry.sourceRoot
            if (-not [string]::IsNullOrWhiteSpace([string]$state.quarantineRoot) -and
                (Test-Path -LiteralPath $state.quarantineRoot)) {
                try {
                    if (Test-Path -LiteralPath $sourceRoot) {
                        if (Test-Path -LiteralPath $state.stageRoot) { throw 'Rollback stage path is occupied' }
                        [IO.Directory]::Move($sourceRoot, $state.stageRoot)
                    }
                    [IO.Directory]::Move($state.quarantineRoot, $sourceRoot)
                    $state.swapped = $false
                }
                catch { $rollbackSucceeded = $false }
            }
        }
        if (-not $rollbackSucceeded) { throw 'Restore transaction failed and automatic rollback was incomplete' }
        throw 'Restore transaction failed and was rolled back'
    }
    finally {
        if (-not $transactionCommitted) {
            foreach ($state in $stages.ToArray()) {
                if ((Test-Path -LiteralPath $state.stageRoot) -and
                    (Test-Path -LiteralPath $state.entry.sourceRoot) -and
                    ([string]::IsNullOrWhiteSpace([string]$state.quarantineRoot) -or
                        -not (Test-Path -LiteralPath $state.quarantineRoot))) {
                    Remove-E2eContainedTree $state.stageRoot $ResolvedGame $state.stageLeaf
                }
            }
        }
    }

    foreach ($state in $stages.ToArray()) {
        if (Test-Path -LiteralPath $state.quarantineRoot) {
            Remove-E2eContainedTree $state.quarantineRoot $ResolvedGame $state.quarantineLeaf
        }
    }
    Remove-E2eContainedTree $backupDataRoot $ResolvedSession 'backup-data'
    if (Test-Path -LiteralPath $backupDataRoot) { throw 'Backup copy cleanup did not complete' }

    $report = New-RestoreVerification $manifestHash $backupDataRoot $preservedExportsRoot `
        $exportSources.Count $validated.ToArray() $ResolvedGame $ResolvedSession
    Write-E2eJsonAtomic $ResolvedReport $report
    $reportHash = (Get-FileHash -LiteralPath $ResolvedReport -Algorithm SHA256).Hash
    Write-Host ('RESTORE PASS report={0} sha256={1} backupCopiesDeleted=true' -f $ResolvedReport, $reportHash)
}

try {
    Assert-E2eGameProcessesClosed
    $resolvedParent = Get-E2eFullPath $AllowedBackupParent 'allowed backup parent'
    if (-not (Test-Path -LiteralPath $resolvedParent -PathType Container)) { throw 'Allowed backup parent is missing' }
    Assert-E2ePathChainHasNoReparsePoint $resolvedParent 'allowed backup parent'
    $resolvedSession = Get-E2eFullPath $SessionRoot 'session root'
    if (-not (Test-E2ePathWithin $resolvedSession $resolvedParent)) { throw 'Session root escapes its explicit backup parent' }
    $resolvedGame = Get-E2eFullPath $GameRoot 'game root'
    if (-not (Test-Path -LiteralPath $resolvedGame -PathType Container)) { throw 'Game root is missing' }
    Assert-E2ePathChainHasNoReparsePoint $resolvedGame 'game root'
    if ((Test-E2ePathWithin $resolvedSession $resolvedGame -AllowEqual) -or
        (Test-E2ePathWithin $resolvedGame $resolvedSession -AllowEqual)) {
        throw 'Backup session and game trees must not overlap'
    }
    if ([string]::IsNullOrWhiteSpace($BackupManifestPath)) {
        $BackupManifestPath = Join-Path $resolvedSession 'backup-manifest.json'
    }
    $resolvedManifest = [IO.Path]::GetFullPath($BackupManifestPath)
    if (-not (Test-E2ePathWithin $resolvedManifest $resolvedSession)) { throw 'Backup manifest escapes its session root' }

    if ($Mode -eq 'Backup') {
        Invoke-Backup $resolvedSession $resolvedParent $resolvedGame $resolvedManifest
    }
    else {
        if ([string]::IsNullOrWhiteSpace($RestoreReportPath)) {
            $RestoreReportPath = Join-Path $resolvedSession 'restore-verification.json'
        }
        $resolvedReport = [IO.Path]::GetFullPath($RestoreReportPath)
        if (-not (Test-E2ePathWithin $resolvedReport $resolvedSession)) { throw 'Restore report escapes its session root' }
        Invoke-Restore $resolvedSession $resolvedParent $resolvedGame $resolvedManifest $resolvedReport
    }
}
catch {
    $typeName = if ($null -ne $_.Exception) { $_.Exception.GetType().Name } else { 'UnknownError' }
    [Console]::Error.WriteLine(('REAL-GAME DATA PROTECTION FAILED ({0}); no file contents were printed' -f $typeName))
    exit 1
}
