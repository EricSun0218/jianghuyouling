Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:E2eBackupManifestKind = 'jianghu-youling-save-backup-manifest'
$script:E2eRestoreReportKind = 'jianghu-youling-save-restore-verification'
$script:E2eSecretReportKind = 'jianghu-youling-secret-scan-report'
$script:E2eShaPattern = '^[A-Fa-f0-9]{64}$'
$script:E2eGenericSecretPattern = '(?i)(sk-[A-Za-z0-9_-]{16,}|Bearer\s+[A-Za-z0-9._~+/-]{20,}|api[_-]?key["'']?\s*[:=]\s*["''][^"'']{12,}["''])'
$script:E2eUtf8NoBom = New-Object Text.UTF8Encoding($false)
$script:E2eStrictUtf8 = New-Object Text.UTF8Encoding($false, $true)
Add-Type -AssemblyName System.Security -ErrorAction Stop

function Get-E2eFullPath([string]$Path, [string]$Where) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Where is empty" }
    try { return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') }
    catch { throw "$Where is not a valid absolute filesystem path" }
}

function Test-E2ePathWithin([string]$Candidate, [string]$Parent, [switch]$AllowEqual) {
    $candidateFull = Get-E2eFullPath $Candidate 'candidate path'
    $parentFull = Get-E2eFullPath $Parent 'parent path'
    if ($candidateFull.Equals($parentFull, [StringComparison]::OrdinalIgnoreCase)) {
        return [bool]$AllowEqual
    }
    $prefix = $parentFull + [IO.Path]::DirectorySeparatorChar
    return $candidateFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-E2ePathChainHasNoReparsePoint([string]$Path, [string]$Where) {
    $current = Get-E2eFullPath $Path $Where
    while (-not [string]::IsNullOrWhiteSpace($current)) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "$Where contains a reparse point"
            }
        }
        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Assert-E2eGameProcessesClosed {
    foreach ($processName in @('The Scroll Of Taiwu', 'GameData')) {
        if ($null -ne (Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
            throw "Taiwu frontend/backend processes must both be closed"
        }
    }
}

function Assert-E2eSafeRelativePath([string]$Path, [string]$Where) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path)) {
        throw "$Where must be a non-empty relative path"
    }
    if ($Path.IndexOf([char]0) -ge 0) { throw "$Where contains an invalid character" }
    $normalized = $Path.Replace('\', '/')
    if ($normalized.StartsWith('/') -or $normalized.EndsWith('/')) {
        throw "$Where is not canonical"
    }
    foreach ($segment in $normalized.Split('/')) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment -eq '.' -or $segment -eq '..') {
            throw "$Where contains an unsafe path segment"
        }
        if ($segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            throw "$Where contains an invalid path segment"
        }
    }
    return $normalized
}

function Get-E2eSha256ForText([string]$Text) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($script:E2eUtf8NoBom.GetBytes($Text)))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-E2eTreeSnapshot([string]$RootPath) {
    $root = Get-E2eFullPath $RootPath 'tree root'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Tree root is missing' }
    Assert-E2ePathChainHasNoReparsePoint $root 'tree root'

    $files = New-Object 'System.Collections.Generic.List[object]'
    $directories = New-Object 'System.Collections.Generic.List[string]'
    $queue = New-Object 'System.Collections.Generic.Queue[string]'
    $queue.Enqueue($root)
    while ($queue.Count -gt 0) {
        $directory = $queue.Dequeue()
        foreach ($item in @(Get-ChildItem -LiteralPath $directory -Force | Sort-Object Name)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Tree contains a reparse point'
            }
            $relative = $item.FullName.Substring($root.Length).TrimStart('\', '/').Replace('\', '/')
            $relative = Assert-E2eSafeRelativePath $relative 'tree relative path'
            if ($item.PSIsContainer) {
                $directories.Add($relative)
                $queue.Enqueue($item.FullName)
            }
            else {
                $files.Add([pscustomobject][ordered]@{
                    relativePath = $relative
                    length = [long]$item.Length
                    sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
                })
            }
        }
    }

    $sortedFiles = @($files.ToArray() | Sort-Object relativePath)
    $sortedDirectories = @($directories.ToArray() | Sort-Object)
    $canonicalRows = New-Object 'System.Collections.Generic.List[string]'
    foreach ($relative in $sortedDirectories) { $canonicalRows.Add('D|' + $relative) }
    $totalBytes = [long]0
    foreach ($row in $sortedFiles) {
        $totalBytes += [long]$row.length
        $canonicalRows.Add(('F|{0}|{1}|{2}' -f $row.relativePath, $row.length, $row.sha256))
    }
    return [pscustomobject][ordered]@{
        files = $sortedFiles
        directories = $sortedDirectories
        fileCount = $sortedFiles.Count
        directoryCount = $sortedDirectories.Count
        totalBytes = $totalBytes
        treeSha256 = Get-E2eSha256ForText ([string]::Join("`n", $canonicalRows.ToArray()))
    }
}

