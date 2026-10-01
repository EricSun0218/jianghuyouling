param(
    [string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$GameRoot = '',
    [switch]$SkipBuild,
    [switch]$OnlineDeepSeek,
    [string]$DeepSeekProtectedConfig,
    [switch]$TestReleaseCandidate,
    [string]$WorkshopCandidateFolder = '',
    [long]$WorkshopExpectedFileId = 0,
    [string]$WorkshopExpectedTitle = '',
    [string]$WorkshopExpectedHashManifest = '',
    [int]$ProcessTimeoutSeconds = 240,
    [ValidateRange(1, 5)][int]$OnlineRetries = 3
)

$hasWorkshopMetadata = $PSBoundParameters.ContainsKey('WorkshopCandidateFolder') -or
    $PSBoundParameters.ContainsKey('WorkshopExpectedFileId') -or
    $PSBoundParameters.ContainsKey('WorkshopExpectedTitle') -or
    $PSBoundParameters.ContainsKey('WorkshopExpectedHashManifest')
$missingWorkshopMetadata = $WorkshopExpectedFileId -le 0 -or
    [string]::IsNullOrWhiteSpace($WorkshopCandidateFolder) -or
    [string]::IsNullOrWhiteSpace($WorkshopExpectedTitle) -or
    [string]::IsNullOrWhiteSpace($WorkshopExpectedHashManifest)
if ($TestReleaseCandidate -and $missingWorkshopMetadata) {
    throw 'Test Workshop candidate validation requires item ID, title, and hash manifest'
}
if (-not $TestReleaseCandidate -and $hasWorkshopMetadata) {
    throw 'Workshop candidate metadata requires explicit -TestReleaseCandidate mode'
}
$testWorkshopFileId = 3764815892
$testWorkshopTitle = '江湖有灵测试版'
if ($TestReleaseCandidate -and
    ($WorkshopExpectedFileId -ne $testWorkshopFileId -or
        $WorkshopExpectedTitle -cne $testWorkshopTitle)) {
    throw "Test release gate only permits '$testWorkshopTitle' ($testWorkshopFileId); formal Workshop candidates are forbidden."
}

# Backward-compatible automated preflight entry point.
# This is NOT real-game full-Mod E2E evidence and cannot satisfy an E2E-* item
# by itself. Use verify-real-game-e2e-evidence.ps1 for the strict release gate.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Write-Host 'AUTOMATED PREFLIGHT ONLY: this command is not real-game full-Mod E2E evidence.' -ForegroundColor Yellow
$defaultGameRoot = 'C:\Program Files (x86)\Steam\steamapps\common\The Scroll Of Taiwu'
if ([string]::IsNullOrWhiteSpace($GameRoot)) { $GameRoot = $env:TAIWU_GAME_DIR }
if ([string]::IsNullOrWhiteSpace($GameRoot)) { $GameRoot = $defaultGameRoot }
$GameRoot = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
$SteamAppsRoot = Split-Path -Parent (Split-Path -Parent $GameRoot)
$DecompiledRoot = if (-not [string]::IsNullOrWhiteSpace($env:JHYL_DECOMPILED_ROOT)) {
    [IO.Path]::GetFullPath($env:JHYL_DECOMPILED_ROOT).TrimEnd('\')
} else {
    Join-Path $Root '.decompiled'
}
$Utf8NoBom = New-Object Text.UTF8Encoding($false)
$Started = [DateTime]::UtcNow
$ResultsRoot = Join-Path $Root '.e2e-results'
$RunId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + $PID
$RunDir = Join-Path $ResultsRoot $RunId
[IO.Directory]::CreateDirectory($RunDir) | Out-Null
$Checks = New-Object 'System.Collections.Generic.List[object]'

function Add-Check([string]$Name, [string]$Status, [string]$Evidence) {
    $Checks.Add([pscustomobject][ordered]@{ name=$Name; status=$Status; evidence=$Evidence })
}

function Quote-Arg([string]$Value) {
    if ($null -eq $Value) { return '""' }
    # Windows argv quoting (CommandLineToArgvW): n backslashes before a quote must
    # become 2n+1 (escaped quote); trailing backslashes must double, or they would
    # escape the closing quote and swallow the next argument.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)\z', '$1$1')
    return '"' + $escaped + '"'
}

function Tail([string]$Text, [int]$Lines = 30) {
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    $parts = [Text.RegularExpressions.Regex]::Split($Text, "\r?\n")
    $start = [Math]::Max(0, $parts.Length - $Lines)
    return [string]::Join([Environment]::NewLine, $parts[$start..($parts.Length - 1)])
}

function Invoke-External([string]$Name, [string]$FileName, [string]$Arguments,
    [int]$TimeoutSeconds = $ProcessTimeoutSeconds, [hashtable]$Environment = $null,
    [bool]$RecordFailure = $true) {
    $safeName = [Text.RegularExpressions.Regex]::Replace($Name, '[^A-Za-z0-9_.-]+', '_')
    $logPath = Join-Path $RunDir ($safeName + '.log')
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $FileName
    $psi.Arguments = $Arguments
    $psi.WorkingDirectory = $Root
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $windowsPowerShell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    if ([IO.Path]::GetFullPath($FileName).Equals(
        [IO.Path]::GetFullPath($windowsPowerShell), [StringComparison]::OrdinalIgnoreCase)) {
        # A pwsh host can prepend PowerShell 7 modules to PSModulePath. Windows
        # PowerShell 5.1 then finds the incompatible Utility module first and
        # cannot auto-load commands such as Get-FileHash in nested fixtures.
        $windowsModuleRoots = @(
            (Join-Path $env:USERPROFILE 'Documents\WindowsPowerShell\Modules'),
            (Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules'),
            (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules')
        ) | Select-Object -Unique
        $psi.EnvironmentVariables['PSModulePath'] = [string]::Join(';', $windowsModuleRoots)
    }
    if ($Environment) {
        foreach ($key in $Environment.Keys) { $psi.EnvironmentVariables[$key] = [string]$Environment[$key] }
    }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($psi)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit([Math]::Max(1, $TimeoutSeconds) * 1000)) {
        try { $process.Kill() } catch { }
        $process.WaitForExit()
        $stdout = $stdoutTask.Result; $stderr = $stderrTask.Result
        [IO.File]::WriteAllText($logPath, $stdout + [Environment]::NewLine + $stderr, $Utf8NoBom)
        if ($RecordFailure) { Add-Check $Name 'FAIL' ("timeout={0}s log={1}" -f $TimeoutSeconds, $logPath) }
        throw "$Name timed out after $TimeoutSeconds seconds"
    }
    $stdout = $stdoutTask.Result; $stderr = $stderrTask.Result
    [IO.File]::WriteAllText($logPath, $stdout + [Environment]::NewLine + $stderr, $Utf8NoBom)
    if ($process.ExitCode -ne 0) {
        if ($RecordFailure) { Add-Check $Name 'FAIL' ("exit={0} log={1}" -f $process.ExitCode, $logPath) }
        throw ("{0} failed (exit {1})`n{2}" -f $Name, $process.ExitCode, (Tail ($stdout + "`n" + $stderr)))
    }
    Add-Check $Name 'PASS' ("ms={0} log={1}" -f $sw.ElapsedMilliseconds, $logPath)
}

function Invoke-OnlineWithRetry([string]$Name, [string]$FileName, [string]$Arguments,
    [hashtable]$Environment) {
    $last = $null
    for ($attempt = 1; $attempt -le $OnlineRetries; $attempt++) {
        try {
            Invoke-External ($Name + '-attempt-' + $attempt) $FileName $Arguments 240 $Environment $false
            Add-Check $Name 'PASS' ("attempt={0}/{1}" -f $attempt, $OnlineRetries)
            return
        }
        catch {
            $last = $_
            if ($attempt -lt $OnlineRetries) { Start-Sleep -Seconds ([Math]::Min(4, $attempt)) }
        }
    }
    Add-Check $Name 'FAIL' ("attempts={0}" -f $OnlineRetries)
    throw $last
}

function Assert-File([string]$Name, [string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Add-Check $Name 'FAIL' "missing=$Path"
        throw "Missing required file: $Path"
    }
    Add-Check $Name 'PASS' $Path
}

function Assert-TreeEqual([string]$Name, [string]$Left, [string]$Right) {
    $leftRows = @(Get-ChildItem -LiteralPath $Left -File -Recurse | ForEach-Object {
        $rel = $_.FullName.Substring([IO.Path]::GetFullPath($Left).TrimEnd('\').Length).TrimStart('\')
        "$rel|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object)
    $rightRows = @(Get-ChildItem -LiteralPath $Right -File -Recurse | ForEach-Object {
        $rel = $_.FullName.Substring([IO.Path]::GetFullPath($Right).TrimEnd('\').Length).TrimStart('\')
        "$rel|$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)"
    } | Sort-Object)
    if ($leftRows.Count -ne $rightRows.Count -or [string]::Join("`n", $leftRows) -ne [string]::Join("`n", $rightRows)) {
        Add-Check $Name 'FAIL' "left=$Left right=$Right"
        throw "$Name differs"
    }
    Add-Check $Name 'PASS' ("files={0}" -f $leftRows.Count)
}

function Assert-DecompileAuthority {
    $steamApps = $SteamAppsRoot
    $appManifest = Join-Path $steamApps 'appmanifest_838350.acf'
    Assert-File 'steam-appmanifest' $appManifest
    $line = Select-String -LiteralPath $appManifest -Pattern '"buildid"\s+"([0-9]+)"' | Select-Object -First 1
    if (-not $line) { throw 'Cannot read Taiwu buildid' }
    $buildId = [regex]::Match($line.Line, '"buildid"\s+"([0-9]+)"').Groups[1].Value
    $decomp = Join-Path $DecompiledRoot ('taiwu_decomp_b' + $buildId)
    Assert-File 'decompile-complete-marker' (Join-Path $decomp '_COMPLETE')
    $manifest = Join-Path $decomp 'SOURCE_ASSEMBLIES.sha256'
    Assert-File 'decompile-source-manifest' $manifest
    $versionPath = Join-Path $decomp 'VERSION.txt'
    Assert-File 'decompile-version-manifest' $versionPath
    $game = $GameRoot
    $verified = 0
    $sourceRows = @(Get-Content -LiteralPath $manifest -Encoding UTF8 | Select-Object -Skip 1 |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($row in $sourceRows) {
        $parts = $row.Split('|')
        if ($parts.Length -ne 4) { throw "Malformed decompile manifest row: $row" }
        $scopeAndFile = $parts[0].Split('/', 2)
        if ($scopeAndFile.Length -ne 2) { throw "Malformed decompile scope: $row" }
        $installed = if ($scopeAndFile[0] -eq 'Backend') {
            Join-Path (Join-Path $game 'Backend') $scopeAndFile[1]
        } else {
            Join-Path (Join-Path $game 'The Scroll of Taiwu_Data\Managed') $scopeAndFile[1]
        }
        if (-not (Test-Path -LiteralPath $installed -PathType Leaf)) { throw "Installed authority missing: $installed" }
        $hash = (Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash
        if ($hash -ne $parts[3]) { throw "Installed/decompiled authority hash mismatch: $($parts[0])" }
        $verified++
    }
    $version = @{}
    foreach ($line in (Get-Content -LiteralPath $versionPath -Encoding UTF8)) {
        $pair = $line.Split('=', 2)
        if ($pair.Length -eq 2) { $version[$pair[0].Trim()] = $pair[1].Trim() }
    }
    $declaredAssemblies = 0; $declaredCs = 0
    if (-not [int]::TryParse([string]$version['source_assemblies'], [ref]$declaredAssemblies) -or
        -not [int]::TryParse([string]$version['decompiled_csharp_files'], [ref]$declaredCs)) {
        throw 'Decompile VERSION.txt is missing numeric inventory declarations'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $aggregate = [BitConverter]::ToString($sha.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $sourceRows)))).Replace('-', '')
    }
    finally { $sha.Dispose() }
    $csCount = @(Get-ChildItem -LiteralPath $decomp -Filter '*.cs' -File -Recurse).Count
    $projectCount = @(Get-ChildItem -LiteralPath $decomp -Filter '*.csproj' -File -Recurse).Count
    $logCount = @(Get-ChildItem -LiteralPath $decomp -Filter '_ilspy.log' -File -Recurse).Count
    if ($verified -le 0 -or $verified -ne $sourceRows.Count -or
        $verified -ne $declaredAssemblies -or $verified -ne $projectCount -or $verified -ne $logCount -or
        $csCount -le 0 -or $csCount -ne $declaredCs -or
        $aggregate -ne [string]$version['source_manifest_aggregate_sha256']) {
        throw "Unexpected authority inventory: verified=$verified rows=$($sourceRows.Count) declaredAssemblies=$declaredAssemblies projects=$projectCount logs=$logCount cs=$csCount declaredCs=$declaredCs aggregateMatch=$($aggregate -eq [string]$version['source_manifest_aggregate_sha256'])"
    }
    Add-Check 'decompile-authority' 'PASS' "build=$buildId assemblies=$verified cs=$csCount manifest=verified"

    # Succession is a same-world identity boundary. Pin the release gate to the
    # currently installed build's authoritative frontend notification and backend
    # transfer route so a future game update cannot silently bypass our cancellation.
    $basicGameDataPath = Join-Path $decomp 'Frontend\Assembly-CSharp\BasicGameData.cs'
    $taiwuDomainPath = Join-Path $decomp 'Backend\GameData\GameData.Domains.Taiwu\TaiwuDomain.cs'
    Assert-File 'native-succession-frontend-source' $basicGameDataPath
    Assert-File 'native-succession-backend-source' $taiwuDomainPath
    $basicGameDataText = [IO.File]::ReadAllText($basicGameDataPath)
    $taiwuDomainText = [IO.File]::ReadAllText($taiwuDomainPath)
    if ($basicGameDataText.IndexOf('private void UpdateTaiwuDomainData(', [StringComparison]::Ordinal) -lt 0 -or
        $basicGameDataText.IndexOf('GEvent.OnEvent(EEvents.OnTaiwuCharIdChange', [StringComparison]::Ordinal) -lt 0 -or
        $taiwuDomainText.IndexOf('public void ConfirmChosenSuccessor(', [StringComparison]::Ordinal) -lt 0 -or
        $taiwuDomainText.IndexOf('TransferTaiwuData(context, element_Objects, _taiwuChar', [StringComparison]::Ordinal) -lt 0) {
        throw "Installed Taiwu build $buildId changed its authoritative succession contract"
    }
    Add-Check 'native-succession-contract' 'PASS' "build=$buildId TaiwuId-change notification and successor transfer verified"
}

function Assert-NoSecrets {
    $pattern = '(?i)(sk-[A-Za-z0-9_-]{16,}|Bearer\s+[A-Za-z0-9._~+/-]{20,}|api[_-]?key["'']?\s*[:=]\s*["''][^"'']{12,}["''])'
    # Limit the preflight scan to material that Git could publish.  Recursing the
    # workspace first walks multi-gigabyte ignored decompiles, evidence backups,
    # and temporary build trees before Where-Object gets a chance to exclude them.
    $git = (Get-Command git -ErrorAction Stop).Source
    # core.quotepath=false prevents Git from returning a quoted octal escape string for
    # tracked Chinese filenames (which is display syntax, not a filesystem path).
    $relativeFiles = @(& $git -C $Root -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed while preparing the secret scan' }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $rootPrefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    $files = New-Object 'System.Collections.Generic.List[IO.FileInfo]'
    foreach ($relative in @($relativeFiles | Sort-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace([string]$relative)) { continue }
        $full = [IO.Path]::GetFullPath((Join-Path $rootFull ([string]$relative)))
        if (-not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "git ls-files returned a path outside the repository: $relative"
        }
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            $files.Add((Get-Item -LiteralPath $full))
        }
    }
    $hits = New-Object 'System.Collections.Generic.List[string]'
    foreach ($file in $files) {
        if ($file.Length -gt 8MB) {
            throw ('Repository secret preflight refuses an unscanned file above 8 MiB: ' +
                $file.FullName.Substring($rootFull.Length).TrimStart('\', '/'))
        }
        try {
            $text = [IO.File]::ReadAllText($file.FullName)
            if ($text -match $pattern) { $hits.Add($file.FullName.Substring($rootFull.Length).TrimStart('\', '/')) }
        }
        catch {
            throw ('Repository secret preflight could not read: ' +
                $file.FullName.Substring($rootFull.Length).TrimStart('\', '/'))
        }
    }
    if ($hits.Count -gt 0) {
        Add-Check 'secret-scan' 'FAIL' ([string]::Join(',', $hits))
        throw 'Potential credential material found; values intentionally not printed'
    }
    Add-Check 'secret-scan' 'PASS' ("files={0}" -f $files.Count)
}

function Assert-SanitizedPersonaAssets {
    $resourceDir = Join-Path $Root 'src\JianghuYouling.Core\Persona\Resources'
    $forbidden = '情欲|性行为|性爱|性交|交合|交媾|做爱|口交|肛交|阴茎|阴道|精液|乳头|下体|性器官|高潮|呻吟|体液|赤裸|裸露|发情|插入|床笫|肉欲'
    $resources = @(Get-ChildItem -LiteralPath $resourceDir -File | Sort-Object Name)
    if ($resources.Count -eq 0) { throw 'Embedded persona resource set is empty' }
    $hits = @($resources | Where-Object {
        try { [IO.File]::ReadAllText($_.FullName) -match $forbidden } catch { $true }
    })
    if ($hits.Count -gt 0) { throw 'Sanitized persona resources still contain forbidden terms' }
    # 发布只消费仓库内已净化、受版本控制并由 special-persona DevTest 验证映射的
    # 嵌入资源。原始创作目录不是构建输入，不能成为换机后的发布前置。
    $hashRows = @($resources | ForEach-Object {
        $_.Name + '|' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    })
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $aggregate = [BitConverter]::ToString($sha.ComputeHash(
            [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $hashRows)))).Replace('-', '')
    }
    finally { $sha.Dispose() }
    Add-Check 'persona-adult-content-scan' 'PASS' (
        "embedded resources clean files={0} aggregate={1}" -f $resources.Count, $aggregate)
}

function Assert-DeploymentHashes {
    $gameMod = Join-Path $GameRoot 'Mod\JianghuYouling'
    $pairs = @(
        @((Join-Path $Root 'src\JianghuYouling.Frontend\bin\Release\JianghuYouling.Frontend.dll'), (Join-Path $gameMod 'Plugins\JianghuYouling.Frontend.dll')),
        @((Join-Path $Root 'src\JianghuYouling.Core\bin\Release\JianghuYouling.Core.dll'), (Join-Path $gameMod 'Plugins\JianghuYouling.Core.dll')),
        @((Join-Path $Root 'src\JianghuYouling.Backend\bin\Release\JianghuYouling.Backend.dll'), (Join-Path $gameMod 'Plugins\JianghuYouling.Backend.dll')),
        @((Join-Path $Root 'deploy\Config.lua'), (Join-Path $gameMod 'Config.lua'))
    )
    foreach ($pair in $pairs) {
        Assert-File 'deployment-source' $pair[0]; Assert-File 'deployment-target' $pair[1]
        if ((Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash) {
            throw "Deployment hash mismatch: $($pair[1])"
        }
    }
    Add-Check 'deployment-hashes' 'PASS' ("files={0}" -f $pairs.Count)
}

try {
    $game = Get-Process -Name 'The Scroll Of Taiwu' -ErrorAction SilentlyContinue
    if ($null -ne $game) { throw 'The Scroll Of Taiwu is running; offline build/deploy gate requires it to be closed' }

    Assert-DecompileAuthority
    Assert-NoSecrets

    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    if (-not $SkipBuild) {
        foreach ($configuration in @('Debug', 'Release')) {
            foreach ($project in @(
                'src\JianghuYouling.Core\JianghuYouling.Core.csproj',
                'src\JianghuYouling.Backend\JianghuYouling.Backend.csproj',
                'src\JianghuYouling.Frontend\JianghuYouling.Frontend.csproj',
                'tools\JianghuYouling.DevTest\JianghuYouling.DevTest.csproj',
                'tools\JianghuYouling.SecretStorageTests\JianghuYouling.SecretStorageTests.csproj',
                'tools\JianghuYouling.TrustBoundaryTests\JianghuYouling.TrustBoundaryTests.csproj',
                'tools\JianghuYouling.BackendContractTests\JianghuYouling.BackendContractTests.csproj')) {
                $label = 'build-' + $configuration.ToLowerInvariant() + '-' + [IO.Path]::GetFileNameWithoutExtension($project)
                Invoke-External $label $dotnet ("build {0} -c {1} --nologo --no-restore -warnaserror" -f (Quote-Arg (Join-Path $Root $project)), $configuration)
            }
        }
    }

    $ps = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    Invoke-External 'regression-checks' $ps ("-NoProfile -ExecutionPolicy Bypass -File {0}" -f (Quote-Arg (Join-Path $Root 'tools\regression-checks.ps1')))
    Invoke-External 'workshop-validator-selftest' $ps ("-NoProfile -ExecutionPolicy Bypass -File {0}" -f (Quote-Arg (Join-Path $Root 'tools\test-workshop-package-validator.ps1')))
    Invoke-External 'frontend-lifecycle-regression' $ps ("-NoProfile -ExecutionPolicy Bypass -File {0}" -f (Quote-Arg (Join-Path $Root 'tools\frontend-lifecycle-regression.ps1')))
    Invoke-External 'real-game-evidence-gate-selftest' $ps ("-NoProfile -ExecutionPolicy Bypass -File {0}" -f (Quote-Arg (Join-Path $Root 'tools\test-real-game-e2e-evidence-gate.ps1')))
    $devTest = Join-Path $Root 'tools\JianghuYouling.DevTest\bin\Release\JianghuYouling.DevTest.exe'
    Assert-File 'devtest-release-binary' $devTest
    Invoke-External 'devtest-provider-only' $devTest '--provider-only'
    Invoke-External 'devtest-tts-dictation-only' $devTest '--tts-dictation-only'
    Invoke-External 'devtest-persistence-only' $devTest '--persistence-only'
    Invoke-External 'devtest-agent-eval-only' $devTest '--agent-eval-only'
    Invoke-External 'devtest-special-persona-only' $devTest '--special-persona-only'
    $secretStorageTest = Join-Path $Root 'tools\JianghuYouling.SecretStorageTests\bin\Release\JianghuYouling.SecretStorageTests.exe'
    Assert-File 'secret-storage-test-release-binary' $secretStorageTest
    Invoke-External 'secret-storage-main-tmp-bak' $secretStorageTest ''
    $backendContractTest = Join-Path $Root 'tools\JianghuYouling.BackendContractTests\bin\Release\net8.0\JianghuYouling.BackendContractTests.exe'
    Assert-File 'backend-contract-test-release-binary' $backendContractTest
    Invoke-External 'backend-start-combat-contracts' $backendContractTest ''
    $trustBoundaryTest = Join-Path $Root 'tools\JianghuYouling.TrustBoundaryTests\bin\Release\JianghuYouling.TrustBoundaryTests.exe'
    Assert-File 'trust-boundary-test-release-binary' $trustBoundaryTest
    Invoke-External 'trust-boundary-tests' $trustBoundaryTest ''
    Invoke-External 'devtest-offline-full' $devTest '--offline'
    if ($OnlineDeepSeek) {
        if (-not [string]::IsNullOrWhiteSpace($DeepSeekProtectedConfig)) {
            Invoke-OnlineWithRetry 'devtest-deepseek-live' $devTest ("--deepseek-protected-config {0}" -f (Quote-Arg $DeepSeekProtectedConfig)) $null
        }
        else {
            $key = [Environment]::GetEnvironmentVariable('DEEPSEEK_API_KEY')
            if ([string]::IsNullOrWhiteSpace($key)) {
                throw 'OnlineDeepSeek requires -DeepSeekProtectedConfig or process-only DEEPSEEK_API_KEY'
            }
            Invoke-OnlineWithRetry 'devtest-deepseek-live' $devTest '--deepseek-env' @{ DEEPSEEK_API_KEY=$key }
        }
    }

    Assert-SanitizedPersonaAssets
    Invoke-External 'git-diff-check' (Get-Command git -ErrorAction Stop).Source 'diff --check'
    if ($TestReleaseCandidate) {
        $validator = Join-Path $Root 'tools\validate-workshop-package.ps1'
        Invoke-External 'workshop-candidate-integrity' $ps (
            ("-NoProfile -ExecutionPolicy Bypass -File {0} -ContentFolder {1} " +
            "-ExpectedFileId {2} -ExpectedTitle {3} -ExpectedHashManifest {4}") -f
            (Quote-Arg $validator),
            (Quote-Arg ([IO.Path]::GetFullPath($WorkshopCandidateFolder))),
            $WorkshopExpectedFileId,
            (Quote-Arg $WorkshopExpectedTitle),
            (Quote-Arg ([IO.Path]::GetFullPath($WorkshopExpectedHashManifest))))
    }
    else {
        Add-Check 'workshop-candidate-integrity' 'SKIP' 'not a test release-candidate run'
    }
    Assert-DeploymentHashes
    Add-Check 'automated-preflight-gate' 'PASS' 'all automated gates completed; real-game E2E is tracked separately'
}
catch {
    Add-Check 'automated-preflight-gate' 'FAIL' $_.Exception.Message
    throw
}
finally {
    $summary = [pscustomobject][ordered]@{
        schemaVersion = 1
        runId = $RunId
        startedUtc = $Started.ToString('o')
        finishedUtc = [DateTime]::UtcNow.ToString('o')
        root = $Root
        gitHead = (& git -C $Root rev-parse HEAD 2>$null)
        checks = $Checks.ToArray()
    }
    [IO.File]::WriteAllText((Join-Path $RunDir 'summary.json'),
        (($summary | ConvertTo-Json -Depth 8) + "`n"), $Utf8NoBom)
    Write-Host ("Automated preflight evidence (not real-game E2E): {0}" -f (Join-Path $RunDir 'summary.json'))
}
