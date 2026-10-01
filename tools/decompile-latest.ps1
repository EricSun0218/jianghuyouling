param(
    [string]$SteamApps = "",
    [string]$GameRoot = "",
    [string]$OutRoot = $(if (-not [string]::IsNullOrWhiteSpace($env:JHYL_DECOMPILED_ROOT)) {
        $env:JHYL_DECOMPILED_ROOT
        } else {
        Join-Path (Split-Path -Parent $PSScriptRoot) '.decompiled'
    })
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$defaultSteamApps = "C:\Program Files (x86)\Steam\steamapps"
if ([string]::IsNullOrWhiteSpace($GameRoot)) { $GameRoot = $env:TAIWU_GAME_DIR }
if ([string]::IsNullOrWhiteSpace($SteamApps)) {
    $SteamApps = if ([string]::IsNullOrWhiteSpace($GameRoot)) {
        $defaultSteamApps
    } else {
        Split-Path -Parent (Split-Path -Parent ([IO.Path]::GetFullPath($GameRoot).TrimEnd('\')))
    }
}
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $GameRoot = Join-Path $SteamApps "common\The Scroll Of Taiwu"
}
$SteamApps = [IO.Path]::GetFullPath($SteamApps).TrimEnd('\')
$GameRoot = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\')

$appid = "838350"
$acf = Join-Path $SteamApps "appmanifest_$appid.acf"

function Read-BuildId {
    $match = Select-String -LiteralPath $acf -Pattern '"buildid"\s+"([0-9]+)"' | Select-Object -First 1
    if (-not $match) { throw "Cannot read buildid from $acf" }
    return [regex]::Match($match.Line, '"buildid"\s+"([0-9]+)"').Groups[1].Value
}

function Get-TextSha256([string]$text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')
    }
    finally { $sha.Dispose() }
}

function Get-SourceRows([object[]]$items) {
    return @($items |
        Sort-Object @{ Expression = { $_.Side } }, @{ Expression = { [IO.Path]::GetFileName($_.Path) } } |
        ForEach-Object {
            $file = Get-Item -LiteralPath $_.Path
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            '{0}/{1}|{2}|{3}|{4}' -f $_.Side, $file.Name, $file.Length, $file.LastWriteTimeUtc.ToString('o'), $hash
        })
}

$buildId = Read-BuildId
$game = $GameRoot
$managed = Join-Path $game "The Scroll of Taiwu_Data\Managed"
$backend = Join-Path $game "Backend"
$tool = Join-Path $env:USERPROFILE ".dotnet\tools\ilspycmd.exe"
if (-not (Test-Path -LiteralPath $tool)) { throw "ilspycmd not found: $tool" }

$sources = New-Object 'System.Collections.Generic.List[object]'
function Add-Source([string]$side, [string]$path) {
    if (Test-Path -LiteralPath $path) {
        $sources.Add([pscustomobject]@{ Side = $side; Path = [IO.Path]::GetFullPath($path) })
    }
}

Add-Source "Frontend" (Join-Path $managed "Assembly-CSharp.dll")
Add-Source "Frontend" (Join-Path $managed "Assembly-CSharp-firstpass.dll")
Add-Source "Frontend" (Join-Path $managed "TaiwuModdingLib.dll")
Add-Source "Frontend" (Join-Path $managed "GameData.dll")
Get-ChildItem -LiteralPath $managed -Filter "GameData.*.dll" -File -ErrorAction SilentlyContinue |
    ForEach-Object { Add-Source "Frontend" $_.FullName }

Add-Source "Backend" (Join-Path $backend "GameData.dll")
Add-Source "Backend" (Join-Path $backend "TaiwuModdingLib.dll")
Get-ChildItem -LiteralPath $backend -Filter "GameData.*.dll" -File -ErrorAction SilentlyContinue |
    ForEach-Object { Add-Source "Backend" $_.FullName }

$sources = @($sources | Sort-Object Side, Path -Unique)
if ($sources.Count -eq 0) { throw "No game-authored assemblies found under $game" }

New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
$out = Join-Path $OutRoot "taiwu_decomp_b$buildId"
$stage = Join-Path $OutRoot (".taiwu_decomp_b{0}.staging-{1}" -f $buildId, $PID)
if (Test-Path -LiteralPath $out) { throw "Target already exists; refusing to mix snapshots: $out" }
if (Test-Path -LiteralPath $stage) { throw "Staging path already exists: $stage" }
New-Item -ItemType Directory -Path $stage | Out-Null

$sourceRowsBefore = Get-SourceRows $sources
$sourceTextBefore = [string]::Join("`n", $sourceRowsBefore)
$sourceFingerprint = Get-TextSha256 $sourceTextBefore
@("format=side/file|bytes|last_write_utc|sha256") + $sourceRowsBefore |
    Set-Content -Encoding UTF8 -LiteralPath (Join-Path $stage "SOURCE_ASSEMBLIES.sha256")

$toolVersionLines = @(& $tool --version 2>$null)
$toolVersion = if ($toolVersionLines.Count -gt 0) { $toolVersionLines[0].Trim() } else { "unknown" }

function Decompile-One([object]$source) {
    $dll = $source.Path
    $side = $source.Side
    $name = [IO.Path]::GetFileNameWithoutExtension($dll)
    $odir = Join-Path (Join-Path $stage $side) $name
    New-Item -ItemType Directory -Force -Path $odir | Out-Null
    $size = (Get-Item -LiteralPath $dll).Length
    Write-Host "[START] $side/$name ($size bytes)"
    & $tool $dll -p -o $odir *> (Join-Path $odir "_ilspy.log")
    $rc = $LASTEXITCODE
    $count = (Get-ChildItem -LiteralPath $odir -Recurse -Filter "*.cs" -File -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "[DONE rc=$rc] $side/$name -> $count cs files"
    if ($rc -ne 0) { throw "ilspycmd failed for $side/$name with exit code $rc" }
    if ($count -le 0) { throw "ilspycmd produced no C# files for $side/$name" }
}

try {
    Write-Host "================= DECOMPILING $($sources.Count) ASSEMBLIES ================="
    foreach ($source in $sources) { Decompile-One $source }

    $buildIdAfter = Read-BuildId
    if ($buildIdAfter -ne $buildId) { throw "Steam build changed during decompilation: $buildId -> $buildIdAfter" }

    $sourceRowsAfter = Get-SourceRows $sources
    $sourceTextAfter = [string]::Join("`n", $sourceRowsAfter)
    if ($sourceTextAfter -ne $sourceTextBefore) { throw "Source assembly hashes changed during decompilation" }

    $logs = @(Get-ChildItem -LiteralPath $stage -Recurse -Filter "_ilspy.log" -File)
    if ($logs.Count -ne $sources.Count) { throw "Expected $($sources.Count) ILSpy logs, found $($logs.Count)" }
    $badLogs = @($logs | Select-String -Pattern '(?i)(^|[^a-z])(error|exception|fatal)([^a-z]|$)')
    if ($badLogs.Count -gt 0) { throw "Decompiler logs contain error markers; inspect staging path: $stage" }

    $total = (Get-ChildItem -LiteralPath $stage -Recurse -Filter "*.cs" -File | Measure-Object).Count
    $projectCount = (Get-ChildItem -LiteralPath $stage -Recurse -Filter "*.csproj" -File | Measure-Object).Count
    if ($total -le 0 -or $projectCount -ne $sources.Count) {
        throw "Snapshot validation failed: cs=$total csproj=$projectCount expectedProjects=$($sources.Count)"
    }

    @(
        "buildid = $buildId",
        "game_name = The Scroll of Taiwu: Beyond The Dome",
        "decompiled_at = $(Get-Date -Format yyyy-MM-dd)",
        "tool = $toolVersion",
        "source = $game",
        "scope = game-authored assemblies only (Assembly-CSharp*, GameData*, TaiwuModdingLib)",
        "layout = Frontend/ for Unity/Mono assemblies, Backend/ for backend .NET assemblies",
        "source_assemblies = $($sources.Count)",
        "source_manifest = SOURCE_ASSEMBLIES.sha256",
        "source_manifest_aggregate_sha256 = $sourceFingerprint",
        "decompiled_csharp_files = $total",
        "validation = $($sources.Count)/$($sources.Count) assemblies, all ilspy exit codes 0, no decompiler errors"
    ) | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $stage "VERSION.txt")

    New-Item -ItemType File -Path (Join-Path $stage "_COMPLETE") | Out-Null
    Move-Item -LiteralPath $stage -Destination $out
    Write-Host "================= ALL DONE: $total cs files total ================="
    Write-Host "OUT=$out"
}
catch {
    Write-Host "FAILED; incomplete staging tree retained at $stage"
    throw
}