function Get-E2eManifestSnapshot([object]$RootRow, [string]$Where) {
    $fileRows = @($RootRow.files)
    $directoryRows = @($RootRow.directories)
    $files = New-Object 'System.Collections.Generic.List[object]'
    $directories = New-Object 'System.Collections.Generic.List[string]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($relativeValue in $directoryRows) {
        $relative = Assert-E2eSafeRelativePath ([string]$relativeValue) "$Where directory"
        if (-not $seen.Add($relative)) { throw "$Where has a duplicate directory row" }
        $directories.Add($relative)
    }
    foreach ($row in $fileRows) {
        if ($null -eq $row) { throw "$Where contains a null file row" }
        $relative = Assert-E2eSafeRelativePath ([string]$row.relativePath) "$Where file"
        if (-not $seen.Add($relative)) { throw "$Where has a duplicate file row" }
        $length = [long]$row.length
        if ($length -lt 0) { throw "$Where contains a negative file length" }
        $sha256 = ([string]$row.sha256).Trim()
        if ($sha256 -cnotmatch $script:E2eShaPattern) { throw "$Where contains an invalid file SHA-256" }
        $files.Add([pscustomobject][ordered]@{
            relativePath = $relative
            length = $length
            sha256 = $sha256.ToUpperInvariant()
        })
    }
    $canonicalRows = New-Object 'System.Collections.Generic.List[string]'
    $sortedDirectories = @($directories.ToArray() | Sort-Object)
    $sortedFiles = @($files.ToArray() | Sort-Object relativePath)
    foreach ($relative in $sortedDirectories) { $canonicalRows.Add('D|' + $relative) }
    $totalBytes = [long]0
    foreach ($row in $sortedFiles) {
        $totalBytes += [long]$row.length
        $canonicalRows.Add(('F|{0}|{1}|{2}' -f $row.relativePath, $row.length, $row.sha256))
    }
    $snapshot = [pscustomobject][ordered]@{
        files = $sortedFiles
        directories = $sortedDirectories
        fileCount = $sortedFiles.Count
        directoryCount = $sortedDirectories.Count
        totalBytes = $totalBytes
        treeSha256 = Get-E2eSha256ForText ([string]::Join("`n", $canonicalRows.ToArray()))
    }
    foreach ($pair in @(
        @('fileCount', $snapshot.fileCount),
        @('directoryCount', $snapshot.directoryCount),
        @('totalBytes', $snapshot.totalBytes),
        @('treeSha256', $snapshot.treeSha256))) {
        $property = $RootRow.PSObject.Properties[$pair[0]]
        if ($null -eq $property -or [string]$property.Value -cne [string]$pair[1]) {
            throw "$Where aggregate metadata does not match its file rows"
        }
    }
    return $snapshot
}

function Assert-E2eSnapshotsEqual([object]$Actual, [object]$Expected, [string]$Where) {
    foreach ($name in @('fileCount', 'directoryCount', 'totalBytes', 'treeSha256')) {
        if ([string]$Actual.$name -cne [string]$Expected.$name) { throw "$Where $name mismatch" }
    }
    $actualFiles = @($Actual.files)
    $expectedFiles = @($Expected.files)
    for ($index = 0; $index -lt $actualFiles.Count; $index++) {
        $a = $actualFiles[$index]; $e = $expectedFiles[$index]
        if ([string]$a.relativePath -cne [string]$e.relativePath -or
            [long]$a.length -ne [long]$e.length -or
            [string]$a.sha256 -cne [string]$e.sha256) {
            throw "$Where file row mismatch"
        }
    }
    $actualDirectories = @($Actual.directories)
    $expectedDirectories = @($Expected.directories)
    for ($index = 0; $index -lt $actualDirectories.Count; $index++) {
        if ([string]$actualDirectories[$index] -cne [string]$expectedDirectories[$index]) {
            throw "$Where directory row mismatch"
        }
    }
}

