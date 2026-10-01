param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$ChecklistPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'docs\acceptance-checklist.md')
)

# Self-test fixtures are created only under .tmpbuild and are deleted only after
# an absolute-path containment check. No real save or game log path is touched.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$utf8NoBom = New-Object Text.UTF8Encoding($false)

$verifier = Join-Path $PSScriptRoot 'verify-real-game-e2e-evidence.ps1'
. $verifier -LibraryMode

function Write-TestText([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, $utf8NoBom)
}

function Write-TestJson([string]$Path, [object]$Value) {
    Write-TestText $Path (($Value | ConvertTo-Json -Depth 30) + "`n")
}

function Write-TestProtectedConfig([string]$Path, [string]$Secret) {
    $entropy = [Text.Encoding]::UTF8.GetBytes('JianghuYouling.LocalSecret.v1')
    $clear = [Text.Encoding]::UTF8.GetBytes($Secret)
    $cipher = $null
    try {
        $cipher = [System.Security.Cryptography.ProtectedData]::Protect(
            $clear, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        Write-TestJson $Path ([pscustomobject][ordered]@{
            baseUrl = 'https://example.invalid/v1'
            model = 'fixture-model'
            apiKeyProtected = [Convert]::ToBase64String($cipher)
        })
    }
    finally {
        [Array]::Clear($clear, 0, $clear.Length)
        if ($null -ne $cipher) { [Array]::Clear($cipher, 0, $cipher.Length) }
    }
}

function ConvertFrom-TestJson([string]$Json) {
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) {
        return (ConvertFrom-Json -InputObject $Json -DateKind String)
    }
    return (ConvertFrom-Json -InputObject $Json)
}

function Copy-TestObject([object]$Value) {
    return (ConvertFrom-TestJson ($Value | ConvertTo-Json -Depth 30))
}

function Invoke-TestPowerShell([string]$ScriptPath, [string[]]$Arguments, [int[]]$ExpectedExitCodes = @(0)) {
    $powerShell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $argumentList = New-Object 'System.Collections.Generic.List[string]'
    foreach ($fixed in @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $ScriptPath)) { $argumentList.Add($fixed) }
    foreach ($argument in $Arguments) { $argumentList.Add($argument) }
    $invokeArguments = $argumentList.ToArray()
    & $powerShell @invokeArguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -notin $ExpectedExitCodes) {
        throw "Fixture helper exited with unexpected code $exitCode"
    }
    return $exitCode
}

function Assert-FixtureFails([string]$Name, [scriptblock]$Action, [string]$ExpectedPattern) {
    $failed = $false
    try { [void](& $Action) }
    catch {
        $failed = $true
        if ($_.Exception.Message -notmatch $ExpectedPattern) {
            throw "Fixture '$Name' failed for the wrong reason: $($_.Exception.Message)"
        }
    }
    if (-not $failed) { throw "Fixture '$Name' unexpectedly passed" }
    Write-Host "fixture-pass=$Name (rejected as expected)"
}

