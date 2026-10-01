param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$ChecklistPath = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'docs\acceptance-checklist.md'),
    [string]$SteamAppsRoot = 'C:\Program Files (x86)\Steam\steamapps',
    [string]$OutputPath
)

# Creates a PENDING template only. It does not claim E2E success and it never
# reads, copies, restores, deletes, or modifies save data.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
$decompiledBase = if (-not [string]::IsNullOrWhiteSpace($env:JHYL_DECOMPILED_ROOT)) {
    [IO.Path]::GetFullPath($env:JHYL_DECOMPILED_ROOT).TrimEnd('\', '/')
} elseif (Test-Path -LiteralPath (Join-Path $root '.decompiled')) {
    [IO.Path]::GetFullPath((Join-Path $root '.decompiled')).TrimEnd('\', '/')
} else {
    [IO.Path]::GetFullPath((Join-Path $root '.decompiled')).TrimEnd('\', '/')
}
$resultsRoot = [IO.Path]::GetFullPath((Join-Path $root '.e2e-results')).TrimEnd('\', '/')
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $resultsRoot ('real-game-e2e-template-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '.json')
}
$outputFull = [IO.Path]::GetFullPath($OutputPath)
$allowedPrefix = $resultsRoot + [IO.Path]::DirectorySeparatorChar
if (-not $outputFull.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Template output must stay under the explicit workspace evidence directory: $resultsRoot"
}
if (Test-Path -LiteralPath $outputFull) { throw "Refusing to overwrite existing template: $outputFull" }

$verifier = Join-Path $PSScriptRoot 'verify-real-game-e2e-evidence.ps1'
. $verifier -LibraryMode
$ids = @(Read-E2eChecklistIds $ChecklistPath)

$git = (Get-Command git -ErrorAction Stop).Source
$gitSha = (& $git -C $root rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitSha)) { throw 'Cannot resolve repository Git HEAD' }
$dirty = @(& $git -C $root status --porcelain --untracked-files=all 2>$null)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect repository working tree state' }

$appManifest = [IO.Path]::GetFullPath((Join-Path $SteamAppsRoot 'appmanifest_838350.acf'))
if (-not (Test-Path -LiteralPath $appManifest -PathType Leaf)) { throw "Taiwu appmanifest missing: $appManifest" }
$buildLine = Select-String -LiteralPath $appManifest -Pattern '"buildid"\s+"([0-9]+)"' | Select-Object -First 1
if ($null -eq $buildLine) { throw 'Cannot parse Taiwu buildid' }
$buildId = [regex]::Match($buildLine.Line, '"buildid"\s+"([0-9]+)"').Groups[1].Value
$decompileRoot = [IO.Path]::GetFullPath((Join-Path $decompiledBase ('taiwu_decomp_b' + $buildId)))
$completeMarker = Join-Path $decompileRoot '_COMPLETE'
$sourceManifest = Join-Path $decompileRoot 'SOURCE_ASSEMBLIES.sha256'
if (-not (Test-Path -LiteralPath $completeMarker -PathType Leaf)) { throw "Decompile marker missing: $completeMarker" }
if (-not (Test-Path -LiteralPath $sourceManifest -PathType Leaf)) { throw "Decompile source manifest missing: $sourceManifest" }
$assemblyCount = @(Get-Content -LiteralPath $sourceManifest -Encoding UTF8 | Select-Object -Skip 1 | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
$csFileCount = @(Get-ChildItem -LiteralPath $decompileRoot -Filter '*.cs' -File -Recurse).Count
$now = [DateTimeOffset]::UtcNow.ToString('o')

$cases = @($ids | ForEach-Object {
    [pscustomobject][ordered]@{
        id = $_
        status = 'PENDING'
        levels = @()
        verifiedUtc = $null
        evidence = @()
    }
})

$template = [pscustomobject][ordered]@{
    schemaVersion = 1
    kind = 'jianghu-youling-real-game-full-mod-e2e-evidence'
    runId = 'REPLACE-WITH-REAL-GAME-RUN-ID'
    startedUtc = $now
    finishedUtc = $null
    source = [pscustomobject][ordered]@{
        gitSha = ([string]$gitSha).Trim()
        workingTreeClean = ($dirty.Count -eq 0)
        buildConfiguration = 'Release'
        releaseArtifactManifestPath = 'REPLACE-WITH-RELEASE-ARTIFACT-MANIFEST-PATH'
        releaseArtifactManifestSha256 = 'REPLACE-WITH-SHA256'
    }
    game = [pscustomobject][ordered]@{
        buildId = $buildId
        appManifestPath = $appManifest
        appManifestSha256 = (Get-FileHash -LiteralPath $appManifest -Algorithm SHA256).Hash
        playerLogPath = 'REPLACE-WITH-LATEST-REAL-GAME-PLAYER-LOG-PATH'
        playerLogSha256 = 'REPLACE-WITH-SHA256'
    }
    decompile = [pscustomobject][ordered]@{
        rootPath = $decompileRoot
        completeMarkerSha256 = (Get-FileHash -LiteralPath $completeMarker -Algorithm SHA256).Hash
        sourceAssembliesManifestSha256 = (Get-FileHash -LiteralPath $sourceManifest -Algorithm SHA256).Hash
        assemblyCount = $assemblyCount
        csFileCount = $csFileCount
    }
    secretScan = [pscustomobject][ordered]@{
        status = 'PENDING'
        reportPath = 'REPLACE-WITH-SECRET-SCAN-REPORT-PATH'
        reportSha256 = 'REPLACE-WITH-SHA256'
    }
    backup = [pscustomobject][ordered]@{
        status = 'PENDING'
        manifestPath = 'REPLACE-WITH-BACKUP-MANIFEST-PATH'
        manifestSha256 = 'REPLACE-WITH-SHA256'
    }
    restore = [pscustomobject][ordered]@{
        status = 'PENDING'
        verificationPath = 'REPLACE-WITH-RESTORE-VERIFICATION-PATH'
        verificationSha256 = 'REPLACE-WITH-SHA256'
    }
    cases = $cases
}

Write-E2eJsonAtomic $outputFull $template
Write-Host "PENDING template created (not E2E PASS): $outputFull"
Write-Host "Checklist IDs: $($ids.Count)"