function Copy-E2eSnapshotToRoot([object]$Snapshot, [string]$SourceRoot, [string]$DestinationRoot) {
    [IO.Directory]::CreateDirectory($DestinationRoot) | Out-Null
    foreach ($relative in @($Snapshot.directories)) {
        $safe = Assert-E2eSafeRelativePath ([string]$relative) 'snapshot directory'
        $target = [IO.Path]::GetFullPath((Join-Path $DestinationRoot $safe))
        if (-not (Test-E2ePathWithin $target $DestinationRoot)) { throw 'Snapshot directory escapes destination root' }
        [IO.Directory]::CreateDirectory($target) | Out-Null
        Assert-E2ePathChainHasNoReparsePoint $target 'snapshot destination directory'
    }
    foreach ($row in @($Snapshot.files)) {
        $safe = Assert-E2eSafeRelativePath ([string]$row.relativePath) 'snapshot file'
        $source = [IO.Path]::GetFullPath((Join-Path $SourceRoot $safe))
        $target = [IO.Path]::GetFullPath((Join-Path $DestinationRoot $safe))
        if (-not (Test-E2ePathWithin $source $SourceRoot) -or
            -not (Test-E2ePathWithin $target $DestinationRoot)) {
            throw 'Snapshot file escapes an explicit root'
        }
        Copy-E2eFileAtomic $source $target
    }
}

function Copy-E2eFileAtomic([string]$Source, [string]$Destination) {
    $sourceFull = Get-E2eFullPath $Source 'copy source'
    $destinationFull = [IO.Path]::GetFullPath($Destination)
    if (-not (Test-Path -LiteralPath $sourceFull -PathType Leaf)) { throw 'Copy source file is missing' }
    Assert-E2ePathChainHasNoReparsePoint $sourceFull 'copy source'
    if (Test-Path -LiteralPath $destinationFull) { throw 'Copy destination already exists' }
    $parent = Split-Path -Parent $destinationFull
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    Assert-E2ePathChainHasNoReparsePoint $parent 'copy destination parent'
    $temporary = $destinationFull + '.jyl-copy-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $input = New-Object IO.FileStream($sourceFull, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            [IO.FileShare]::Read, 131072, [IO.FileOptions]::SequentialScan)
        try {
            $output = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
                [IO.FileShare]::None, 131072, [IO.FileOptions]::WriteThrough)
            try { $input.CopyTo($output); $output.Flush($true) }
            finally { $output.Dispose() }
        }
        finally { $input.Dispose() }
        $sourceInfo = Get-Item -LiteralPath $sourceFull
        $temporaryInfo = Get-Item -LiteralPath $temporary
        if ($sourceInfo.Length -ne $temporaryInfo.Length -or
            (Get-FileHash -LiteralPath $sourceFull -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash) {
            throw 'Atomic copy readback mismatch'
        }
        [IO.File]::Move($temporary, $destinationFull)
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Write-E2eJsonAtomic([string]$Path, [object]$Value) {
    $full = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $full) { throw 'Evidence output already exists' }
    $parent = Split-Path -Parent $full
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    Assert-E2ePathChainHasNoReparsePoint $parent 'evidence output parent'
    $temporary = $full + '.jyl-write-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $json = ($Value | ConvertTo-Json -Depth 40) + "`n"
        $bytes = $script:E2eUtf8NoBom.GetBytes($json)
        $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
            [IO.FileShare]::None, 65536, [IO.FileOptions]::WriteThrough)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        [IO.File]::Move($temporary, $full)
        if ((Get-Item -LiteralPath $full).Length -ne $bytes.Length) { throw 'Evidence output readback mismatch' }
        $expectedHash = [Security.Cryptography.SHA256]::Create()
        try { $expected = ([BitConverter]::ToString($expectedHash.ComputeHash($bytes))).Replace('-', '') }
        finally { $expectedHash.Dispose() }
        if ((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash -cne $expected) {
            throw 'Evidence output readback mismatch'
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Remove-E2eContainedTree([string]$Path, [string]$AllowedParent, [string]$ExpectedLeaf) {
    $full = Get-E2eFullPath $Path 'cleanup target'
    $parent = Get-E2eFullPath $AllowedParent 'cleanup parent'
    if (-not (Test-E2ePathWithin $full $parent)) { throw 'Cleanup target escapes its explicit parent' }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedLeaf) -and
        [IO.Path]::GetFileName($full) -cne $ExpectedLeaf) { throw 'Cleanup target has an unexpected leaf name' }
    if (-not (Test-Path -LiteralPath $full)) { return }
    [void](Get-E2eTreeSnapshot $full)
    Remove-Item -LiteralPath $full -Recurse -Force
    if (Test-Path -LiteralPath $full) { throw 'Contained cleanup did not remove its target' }
}