function Remove-ContainedSelfTestTree([string]$Path, [string]$AllowedParent) {
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $parent = [IO.Path]::GetFullPath($AllowedParent).TrimEnd('\', '/')
    $prefix = $parent + [IO.Path]::DirectorySeparatorChar
    if ($full -eq $parent -or -not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing self-test cleanup outside explicit .tmpbuild parent: $full"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}

$tmpParent = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.tmpbuild'))
$tmpRoot = [IO.Path]::GetFullPath((Join-Path $tmpParent ('e2e-selftest-' + $PID + '-' + [DateTime]::UtcNow.Ticks)))
[IO.Directory]::CreateDirectory($tmpRoot) | Out-Null

try {
    $ids = @(Read-E2eChecklistIds $ChecklistPath)
    if ($ids.Count -lt 100) { throw "Expected the full acceptance checklist, found only $($ids.Count) IDs" }

    $gitSha = '1111111111111111111111111111111111111111'
    $buildId = '24160523'
    $started = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $captured = [DateTimeOffset]::UtcNow.AddMinutes(-3)
    $finished = [DateTimeOffset]::UtcNow.AddMinutes(-1)

    $releaseArtifactManifest = Join-Path $tmpRoot 'release-artifacts.json'
    $playerLog = Join-Path $tmpRoot 'Player.log'
    Write-TestText $releaseArtifactManifest '{"release":"fixture","dlls":4}'
    Write-TestText $playerLog '[fixture] real-game log checkpoint stream'

    $sourceBase = Join-Path $tmpRoot 'source'
    $sessionParent = Join-Path $tmpRoot 'sessions'
    $sessionRoot = Join-Path $sessionParent 'fixture-backup-session'
    $saveSource = Join-Path $sourceBase 'SaveGames'
    $logsSource = Join-Path $sourceBase 'TaiWu-JianghuYouling-Logs'
    Write-TestText (Join-Path $saveSource 'slot-a\save.dat') 'fixture-save-state'
    Write-TestText (Join-Path $logsSource 'Settings\worldbook.txt') 'fixture-world-state'
    [IO.Directory]::CreateDirectory($sessionParent) | Out-Null
    $protectionScript = Join-Path $PSScriptRoot 'e2e\protect-real-game-data.ps1'
    $backupManifestPath = Join-Path $sessionRoot 'backup-manifest.json'
    [void](Invoke-TestPowerShell $protectionScript @(
        '-Mode', 'Backup', '-SessionRoot', $sessionRoot, '-AllowedBackupParent', $sessionParent,
        '-GameRoot', $sourceBase, '-BackupManifestPath', $backupManifestPath))
    $backupManifestSha = (Get-FileHash -LiteralPath $backupManifestPath -Algorithm SHA256).Hash

    Write-TestText (Join-Path $saveSource 'slot-a\save.dat') 'fixture-mutated-by-real-game-test'
    Write-TestText (Join-Path $saveSource 'slot-b\test-only.dat') 'temporary-test-save'
    Write-TestText (Join-Path $logsSource 'Settings\worldbook.txt') 'fixture-mutated-world-state'
    Write-TestText (Join-Path $logsSource 'Worlds\World_99\Exports\test-chat.md') 'fixture test-run export evidence'
    $rotatedConfigPath = Join-Path $logsSource 'Settings\llm.json'
    $rotatedFixtureSecret = 'fixture-rotated-' + [Guid]::NewGuid().ToString('N')
    Write-TestProtectedConfig $rotatedConfigPath $rotatedFixtureSecret
    $rotationRejectReport = Join-Path $sessionRoot 'rotation-must-not-pass.json'
    [void](Invoke-TestPowerShell $protectionScript @(
        '-Mode', 'Restore', '-SessionRoot', $sessionRoot, '-AllowedBackupParent', $sessionParent,
        '-GameRoot', $sourceBase, '-BackupManifestPath', $backupManifestPath,
        '-ExpectedBackupManifestSha256', $backupManifestSha,
        '-RestoreReportPath', $rotationRejectReport) @(1))
    if ((Test-Path -LiteralPath $rotationRejectReport) -or
        [IO.File]::ReadAllText((Join-Path $saveSource 'slot-a\save.dat')) -cne 'fixture-mutated-by-real-game-test') {
        throw 'Credential-rotation rejection modified data or wrote a false PASS report'
    }
    Remove-Item -LiteralPath $rotatedConfigPath -Force
    $rotatedFixtureSecret = $null
    Write-Host 'fixture-pass=credential-rotation-before-restore'
    $restoreReportPath = Join-Path $sessionRoot 'restore-verification.json'
    [void](Invoke-TestPowerShell $protectionScript @(
        '-Mode', 'Restore', '-SessionRoot', $sessionRoot, '-AllowedBackupParent', $sessionParent,
        '-GameRoot', $sourceBase, '-BackupManifestPath', $backupManifestPath,
        '-ExpectedBackupManifestSha256', $backupManifestSha, '-RestoreReportPath', $restoreReportPath))
    if ([IO.File]::ReadAllText((Join-Path $saveSource 'slot-a\save.dat')) -cne 'fixture-save-state' -or
        [IO.File]::ReadAllText((Join-Path $logsSource 'Settings\worldbook.txt')) -cne 'fixture-world-state' -or
        (Test-Path -LiteralPath (Join-Path $saveSource 'slot-b\test-only.dat')) -or
        (Test-Path -LiteralPath (Join-Path $sessionRoot 'backup-data'))) {
        throw 'Real backup/restore fixture did not restore and clean exactly'
    }
    Remove-Item -LiteralPath $restoreReportPath -Force
    [void](Invoke-TestPowerShell $protectionScript @(
        '-Mode', 'Restore', '-SessionRoot', $sessionRoot, '-AllowedBackupParent', $sessionParent,
        '-GameRoot', $sourceBase, '-BackupManifestPath', $backupManifestPath,
        '-ExpectedBackupManifestSha256', $backupManifestSha, '-RestoreReportPath', $restoreReportPath))
    if (-not (Test-Path -LiteralPath $restoreReportPath -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $sessionRoot 'backup-data'))) {
        throw 'Manifest-only post-cleanup report finalization did not recover'
    }
    Write-Host 'fixture-pass=manifest-only-post-cleanup-finalize'
    Remove-Item -LiteralPath $restoreReportPath -Force
    $backupDocumentForRecovery = ConvertFrom-TestJson ([IO.File]::ReadAllText($backupManifestPath, [Text.Encoding]::UTF8))
    foreach ($rootRow in @($backupDocumentForRecovery.roots)) {
        $expectedRecoverySnapshot = Get-E2eManifestSnapshot $rootRow 'recovery backup fixture'
        Copy-E2eSnapshotToRoot $expectedRecoverySnapshot ([string]$rootRow.sourceRoot) ([string]$rootRow.backupRoot)
    }
    $simulatedQuarantineLeaf = '.SaveGames.jyl-testdata-' + [Guid]::NewGuid().ToString('N')
    Write-TestText (Join-Path (Join-Path $sourceBase $simulatedQuarantineLeaf) 'test-only.dat') 'simulated interrupted cleanup'
    [void](Invoke-TestPowerShell $protectionScript @(
        '-Mode', 'Restore', '-SessionRoot', $sessionRoot, '-AllowedBackupParent', $sessionParent,
        '-GameRoot', $sourceBase, '-BackupManifestPath', $backupManifestPath,
        '-ExpectedBackupManifestSha256', $backupManifestSha, '-RestoreReportPath', $restoreReportPath))
    if (-not (Test-Path -LiteralPath $restoreReportPath -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $sessionRoot 'backup-data')) -or
        (Test-Path -LiteralPath (Join-Path $sourceBase $simulatedQuarantineLeaf))) {
        throw 'Interrupted-cleanup recovery did not converge to the terminal state'
    }
    Write-Host 'fixture-pass=interrupted-cleanup-recovery'
    [void](Invoke-TestPowerShell $protectionScript @(
        '-Mode', 'Backup', '-SessionRoot', (Join-Path $tmpRoot 'outside-session'),
        '-AllowedBackupParent', $sessionParent, '-GameRoot', $sourceBase) @(1))
    if (Test-Path -LiteralPath (Join-Path $tmpRoot 'outside-session')) {
        throw 'Out-of-containment backup fixture modified its rejected target'
    }
    Write-Host 'fixture-pass=backup-session-containment'
    [void](Invoke-TestPowerShell $protectionScript @(
        '-Mode', 'Restore', '-SessionRoot', $sessionRoot, '-AllowedBackupParent', $sessionParent,
        '-GameRoot', $sourceBase, '-BackupManifestPath', $backupManifestPath,
        '-ExpectedBackupManifestSha256', ('0' * 64),
        '-RestoreReportPath', (Join-Path $sessionRoot 'must-not-exist.json')) @(1))
    if (Test-Path -LiteralPath (Join-Path $sessionRoot 'must-not-exist.json')) {
        throw 'Wrong-manifest-hash restore fixture wrote a report'
    }
    Write-Host 'fixture-pass=restore-independent-manifest-hash'

    # The protection fixture intentionally exercises multiple fresh PowerShell/DPAPI
    # processes and can take much longer on a busy or resumed desktop.  Bind the
    # synthetic run's finish boundary after those operations so valid restore evidence
    # generated during the fixture cannot age out of its own ten-minute window.
    $finished = [DateTimeOffset]::UtcNow

    $secretReport = [pscustomobject][ordered]@{
        schemaVersion = 2
        kind = 'jianghu-youling-secret-scan-report'
        status = 'PASS'
        findings = 0
        gitSha = $gitSha
        scannedUtc = $finished.ToString('o')
        scanPolicy = 'generic-pattern-and-exact-dpapi-configured-values-v3'
        settingsRoot = (Join-Path $logsSource 'Settings')
        exactSecretCount = 0
        protectedConfigReplicaCount = 0
        credentialSources = @(
            [pscustomobject][ordered]@{ configFile='llm.json'; replicaCount=0; decryptedCredentialValues=0 },
            [pscustomobject][ordered]@{ configFile='tts.json'; replicaCount=0; decryptedCredentialValues=0 },
            [pscustomobject][ordered]@{ configFile='minimax.json'; replicaCount=0; decryptedCredentialValues=0 }
        )
        scopes = @(
            [pscustomobject][ordered]@{ name='repository'; status='PASS'; sourceKind='file-set'; roots=@($tmpRoot); fileCount=0; totalBytes=0; snapshotSha256=(Get-E2eSha256ForText ''); findings=0; files=@() },
            [pscustomobject][ordered]@{ name='git-history'; status='PASS'; sourceKind='git-object-database'; roots=@($tmpRoot); fileCount=0; totalBytes=0; snapshotSha256=(Get-E2eSha256ForText ''); findings=0; gitObjectCount=0; blobs=@() },
            [pscustomobject][ordered]@{ name='command-output'; status='PASS'; sourceKind='file-set'; roots=@((Join-Path $tmpRoot 'command-output')); fileCount=0; totalBytes=0; snapshotSha256=(Get-E2eSha256ForText ''); findings=0; files=@() },
            [pscustomobject][ordered]@{ name='player-log'; status='PASS'; sourceKind='file-set'; roots=@($playerLog); fileCount=0; totalBytes=0; snapshotSha256=(Get-E2eSha256ForText ''); findings=0; files=@() },
            [pscustomobject][ordered]@{ name='llm-metrics'; status='PASS'; sourceKind='file-set'; roots=@($tmpRoot); fileCount=0; totalBytes=0; snapshotSha256=(Get-E2eSha256ForText ''); findings=0; files=@() },
            [pscustomobject][ordered]@{ name='chat-exports'; status='PASS'; sourceKind='file-set'; roots=@($tmpRoot); fileCount=0; totalBytes=0; snapshotSha256=(Get-E2eSha256ForText ''); findings=0; files=@() }
        )
    }
    $secretReportPath = Join-Path $tmpRoot 'secret-scan.json'
    Write-TestJson $secretReportPath $secretReport

    $caseRows = New-Object 'System.Collections.Generic.List[object]'
    foreach ($id in $ids) {
        $artifactPath = Join-Path $tmpRoot ('evidence\' + $id + '.txt')
        Write-TestText $artifactPath ("Concrete isolated runtime fixture for {0}; checkpoint state is unique.`nlocator=checkpoint-{0}-line-1`n" -f $id)
        $caseRows.Add([pscustomobject][ordered]@{
            id = $id
            status = 'PASS'
            levels = @('G')
            verifiedUtc = $finished.ToString('o')
            evidence = @([pscustomobject][ordered]@{
                kind = 'game-state'
                artifactPath = $artifactPath
                sha256 = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
                locator = "checkpoint-$id-line-1"
                observation = "Observed the exact isolated runtime state and expected result for checklist item $id."
                capturedUtc = $captured.ToString('o')
            })
        })
    }

    $appManifestPath = Join-Path $tmpRoot 'appmanifest_838350.acf'
    Write-TestText $appManifestPath '"buildid" "24160523"'
    $authority = [pscustomobject][ordered]@{
        repositoryRoot = $tmpRoot
        gameRoot = $sourceBase
        gitSha = $gitSha
        buildId = $buildId
        appManifestPath = $appManifestPath
        appManifestSha256 = (Get-FileHash -LiteralPath $appManifestPath -Algorithm SHA256).Hash
        decompileRoot = (Join-Path $tmpRoot '.decompiled\taiwu_decomp_b24160523')
        completeMarkerSha256 = '2222222222222222222222222222222222222222222222222222222222222222'
        sourceAssembliesManifestSha256 = '3333333333333333333333333333333333333333333333333333333333333333'
        assemblyCount = 33
        csFileCount = 11424
    }

    $baseManifest = [pscustomobject][ordered]@{
        schemaVersion = 1
        kind = 'jianghu-youling-real-game-full-mod-e2e-evidence'
        runId = 'fixture-complete-manifest'
        startedUtc = $started.ToString('o')
        finishedUtc = $finished.ToString('o')
        source = [pscustomobject][ordered]@{
            gitSha = $gitSha; workingTreeClean = $true; buildConfiguration = 'Release'
            releaseArtifactManifestPath = $releaseArtifactManifest
            releaseArtifactManifestSha256 = (Get-FileHash -LiteralPath $releaseArtifactManifest -Algorithm SHA256).Hash
        }
        game = [pscustomobject][ordered]@{
            buildId = $buildId; appManifestPath = $appManifestPath
            appManifestSha256 = (Get-FileHash -LiteralPath $appManifestPath -Algorithm SHA256).Hash
            playerLogPath = $playerLog; playerLogSha256 = (Get-FileHash -LiteralPath $playerLog -Algorithm SHA256).Hash
        }
        decompile = [pscustomobject][ordered]@{
            rootPath = $authority.decompileRoot
            completeMarkerSha256 = $authority.completeMarkerSha256
            sourceAssembliesManifestSha256 = $authority.sourceAssembliesManifestSha256
            assemblyCount = $authority.assemblyCount; csFileCount = $authority.csFileCount
        }
        secretScan = [pscustomobject][ordered]@{
            status = 'PASS'; reportPath = $secretReportPath
            reportSha256 = (Get-FileHash -LiteralPath $secretReportPath -Algorithm SHA256).Hash
        }
        backup = [pscustomobject][ordered]@{
            status = 'PASS'; manifestPath = $backupManifestPath; manifestSha256 = $backupManifestSha
        }
        restore = [pscustomobject][ordered]@{
            status = 'PASS'; verificationPath = $restoreReportPath
            verificationSha256 = (Get-FileHash -LiteralPath $restoreReportPath -Algorithm SHA256).Hash
        }
        cases = $caseRows.ToArray()
    }

    $completePath = Join-Path $tmpRoot 'fixture-complete.json'
    Write-TestJson $completePath $baseManifest
    $completeResult = Test-E2eEvidenceManifest $completePath $ChecklistPath $authority 72 -SkipRepositorySecretScan
    if ($completeResult.caseCount -ne $ids.Count -or $completeResult.uniqueEvidenceSlices -ne $ids.Count) {
        throw 'Complete-template fixture returned the wrong counts'
    }
    Write-Host "fixture-pass=complete-template count=$($completeResult.caseCount)"

    $backupDocument = ConvertFrom-TestJson ([IO.File]::ReadAllText($backupManifestPath, [Text.Encoding]::UTF8))
    $resurrectedBackupData = [string]$backupDocument.backupDataRoot
    Write-TestText (Join-Path $resurrectedBackupData 'SaveGames\unexpected-copy.dat') 'must-be-rejected'
    Assert-FixtureFails 'backup-copy-not-deleted' { Test-E2eEvidenceManifest $completePath $ChecklistPath $authority 72 -SkipRepositorySecretScan } 'backup copies still exist'
    Remove-E2eContainedTree $resurrectedBackupData $sessionRoot 'backup-data'

    Write-TestText (Join-Path $saveSource 'slot-a\save.dat') 'tampered-after-restore'
    Assert-FixtureFails 'restored-source-tampered' { Test-E2eEvidenceManifest $completePath $ChecklistPath $authority 72 -SkipRepositorySecretScan } 'mismatch'
    Write-TestText (Join-Path $saveSource 'slot-a\save.dat') 'fixture-save-state'

    $unsafeBackup = Copy-TestObject $backupDocument
    $unsafeBackup.roots[0].files[0].relativePath = '../escape.dat'
    $unsafeBackupPath = Join-Path $tmpRoot 'fixture-unsafe-backup-manifest.json'
    Write-TestJson $unsafeBackupPath $unsafeBackup
    $unsafeManifest = Copy-TestObject $baseManifest
    $unsafeManifest.backup.manifestPath = $unsafeBackupPath
    $unsafeManifest.backup.manifestSha256 = (Get-FileHash -LiteralPath $unsafeBackupPath -Algorithm SHA256).Hash
    $unsafeManifestPath = Join-Path $tmpRoot 'fixture-unsafe-backup.json'
    Write-TestJson $unsafeManifestPath $unsafeManifest
    Assert-FixtureFails 'unsafe-backup-relative-path' { Test-E2eEvidenceManifest $unsafeManifestPath $ChecklistPath $authority 72 -SkipRepositorySecretScan } 'unsafe path segment'

    $weakSecretReport = Copy-TestObject $secretReport
    $weakSecretReport.scopes = @('repository', 'git-history', 'command-output', 'player-log', 'llm-metrics', 'chat-exports')
    $weakSecretPath = Join-Path $tmpRoot 'fixture-weak-secret-report.json'
    Write-TestJson $weakSecretPath $weakSecretReport
    $weakSecretManifest = Copy-TestObject $baseManifest
    $weakSecretManifest.secretScan.reportPath = $weakSecretPath
    $weakSecretManifest.secretScan.reportSha256 = (Get-FileHash -LiteralPath $weakSecretPath -Algorithm SHA256).Hash
    $weakSecretManifestPath = Join-Path $tmpRoot 'fixture-weak-secret.json'
    Write-TestJson $weakSecretManifestPath $weakSecretManifest
    Assert-FixtureFails 'declarative-secret-template' { Test-E2eEvidenceManifest $weakSecretManifestPath $ChecklistPath $authority 72 -SkipRepositorySecretScan } 'missing property'

    $falseCleanup = ConvertFrom-TestJson ([IO.File]::ReadAllText($restoreReportPath, [Text.Encoding]::UTF8))
    $falseCleanup.backupCopiesDeleted = $false
    $falseCleanupPath = Join-Path $tmpRoot 'fixture-false-cleanup-report.json'
    Write-TestJson $falseCleanupPath $falseCleanup
    $falseCleanupManifest = Copy-TestObject $baseManifest
    $falseCleanupManifest.restore.verificationPath = $falseCleanupPath
    $falseCleanupManifest.restore.verificationSha256 = (Get-FileHash -LiteralPath $falseCleanupPath -Algorithm SHA256).Hash
    $falseCleanupManifestPath = Join-Path $tmpRoot 'fixture-false-cleanup.json'
    Write-TestJson $falseCleanupManifestPath $falseCleanupManifest
    Assert-FixtureFails 'false-backup-cleanup-claim' { Test-E2eEvidenceManifest $falseCleanupManifestPath $ChecklistPath $authority 72 -SkipRepositorySecretScan } 'backupCopiesDeleted'

    $secretFixtureRoot = Join-Path $tmpRoot 'secret-generator-fixture'
    $secretRepository = Join-Path $secretFixtureRoot 'repository'
    $secretCommand = Join-Path $secretFixtureRoot 'command-output'
    $secretExports = Join-Path $secretFixtureRoot 'chat-exports\root-01'
    $secretSettings = Join-Path $secretFixtureRoot 'TaiWu-JianghuYouling-Logs\Settings'
    $secretPlayerLog = Join-Path $secretFixtureRoot 'Player.log'
    $secretPreviousPlayerLog = Join-Path $secretFixtureRoot 'Player-prev.log'
    $secretMetrics = Join-Path $secretFixtureRoot 'llm_metrics.jsonl'
    $secretRotatedMetrics = $secretMetrics + '.1'
    Write-TestText (Join-Path $secretRepository 'clean.txt') 'clean repository fixture'
    Write-TestText (Join-Path $secretCommand 'preflight.log') 'clean command fixture'
    Write-TestText (Join-Path $secretExports 'chat.md') 'clean export fixture'
    Write-TestText $secretPlayerLog 'clean Player.log fixture'
    Write-TestText $secretPreviousPlayerLog 'clean Player-prev.log fixture'
    Write-TestText $secretMetrics '{"status":"ok"}'
    Write-TestText $secretRotatedMetrics '{"status":"rotated-ok"}'
    $fixtureSecret = 'fixture-exact-' + [Guid]::NewGuid().ToString('N')
    Write-TestProtectedConfig (Join-Path $secretSettings 'llm.json') $fixtureSecret
    $git = (Get-Command git -ErrorAction Stop).Source
    & $git -C $secretRepository init -q
    & $git -C $secretRepository config user.email 'fixture@example.invalid'
    & $git -C $secretRepository config user.name 'E2E Fixture'
    & $git -C $secretRepository config commit.gpgsign false
    & $git -C $secretRepository config core.hooksPath .git/no-hooks
    & $git -C $secretRepository add clean.txt
    & $git -C $secretRepository commit -q -m fixture
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create isolated Git fixture for the secret scanner' }
    $secretGenerator = Join-Path $PSScriptRoot 'e2e\new-secret-scan-report.ps1'
    $generatedSecretReport = Join-Path $secretFixtureRoot 'secret-pass.json'
    [void](Invoke-TestPowerShell $secretGenerator @(
        '-OutputPath', $generatedSecretReport, '-RepositoryRoot', $secretRepository,
        '-CommandOutputRoot', $secretCommand, '-PlayerLogPath', $secretPlayerLog,
        '-LlmMetricsPath', $secretMetrics, '-ChatExportRoots', $secretExports,
        '-SettingsRoot', $secretSettings))
    $generatedReportObject = ConvertFrom-TestJson ([IO.File]::ReadAllText($generatedSecretReport, [Text.Encoding]::UTF8))
    if ($generatedReportObject.status -cne 'PASS' -or $generatedReportObject.scopes.Count -ne 6 -or
        [int]$generatedReportObject.exactSecretCount -ne 1 -or
        [IO.File]::ReadAllText($generatedSecretReport).Contains($fixtureSecret)) {
        throw 'Real six-scope secret generator did not produce a value-free PASS report'
    }
    $secretAuthority = [pscustomobject][ordered]@{
        repositoryRoot = $secretRepository
        gameRoot = $secretFixtureRoot
        llmMetricsPath = $secretMetrics
    }
    $secretGitSha = (& $git -C $secretRepository rev-parse HEAD).Trim()
    Test-SecretScanReport ([pscustomobject][ordered]@{
        status = 'PASS'
        reportPath = $generatedSecretReport
        reportSha256 = (Get-FileHash -LiteralPath $generatedSecretReport -Algorithm SHA256).Hash
    }) $secretFixtureRoot ([DateTimeOffset]::UtcNow.AddMinutes(-5)) ([DateTimeOffset]::UtcNow.AddMinutes(5)) `
        $secretGitSha $secretAuthority $secretPlayerLog $secretFixtureRoot
    Write-TestText (Join-Path $secretCommand 'leak.log') ('prefix-' + $fixtureSecret + '-suffix')
    $failedSecretReport = Join-Path $secretFixtureRoot 'secret-fail.json'
    [void](Invoke-TestPowerShell $secretGenerator @(
        '-OutputPath', $failedSecretReport, '-RepositoryRoot', $secretRepository,
        '-CommandOutputRoot', $secretCommand, '-PlayerLogPath', $secretPlayerLog,
        '-LlmMetricsPath', $secretMetrics, '-ChatExportRoots', $secretExports,
        '-SettingsRoot', $secretSettings) @(2))
    $failedReportObject = ConvertFrom-TestJson ([IO.File]::ReadAllText($failedSecretReport, [Text.Encoding]::UTF8))
    if ($failedReportObject.status -cne 'FAIL' -or [int]$failedReportObject.findings -lt 1 -or
        [IO.File]::ReadAllText($failedSecretReport).Contains($fixtureSecret)) {
        throw 'Exact configured-secret negative fixture was not rejected without disclosure'
    }
    Remove-Item -LiteralPath (Join-Path $secretCommand 'leak.log') -Force
    $genericFixtureSecret = 'fixture_generic_value_1234567890'
    Write-TestText (Join-Path $secretCommand 'generic-leak.json') ('{"apiKey":"' + $genericFixtureSecret + '"}')
    $genericFailedReport = Join-Path $secretFixtureRoot 'secret-generic-fail.json'
    [void](Invoke-TestPowerShell $secretGenerator @(
        '-OutputPath', $genericFailedReport, '-RepositoryRoot', $secretRepository,
        '-CommandOutputRoot', $secretCommand, '-PlayerLogPath', $secretPlayerLog,
        '-LlmMetricsPath', $secretMetrics, '-ChatExportRoots', $secretExports,
        '-SettingsRoot', $secretSettings) @(2))
    $genericFailedObject = ConvertFrom-TestJson ([IO.File]::ReadAllText($genericFailedReport, [Text.Encoding]::UTF8))
    if ($genericFailedObject.status -cne 'FAIL' -or [int]$genericFailedObject.findings -lt 1 -or
        [IO.File]::ReadAllText($genericFailedReport).Contains($genericFixtureSecret)) {
        throw 'Generic unknown-secret negative fixture was not rejected without disclosure'
    }
    Remove-Item -LiteralPath (Join-Path $secretCommand 'generic-leak.json') -Force
    Write-TestText $secretPreviousPlayerLog ('previous-prefix-' + $fixtureSecret + '-suffix')
    $previousLogFailedReport = Join-Path $secretFixtureRoot 'secret-previous-log-fail.json'
    [void](Invoke-TestPowerShell $secretGenerator @(
        '-OutputPath', $previousLogFailedReport, '-RepositoryRoot', $secretRepository,
        '-CommandOutputRoot', $secretCommand, '-PlayerLogPath', $secretPlayerLog,
        '-LlmMetricsPath', $secretMetrics, '-ChatExportRoots', $secretExports,
        '-SettingsRoot', $secretSettings) @(2))
    $previousLogFailedObject = ConvertFrom-TestJson ([IO.File]::ReadAllText($previousLogFailedReport, [Text.Encoding]::UTF8))
    if ($previousLogFailedObject.status -cne 'FAIL' -or [int]$previousLogFailedObject.findings -lt 1 -or
        [IO.File]::ReadAllText($previousLogFailedReport).Contains($fixtureSecret)) {
        throw 'Player-prev.log exact-secret negative fixture was not rejected without disclosure'
    }
    Write-TestText $secretPreviousPlayerLog 'clean Player-prev.log fixture'
    Write-TestText $secretRotatedMetrics ('rotated-prefix-' + $fixtureSecret + '-suffix')
    $rotatedMetricsFailedReport = Join-Path $secretFixtureRoot 'secret-rotated-metrics-fail.json'
    [void](Invoke-TestPowerShell $secretGenerator @(
        '-OutputPath', $rotatedMetricsFailedReport, '-RepositoryRoot', $secretRepository,
        '-CommandOutputRoot', $secretCommand, '-PlayerLogPath', $secretPlayerLog,
        '-LlmMetricsPath', $secretMetrics, '-ChatExportRoots', $secretExports,
        '-SettingsRoot', $secretSettings) @(2))
    $rotatedMetricsFailedObject = ConvertFrom-TestJson ([IO.File]::ReadAllText($rotatedMetricsFailedReport, [Text.Encoding]::UTF8))
    if ($rotatedMetricsFailedObject.status -cne 'FAIL' -or [int]$rotatedMetricsFailedObject.findings -lt 1 -or
        [IO.File]::ReadAllText($rotatedMetricsFailedReport).Contains($fixtureSecret)) {
        throw 'llm_metrics.jsonl.1 exact-secret negative fixture was not rejected without disclosure'
    }
    Write-TestText $secretRotatedMetrics '{"status":"rotated-ok"}'
    $genericFixtureSecret = $null
    $fixtureSecret = $null
    Write-Host 'fixture-pass=real-six-scope-secret-generator-with-rotated-logs'

    $missing = Copy-TestObject $baseManifest
    $missing.cases = @()
    $missingPath = Join-Path $tmpRoot 'fixture-all-missing.json'
    Write-TestJson $missingPath $missing
    Assert-FixtureFails 'all-missing' { Test-E2eEvidenceManifest $missingPath $ChecklistPath $authority 72 -SkipRepositorySecretScan } 'missing checklist IDs'

    $duplicate = Copy-TestObject $baseManifest
    $duplicate.cases = @($duplicate.cases) + @($duplicate.cases[0])
    $duplicatePath = Join-Path $tmpRoot 'fixture-duplicate.json'
    Write-TestJson $duplicatePath $duplicate
    Assert-FixtureFails 'duplicate' { Test-E2eEvidenceManifest $duplicatePath $ChecklistPath $authority 72 -SkipRepositorySecretScan } 'Duplicate case ID'

    $unknown = Copy-TestObject $baseManifest
    $unknownCase = Copy-TestObject $unknown.cases[0]
    $unknownCase.id = 'E2E-999'
    $unknown.cases = @($unknown.cases) + @($unknownCase)
    $unknownPath = Join-Path $tmpRoot 'fixture-unknown.json'
    Write-TestJson $unknownPath $unknown
    Assert-FixtureFails 'unknown' { Test-E2eEvidenceManifest $unknownPath $ChecklistPath $authority 72 -SkipRepositorySecretScan } 'unknown case ID'

    $fakeEvidence = Copy-TestObject $baseManifest
    $fakeEvidence.cases[0].evidence[0].locator = 'generic-pass'
    $fakeEvidence.cases[0].evidence[0].observation = 'PASS'
    $fakePath = Join-Path $tmpRoot 'fixture-fake-evidence.json'
    Write-TestJson $fakePath $fakeEvidence
    Assert-FixtureFails 'fake-evidence' { Test-E2eEvidenceManifest $fakePath $ChecklistPath $authority 72 -SkipRepositorySecretScan } 'too broad'

    $inventedLocator = Copy-TestObject $baseManifest
    $inventedLocator.cases[0].evidence[0].locator = 'specific-checkpoint-that-is-not-in-the-artifact'
    $inventedLocator.cases[0].evidence[0].observation = 'Observed a detailed but deliberately unsupported checkpoint description.'
    $inventedLocatorPath = Join-Path $tmpRoot 'fixture-invented-locator.json'
    Write-TestJson $inventedLocatorPath $inventedLocator
    Assert-FixtureFails 'invented-locator' { Test-E2eEvidenceManifest $inventedLocatorPath $ChecklistPath $authority 72 -SkipRepositorySecretScan } 'does not occur'

    Write-Host "REAL-GAME E2E EVIDENCE GATE SELF-TEST PASS: fixtures=19 checklistIds=$($ids.Count)"
}
finally {
    Remove-ContainedSelfTestTree $tmpRoot $tmpParent
}
