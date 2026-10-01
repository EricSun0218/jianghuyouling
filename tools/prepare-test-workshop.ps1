param(
    [Parameter(Mandatory = $true)][string]$ReleaseVersion,
    [string]$Root = '',
    [switch]$Resume
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
}
$rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $rootPath '.tmpdeploy'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $stagingRoot ('release-' + $ReleaseVersion)))
if (-not $releaseRoot.StartsWith($stagingRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Workshop candidate path escaped .tmpdeploy.'
}
if ((Test-Path -LiteralPath $releaseRoot) -and -not $Resume) {
    throw "Workshop candidate already exists; refusing to overwrite: $releaseRoot"
}

$candidate = Join-Path $releaseRoot 'test'
$plugins = Join-Path $candidate 'Plugins'
[IO.Directory]::CreateDirectory($plugins) | Out-Null

# 正式版与测试版共用同一份介绍正文。发布候选额外携带一份明确的 BBCode
# 文案文件，避免只上传内容包却漏掉测试页介绍，或复制到旧的测试版草稿。
$formalDescription = Join-Path $rootPath 'docs\workshop-description-formal.bbcode.txt'
$testDescription = Join-Path $rootPath 'docs\workshop-description-test.bbcode.txt'
if (-not (Test-Path -LiteralPath $formalDescription -PathType Leaf) -or
    -not (Test-Path -LiteralPath $testDescription -PathType Leaf)) {
    throw 'Workshop description source is missing.'
}
$formalDescriptionText = [IO.File]::ReadAllText($formalDescription, [Text.Encoding]::UTF8)
$testDescriptionText = [IO.File]::ReadAllText($testDescription, [Text.Encoding]::UTF8)
if ($formalDescriptionText -cne $testDescriptionText) {
    throw 'Test Workshop description is not synchronized with the formal description.'
}
$descriptionVersionMarker = '【当前版本】' + $ReleaseVersion
if (-not $formalDescriptionText.Contains($descriptionVersionMarker,
        [StringComparison]::Ordinal)) {
    throw "Workshop description version marker is missing: $descriptionVersionMarker"
}
$descriptionUtf8Bytes = [Text.Encoding]::UTF8.GetByteCount(
    $formalDescriptionText.Trim())
if ($descriptionUtf8Bytes -ge 8000) {
    throw "Workshop description exceeds Steam's UTF-8 byte limit: $descriptionUtf8Bytes/7999"
}

foreach ($project in @('JianghuYouling.Core', 'JianghuYouling.Backend',
        'JianghuYouling.Frontend')) {
    $dll = Join-Path $rootPath ("src\$project\bin\Release\$project.dll")
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "Release DLL is missing: $dll"
    }
    Copy-Item -LiteralPath $dll -Destination $plugins
}

$assets = @(
    'Settings.Lua',
    'workshop-cover-0.31.jpg',
    'workshop-detail-0.31-01-npc-actions.jpg',
    'workshop-detail-0.31-02-linger.jpg',
    'workshop-detail-0.31-03-living-memory.jpg',
    'workshop-detail-0.31-04-group-actions.jpg',
    'workshop-detail-0.31-05-companion-monthly.jpg',
    'workshop-detail-0.31-06-monthly-saga.jpg',
    'workshop-detail-0.31-07-chat-experience.jpg',
    'workshop-detail-0.31-08-persona-world.jpg'
)
foreach ($asset in $assets) {
    $source = Join-Path $rootPath (Join-Path 'deploy' $asset)
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Workshop asset is missing: $source"
    }
    Copy-Item -LiteralPath $source -Destination $candidate
}

foreach ($license in @('LICENSE', 'NOTICE', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $rootPath $license) -Destination $candidate
}

$utf8 = New-Object Text.UTF8Encoding($false)
$sourceConfig = Join-Path $rootPath 'deploy\Config.lua'
$config = [IO.File]::ReadAllText($sourceConfig, [Text.Encoding]::UTF8)
$testTitle = -join @(
    [char]0x6C5F, [char]0x6E56, [char]0x6709, [char]0x7075,
    [char]0x6D4B, [char]0x8BD5, [char]0x7248)
$config = [regex]::Replace($config, '(?m)^(\s*)Title\s*=\s*"[^"\r\n]*",',
    ('$1Title = "' + $testTitle + '",'), 1)
$config = $config.Replace('Source = 0,', 'Source = 1,')
$config = $config.Replace('FileId = 3747674580,', 'FileId = 3764815892,')
$versionLiteral = 'Version = "' + $ReleaseVersion + '"'
$titleOk = $config.Contains('Title = "' + $testTitle + '"')
$versionOk = $config.Contains($versionLiteral)
$sourceOk = $config.Contains('Source = 1,')
$fileIdOk = $config.Contains('FileId = 3764815892,')
if (-not $titleOk -or -not $versionOk -or -not $sourceOk -or -not $fileIdOk) {
    throw "Test Workshop Config identity rewrite failed: title=$titleOk version=$versionOk source=$sourceOk fileId=$fileIdOk"
}
[IO.File]::WriteAllText((Join-Path $candidate 'Config.lua'), $config, $utf8)
[IO.File]::WriteAllText((Join-Path $releaseRoot 'test.workshop-description.bbcode.txt'),
    $formalDescriptionText, $utf8)

$hashes = [ordered]@{}
foreach ($file in @(Get-ChildItem -LiteralPath $candidate -Recurse -File |
        Sort-Object FullName)) {
    $relative = $file.FullName.Substring($candidate.Length + 1).Replace('\', '/')
    $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
}
$manifest = Join-Path $releaseRoot 'test.hashes.json'
[IO.File]::WriteAllText($manifest, ($hashes | ConvertTo-Json), $utf8)

[pscustomobject]@{
    Candidate = $candidate
    Manifest = $manifest
    Description = (Join-Path $releaseRoot 'test.workshop-description.bbcode.txt')
    WorkshopFileId = 3764815892
    Files = $hashes.Count
}