# Windows PowerShell 5.1 does not consistently preload System.Security on a
# fresh process.  The evidence helpers use CurrentUser DPAPI, so load the
# framework assembly explicitly before any ProtectedData call.
Add-Type -AssemblyName System.Security

function Get-E2eOrphanTemporaryFiles([string[]]$Roots) {
    $rows = New-Object 'System.Collections.Generic.List[string]'
    foreach ($rootValue in @($Roots)) {
        if ([string]::IsNullOrWhiteSpace($rootValue)) { continue }
        $root = Get-E2eFullPath $rootValue 'temporary scan root'
        if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
        Assert-E2ePathChainHasNoReparsePoint $root 'temporary scan root'
        [void](Get-E2eTreeSnapshot $root)
        foreach ($item in @(Get-ChildItem -LiteralPath $root -Recurse -Force)) {
            if ($item.Name -match '(?i)(\.jyl-(?:copy|write|restore|testdata)-|\.migration\.tmp$)') {
                $rows.Add($item.FullName)
            }
        }
    }
    return $rows.ToArray()
}

function Get-E2eConfiguredSecrets([string]$SettingsRoot) {
    $root = Get-E2eFullPath $SettingsRoot 'settings root'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Settings root is missing' }
    if ([IO.Path]::GetFileName($root) -cne 'Settings') { throw 'Configured-secret root must be the Mod Settings directory' }
    Assert-E2ePathChainHasNoReparsePoint $root 'settings root'
    $secrets = New-Object 'System.Collections.Generic.List[string]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    $sources = New-Object 'System.Collections.Generic.List[object]'
    $replicaCount = 0
    $entropy = [Text.Encoding]::UTF8.GetBytes('JianghuYouling.LocalSecret.v1')
    foreach ($fileName in @('llm.json', 'tts.json', 'minimax.json')) {
        $fileReplicaCount = 0
        $fileSecretCount = 0
        foreach ($suffix in @('', '.bak', '.tmp')) {
            $path = Join-Path $root ($fileName + $suffix)
            if (-not (Test-Path -LiteralPath $path)) { continue }
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Protected-config replica is not a file' }
            Assert-E2ePathChainHasNoReparsePoint $path 'protected-config replica'
            $info = Get-Item -LiteralPath $path
            if ($info.Length -lt 2 -or $info.Length -gt 1MB) { throw 'Protected-config replica size is invalid' }
            try {
                $bytes = [IO.File]::ReadAllBytes($path)
                $text = $script:E2eStrictUtf8.GetString($bytes)
                [void]($text | ConvertFrom-Json)
            }
            catch { throw 'Protected-config replica is not strict UTF-8 JSON' }
            if ([regex]::Matches($text, '"apiKey"\s*:').Count -ne 0) {
                throw 'Protected-config replica contains forbidden plaintext credential storage'
            }
            $fieldCount = [regex]::Matches($text, '"apiKeyProtected"\s*:').Count
            $matches = [regex]::Matches($text, '"apiKeyProtected"\s*:\s*"(?<value>[A-Za-z0-9+/=]+)"')
            if ($fieldCount -gt 1 -or $fieldCount -ne $matches.Count) {
                throw 'Protected-config replica has ambiguous protected credential metadata'
            }
            $replicaCount++
            $fileReplicaCount++
            if ($matches.Count -eq 0) { continue }
            $cipher = $null
            $clear = $null
            try {
                $cipher = [Convert]::FromBase64String($matches[0].Groups['value'].Value)
                $clear = [System.Security.Cryptography.ProtectedData]::Unprotect(
                    $cipher, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
                $secret = $script:E2eStrictUtf8.GetString($clear).Trim()
                if ($secret.Length -lt 4 -or $secret.Length -gt 16384) {
                    throw 'Configured credential length cannot be scanned safely'
                }
                if ($seen.Add($secret)) { $secrets.Add($secret) }
                $fileSecretCount++
            }
            catch { throw 'Protected-config credential cannot be safely decrypted by the current user' }
            finally {
                if ($null -ne $cipher) { [Array]::Clear($cipher, 0, $cipher.Length) }
                if ($null -ne $clear) { [Array]::Clear($clear, 0, $clear.Length) }
            }
        }
        $sources.Add([pscustomobject][ordered]@{
            configFile = $fileName
            replicaCount = $fileReplicaCount
            decryptedCredentialValues = $fileSecretCount
        })
    }
    return [pscustomobject][ordered]@{
        values = $secrets.ToArray()
        exactSecretCount = $secrets.Count
        protectedConfigReplicaCount = $replicaCount
        sources = $sources.ToArray()
    }
}

function Test-E2eByteSequence([byte[]]$Haystack, [byte[]]$Needle) {
    if ($null -eq $Needle -or $Needle.Length -eq 0 -or $Haystack.Length -lt $Needle.Length) { return $false }
    $limit = $Haystack.Length - $Needle.Length
    for ($offset = 0; $offset -le $limit; $offset++) {
        if ($Haystack[$offset] -ne $Needle[0]) { continue }
        $equal = $true
        for ($index = 1; $index -lt $Needle.Length; $index++) {
            if ($Haystack[$offset + $index] -ne $Needle[$index]) { $equal = $false; break }
        }
        if ($equal) { return $true }
    }
    return $false
}

function Test-E2eBytesContainSecret([byte[]]$Bytes, [string[]]$ExactSecrets) {
    foreach ($secret in @($ExactSecrets)) {
        $secretBytes = $script:E2eUtf8NoBom.GetBytes($secret)
        try { if (Test-E2eByteSequence $Bytes $secretBytes) { return $true } }
        finally { [Array]::Clear($secretBytes, 0, $secretBytes.Length) }
    }
    $text = [Text.Encoding]::ASCII.GetString($Bytes)
    return [regex]::IsMatch($text, $script:E2eGenericSecretPattern,
        [Text.RegularExpressions.RegexOptions]::CultureInvariant, [TimeSpan]::FromSeconds(2))
}

function Get-E2eFileSetSecretScope([string]$Name, [object[]]$FileRows, [string[]]$ExactSecrets,
    [string[]]$Roots) {
    $rows = New-Object 'System.Collections.Generic.List[object]'
    $canonical = New-Object 'System.Collections.Generic.List[string]'
    $findings = New-Object 'System.Collections.Generic.List[string]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $totalBytes = [long]0
    foreach ($inputRow in @($FileRows)) {
        $identity = ([string]$inputRow.identity).Trim().Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($identity) -or -not $seen.Add($identity)) {
            throw "Secret scan scope $Name has a duplicate or empty file identity"
        }
        $path = Get-E2eFullPath ([string]$inputRow.path) "secret scan scope $Name file"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Secret scan scope $Name file is missing" }
        Assert-E2ePathChainHasNoReparsePoint $path "secret scan scope $Name file"
        $info = Get-Item -LiteralPath $path
        if ($info.Length -gt 64MB) { throw "Secret scan scope $Name contains a file above the bounded 64 MiB scanner limit" }
        $bytes = [IO.File]::ReadAllBytes($path)
        try {
            if (Test-E2eBytesContainSecret $bytes $ExactSecrets) { $findings.Add($identity) }
        }
        finally { [Array]::Clear($bytes, 0, $bytes.Length) }
        $sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $totalBytes += [long]$info.Length
        $rows.Add([pscustomobject][ordered]@{
            identity = $identity
            path = $path
            length = [long]$info.Length
            sha256 = $sha256
        })
        $canonical.Add(('{0}|{1}|{2}' -f $identity, $info.Length, $sha256))
    }
    $canonicalRows = $canonical.ToArray()
    [Array]::Sort($canonicalRows, [StringComparer]::Ordinal)
    return [pscustomobject][ordered]@{
        name = $Name
        status = if ($findings.Count -eq 0) { 'PASS' } else { 'FAIL' }
        sourceKind = 'file-set'
        roots = @($Roots | ForEach-Object { Get-E2eFullPath $_ "secret scan scope $Name root" })
        fileCount = $rows.Count
        totalBytes = $totalBytes
        snapshotSha256 = Get-E2eSha256ForText ([string]::Join("`n", $canonicalRows))
        findings = $findings.Count
        files = $rows.ToArray()
    }
}

