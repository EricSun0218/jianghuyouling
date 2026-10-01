param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'jyl-workshop-validator-' + [Guid]::NewGuid().ToString('N'))
$content = Join-Path $testRoot 'content'
$manifest = Join-Path $testRoot 'hashes.json'
$validator = Join-Path $Root 'tools\validate-workshop-package.ps1'
$fullPreflight = Join-Path $Root 'tools\full-mod-e2e.ps1'
$powershell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$expectedId = 3764815892
$expectedTitle = -join ([char[]]@(
    0x6C5F, 0x6E56, 0x6709, 0x7075, 0x6D4B, 0x8BD5, 0x7248))
$expectedFiles = @(
    'LICENSE',
    'NOTICE',
    'THIRD_PARTY_NOTICES.md',
    'Config.lua',
    'Settings.Lua',
    'workshop-cover-0.31.jpg',
    'workshop-detail-0.31-01-npc-actions.jpg',
    'workshop-detail-0.31-02-linger.jpg',
    'workshop-detail-0.31-03-living-memory.jpg',
    'workshop-detail-0.31-04-group-actions.jpg',
    'workshop-detail-0.31-05-companion-monthly.jpg',
    'workshop-detail-0.31-06-monthly-saga.jpg',
    'workshop-detail-0.31-07-chat-experience.jpg',
    'workshop-detail-0.31-08-persona-world.jpg',
    'Plugins/JianghuYouling.Frontend.dll',
    'Plugins/JianghuYouling.Core.dll',
    'Plugins/JianghuYouling.Backend.dll'
)

function Assert-Case([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "Workshop validator self-test failed: $Name" }
}

# Some locked-down Windows PowerShell hosts do not auto-load Microsoft.PowerShell.Utility.
# Keep the validator self-test deterministic with an equivalent local SHA-256 implementation.
if ($null -eq (Get-Command Get-FileHash -ErrorAction SilentlyContinue)) {
    function Get-FileHash {
        param(
            [Parameter(Mandatory = $true)][string]$LiteralPath,
            [string]$Algorithm = 'SHA256'
        )
        if ($Algorithm -ne 'SHA256') { throw "Unsupported hash algorithm: $Algorithm" }
        $stream = [IO.File]::OpenRead($LiteralPath)
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $sha.Dispose() }
        }
        finally { $stream.Dispose() }
        [pscustomobject]@{ Algorithm = 'SHA256'; Hash = $hash; Path = $LiteralPath }
    }
}

function Write-TestConfig([string]$Body) {
    [IO.File]::WriteAllText((Join-Path $content 'Config.lua'), $Body, $utf8)
}