function Get-E2eRecursiveFileRows([string]$Root, [string]$IdentityPrefix) {
    $rootFull = Get-E2eFullPath $Root 'secret scan root'
    $snapshot = Get-E2eTreeSnapshot $rootFull
    return @($snapshot.files | ForEach-Object {
        $identity = if ([string]::IsNullOrWhiteSpace($IdentityPrefix)) {
            [string]$_.relativePath
        } else {
            $IdentityPrefix.TrimEnd('/') + '/' + [string]$_.relativePath
        }
        [pscustomobject][ordered]@{ identity = $identity; path = Join-Path $rootFull $_.relativePath }
    })
}

function Get-E2eRepositoryFileRows([string]$RepositoryRoot) {
    $root = Get-E2eFullPath $RepositoryRoot 'repository root'
    $git = (Get-Command git -ErrorAction Stop).Source
    $relativeRows = @(& $git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate repository files for secret scanning' }
    $rows = New-Object 'System.Collections.Generic.List[object]'
    foreach ($relativeValue in @($relativeRows | Sort-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace([string]$relativeValue)) { continue }
        $relative = ([string]$relativeValue).Replace('\', '/')
        $full = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not (Test-E2ePathWithin $full $root)) { throw 'Git returned a repository path outside its root' }
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            $rows.Add([pscustomobject][ordered]@{ identity = $relative; path = $full })
        }
    }
    return $rows.ToArray()
}