function Write-HashManifest {
    $hashes = [ordered]@{}
    foreach ($relative in $expectedFiles) {
        $path = Join-Path $content ($relative.Replace([char]47, [char]92))
        $hashes[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    [IO.File]::WriteAllText($manifest,
        (($hashes | ConvertTo-Json -Depth 3) + "`n"), $utf8)
}

function Validator-Passes {
    $script:lastValidatorError = $null
    try {
        $null = & $validator -ContentFolder $content -ExpectedFileId $expectedId `
            -ExpectedTitle $expectedTitle -ExpectedHashManifest $manifest
        return $true
    }
    catch {
        $script:lastValidatorError = $_.Exception.Message + "`n" + $_.ScriptStackTrace
        return $false
    }
}

try {
    [IO.Directory]::CreateDirectory($content) | Out-Null
    foreach ($relative in $expectedFiles) {
        $path = Join-Path $content ($relative.Replace([char]47, [char]92))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
        [IO.File]::WriteAllText($path, "fixture:$relative", $utf8)
    }
    Write-TestConfig @"
return {
    Title = "$expectedTitle",
    Source = 1,
    FileId = $expectedId,
}
"@
    Write-HashManifest
    $validPackagePassed = Validator-Passes
    if (-not $validPackagePassed) {
        throw "Workshop validator rejected its valid fixture: $script:lastValidatorError"
    }
    Assert-Case 'valid exact package succeeds' $validPackagePassed

    [IO.File]::WriteAllText((Join-Path $content 'stale-player.log'), 'secret-free stale file', $utf8)
    Assert-Case 'undeclared stale file fails' (-not (Validator-Passes))
    [IO.File]::Delete((Join-Path $content 'stale-player.log'))

    [IO.File]::Delete((Join-Path $content 'Settings.Lua'))
    Assert-Case 'missing declared file fails' (-not (Validator-Passes))
    [IO.File]::WriteAllText((Join-Path $content 'Settings.Lua'), 'fixture:Settings.Lua', $utf8)
    Write-HashManifest

    [IO.File]::AppendAllText((Join-Path $content 'workshop-cover-0.31.jpg'), 'tampered', $utf8)
    Assert-Case 'artifact hash mismatch fails' (-not (Validator-Passes))
    [IO.File]::WriteAllText((Join-Path $content 'workshop-cover-0.31.jpg'),
        'fixture:workshop-cover-0.31.jpg', $utf8)
    Write-HashManifest

    [IO.File]::WriteAllText($manifest, "{}`n", $utf8)
    Assert-Case 'manifest count mismatch fails' (-not (Validator-Passes))

    Write-TestConfig @"
return {
    Title = "$expectedTitle",
    Source = 1,
    Source = 0,
    FileId = $expectedId,
}
"@
    Write-HashManifest
    Assert-Case 'duplicate top-level identity fails' (-not (Validator-Passes))

    Write-TestConfig @"
return {
    Title = "$expectedTitle" .. "错误",
    Source = 1,
    FileId = $expectedId,
}
"@
    Write-HashManifest
    Assert-Case 'concatenated title expression fails' (-not (Validator-Passes))

    Write-TestConfig @"
return {
    Title = "$expectedTitle",
    Source = 1 + 1,
    FileId = $expectedId,
}
"@
    Write-HashManifest
    Assert-Case 'computed source expression fails' (-not (Validator-Passes))

    Write-TestConfig @"
return {
    Title = "$expectedTitle",
    Source = 1,
    FileId = $expectedId + 1,
}
"@
    Write-HashManifest
    Assert-Case 'computed file ID expression fails' (-not (Validator-Passes))

    Write-TestConfig @"
local config = {
    Title = "$expectedTitle",
    Source = 1,
    FileId = $expectedId,
}
config.FileId = 3747674580
return config
"@
    Write-HashManifest
    Assert-Case 'post-construction identity mutation fails' (-not (Validator-Passes))

    Write-TestConfig (-join @(
        "return {`n",
        "    Title = `"$expectedTitle`",`n",
        "    Source = 1,`n",
        "    FileId = $expectedId,`n",
        "    -- hidden by isolated carriage return",
        "`r    FileId = 3747674580,`n",
        "}`n"
    ))
    Write-HashManifest
    Assert-Case 'isolated carriage return ends a Lua short comment' (
        -not (Validator-Passes))

    Write-TestConfig @"
return {
    Description = [=[
        Title = "$expectedTitle",
        Source = 1,
        FileId = $expectedId,
    ]=],
    Title = "wrong-title",
    Source = 0,
    FileId = 1,
}
"@
    Write-HashManifest
    Assert-Case 'description-embedded identity cannot spoof top-level values' (
        -not (Validator-Passes))

    $savedErrorAction = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $partialExit = & $powershell -NoProfile -ExecutionPolicy Bypass -File $fullPreflight `
        -WorkshopExpectedFileId -1 2>&1
    $partialExitCode = $LASTEXITCODE
    $ErrorActionPreference = $savedErrorAction
    Assert-Case 'partial wrapper metadata is rejected before ordinary preflight' (
        $partialExitCode -ne 0 -and
        ([string]::Join("`n", @($partialExit))).Contains(
            'requires explicit -TestReleaseCandidate mode'))

    $ErrorActionPreference = 'Continue'
    $missingExit = & $powershell -NoProfile -ExecutionPolicy Bypass -File $fullPreflight `
        -TestReleaseCandidate 2>&1
    $missingExitCode = $LASTEXITCODE
    $ErrorActionPreference = $savedErrorAction
    Assert-Case 'release mode requires all candidate metadata' (
        $missingExitCode -ne 0 -and
        ([string]::Join("`n", @($missingExit))).Contains(
            'requires item ID, title, and hash manifest'))

    $ErrorActionPreference = 'Continue'
    $formalIdExit = & $powershell -NoProfile -ExecutionPolicy Bypass -File $fullPreflight `
        -TestReleaseCandidate `
        -WorkshopCandidateFolder $content `
        -WorkshopExpectedFileId 3747674580 `
        -WorkshopExpectedTitle $expectedTitle `
        -WorkshopExpectedHashManifest $manifest 2>&1
    $formalIdExitCode = $LASTEXITCODE
    $ErrorActionPreference = $savedErrorAction
    Assert-Case 'test release wrapper rejects a self-consistent formal Workshop ID' (
        $formalIdExitCode -ne 0 -and
        ([string]::Join("`n", @($formalIdExit))).Contains(
            'formal Workshop candidates are forbidden'))

    Write-Output 'Workshop package validator behavioral tests passed.'
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).
        TrimEnd([char]92) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTestRoot.StartsWith($resolvedTemp,
            [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Directory]::Exists($resolvedTestRoot)) {
        [IO.Directory]::Delete($resolvedTestRoot, $true)
    }
}

# Negative cases above intentionally launch child processes that exit non-zero.
# Do not leak the last expected child exit code to the release-gate caller.
exit 0