function Read-E2eAsciiLine([IO.Stream]$Stream) {
    $bytes = New-Object 'System.Collections.Generic.List[byte]'
    while ($true) {
        $value = $Stream.ReadByte()
        if ($value -lt 0) {
            if ($bytes.Count -eq 0) { return $null }
            break
        }
        if ($value -eq 10) { break }
        if ($value -ne 13) { $bytes.Add([byte]$value) }
        if ($bytes.Count -gt 4096) { throw 'Git batch protocol returned an oversized header' }
    }
    return [Text.Encoding]::ASCII.GetString($bytes.ToArray())
}

function Write-E2eAsciiLine([IO.Stream]$Stream, [string]$Value) {
    if ($Value -notmatch '^[A-Za-z0-9 ._:/-]+$') { throw 'Git batch input contains non-ASCII protocol characters' }
    $bytes = [Text.Encoding]::ASCII.GetBytes($Value + "`n")
    try { $Stream.Write($bytes, 0, $bytes.Length); $Stream.Flush() }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
}

function New-E2eGitProcess([string]$Git, [string]$Root, [string]$Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Git
    $start.Arguments = $Arguments
    $start.WorkingDirectory = $Root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::ASCII
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    return [Diagnostics.Process]::Start($start)
}

function Get-E2eGitHistorySecretScope([string]$RepositoryRoot, [string[]]$ExactSecrets) {
    $root = Get-E2eFullPath $RepositoryRoot 'repository root'
    $git = (Get-Command git -ErrorAction Stop).Source
    $objectRows = @(& $git -C $root rev-list --objects --all)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate Git history objects for secret scanning' }
    $objectIds = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($row in $objectRows) {
        $match = [regex]::Match([string]$row, '^([A-Fa-f0-9]{40,64})(?:\s|$)')
        if ($match.Success) { [void]$objectIds.Add($match.Groups[1].Value.ToLowerInvariant()) }
    }
    $check = New-E2eGitProcess $git $root 'cat-file --batch-check'
    $blobs = New-Object 'System.Collections.Generic.List[object]'
    try {
        # Windows PowerShell 5.1 constructs redirected StandardInput with a UTF-8
        # BOM. Consume that one-time preamble with an impossible object id before
        # any authoritative object is queried.
        Write-E2eAsciiLine $check.StandardInput.BaseStream ('0' * 40)
        $warmup = $check.StandardOutput.ReadLine()
        if ([string]::IsNullOrWhiteSpace($warmup) -or $warmup -notmatch ' missing$') {
            throw 'Git batch-check warmup framing failed'
        }
        foreach ($oid in @($objectIds | Sort-Object)) {
            Write-E2eAsciiLine $check.StandardInput.BaseStream $oid
            $line = $check.StandardOutput.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) { throw 'Git batch-check returned an empty response' }
            $parts = $line.Split(' ')
            if ($parts.Length -ge 3 -and $parts[1] -eq 'blob') {
                $size = [long]$parts[2]
                if ($size -lt 0 -or $size -gt 64MB) { throw 'Git history contains a blob above the bounded 64 MiB scanner limit' }
                $blobs.Add([pscustomobject][ordered]@{ oid = $parts[0].ToLowerInvariant(); length = $size })
            }
        }
        $check.StandardInput.BaseStream.Close()
        if (-not $check.WaitForExit(30000) -or $check.ExitCode -ne 0) { throw 'Git batch-check failed' }
    }
    finally { if (-not $check.HasExited) { try { $check.Kill() } catch { } }; $check.Dispose() }

    $batch = New-E2eGitProcess $git $root 'cat-file --batch'
    $findings = 0
    $totalBytes = [long]0
    $canonical = New-Object 'System.Collections.Generic.List[string]'
    try {
        Write-E2eAsciiLine $batch.StandardInput.BaseStream ('0' * 40)
        $warmup = Read-E2eAsciiLine $batch.StandardOutput.BaseStream
        if ([string]::IsNullOrWhiteSpace($warmup) -or $warmup -notmatch ' missing$') {
            throw 'Git batch warmup framing failed'
        }
        foreach ($blob in @($blobs.ToArray() | Sort-Object oid)) {
            Write-E2eAsciiLine $batch.StandardInput.BaseStream $blob.oid
            $header = Read-E2eAsciiLine $batch.StandardOutput.BaseStream
            $expectedHeader = $blob.oid + ' blob ' + $blob.length
            if ($header -cne $expectedHeader) { throw 'Git batch returned unexpected blob metadata' }
            $bytes = New-Object byte[] ([int]$blob.length)
            $offset = 0
            while ($offset -lt $bytes.Length) {
                $read = $batch.StandardOutput.BaseStream.Read($bytes, $offset, $bytes.Length - $offset)
                if ($read -le 0) { throw 'Git batch ended inside a blob' }
                $offset += $read
            }
            if ($batch.StandardOutput.BaseStream.ReadByte() -ne 10) { throw 'Git batch blob framing is invalid' }
            try { if (Test-E2eBytesContainSecret $bytes $ExactSecrets) { $findings++ } }
            finally { [Array]::Clear($bytes, 0, $bytes.Length) }
            $totalBytes += [long]$blob.length
            $canonical.Add(($blob.oid + '|' + $blob.length))
        }
        $batch.StandardInput.BaseStream.Close()
        if (-not $batch.WaitForExit(30000) -or $batch.ExitCode -ne 0) { throw 'Git batch failed' }
    }
    finally { if (-not $batch.HasExited) { try { $batch.Kill() } catch { } }; $batch.Dispose() }
    return [pscustomobject][ordered]@{
        name = 'git-history'
        status = if ($findings -eq 0) { 'PASS' } else { 'FAIL' }
        sourceKind = 'git-object-database'
        roots = @($root)
        fileCount = $blobs.Count
        totalBytes = $totalBytes
        snapshotSha256 = Get-E2eSha256ForText ([string]::Join("`n", $canonical.ToArray()))
        findings = $findings
        gitObjectCount = $objectIds.Count
        blobs = @($blobs.ToArray() | Sort-Object oid)
    }
}
