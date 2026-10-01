param(
    [Parameter(Mandatory = $true)][string]$ContentFolder,
    [Parameter(Mandatory = $true)][long]$ExpectedFileId,
    [Parameter(Mandatory = $true)][string]$ExpectedTitle,
    [Parameter(Mandatory = $true)][string]$ExpectedHashManifest
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ContentFolder)
$configPath = Join-Path $root 'Config.lua'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Workshop package is missing Config.lua: $root"
}

$config = [IO.File]::ReadAllText($configPath, [Text.Encoding]::UTF8)

function Find-LuaLongBracketEnd([string]$Text, [int]$Start) {
    if ($Start -lt 0 -or $Start -ge $Text.Length -or $Text[$Start] -ne '[') {
        return -1
    }
    $cursor = $Start + 1
    while ($cursor -lt $Text.Length -and $Text[$cursor] -eq '=') { $cursor++ }
    if ($cursor -ge $Text.Length -or $Text[$cursor] -ne '[') { return -1 }
    $equals = $cursor - $Start - 1
    $closing = ']' + ('=' * $equals) + ']'
    $end = $Text.IndexOf($closing, $cursor + 1, [StringComparison]::Ordinal)
    return $(if ($end -lt 0) { -1 } else { $end + $closing.Length })
}

function Find-LuaQuotedEnd([string]$Text, [int]$Start) {
    $quote = $Text[$Start]
    $cursor = $Start + 1
    while ($cursor -lt $Text.Length) {
        if ($Text[$cursor] -eq [char]92) { $cursor += 2; continue }
        if ($Text[$cursor] -eq $quote) { return $cursor + 1 }
        $cursor++
    }
    return -1
}

function Test-LuaIdentifierStart([char]$Character) {
    return [char]::IsLetter($Character) -or $Character -eq '_'
}

function Test-LuaIdentifierPart([char]$Character) {
    return [char]::IsLetterOrDigit($Character) -or $Character -eq '_'
}

$script:LuaConfigText = $config
$script:LuaConfigCursor = 0
$script:LuaIdentityAssignments = New-Object 'System.Collections.Generic.List[object]'

function Skip-LuaTrivia {
    while ($script:LuaConfigCursor -lt $script:LuaConfigText.Length) {
        $ch = $script:LuaConfigText[$script:LuaConfigCursor]
        if ([char]::IsWhiteSpace($ch)) {
            $script:LuaConfigCursor++
            continue
        }
        if ($ch -eq '-' -and
            $script:LuaConfigCursor + 1 -lt $script:LuaConfigText.Length -and
            $script:LuaConfigText[$script:LuaConfigCursor + 1] -eq '-') {
            $longEnd = Find-LuaLongBracketEnd $script:LuaConfigText (
                $script:LuaConfigCursor + 2)
            if ($longEnd -ge 0) {
                $script:LuaConfigCursor = $longEnd
                continue
            }
            while ($script:LuaConfigCursor -lt $script:LuaConfigText.Length -and
                $script:LuaConfigText[$script:LuaConfigCursor] -ne "`r" -and
                $script:LuaConfigText[$script:LuaConfigCursor] -ne "`n") {
                $script:LuaConfigCursor++
            }
            continue
        }
        break
    }
}

function Read-LuaIdentifier {
    Skip-LuaTrivia
    if ($script:LuaConfigCursor -ge $script:LuaConfigText.Length -or
        -not (Test-LuaIdentifierStart $script:LuaConfigText[$script:LuaConfigCursor])) {
        throw "Config.lua expected an identifier at offset $script:LuaConfigCursor."
    }
    $start = $script:LuaConfigCursor
    $script:LuaConfigCursor++
    while ($script:LuaConfigCursor -lt $script:LuaConfigText.Length -and
        (Test-LuaIdentifierPart $script:LuaConfigText[$script:LuaConfigCursor])) {
        $script:LuaConfigCursor++
    }
    return $script:LuaConfigText.Substring(
        $start, $script:LuaConfigCursor - $start)
}

function Expect-LuaCharacter([char]$Expected) {
    Skip-LuaTrivia
    if ($script:LuaConfigCursor -ge $script:LuaConfigText.Length -or
        $script:LuaConfigText[$script:LuaConfigCursor] -ne $Expected) {
        throw "Config.lua expected '$Expected' at offset $script:LuaConfigCursor."
    }
    $script:LuaConfigCursor++
}

function Read-LuaValue([int]$Depth) {
    Skip-LuaTrivia
    if ($script:LuaConfigCursor -ge $script:LuaConfigText.Length) {
        throw 'Config.lua ended while reading a table value.'
    }
    $start = $script:LuaConfigCursor
    $ch = $script:LuaConfigText[$start]
    if ($ch -eq '"' -or $ch -eq "'") {
        $end = Find-LuaQuotedEnd $script:LuaConfigText $start
        if ($end -lt 0) { throw 'Config.lua contains an unterminated quoted string.' }
        $raw = $script:LuaConfigText.Substring($start + 1, $end - $start - 2)
        $script:LuaConfigCursor = $end
        return [pscustomobject]@{
            Kind = 'string'
            Value = $raw
            Escaped = ($raw.IndexOf([char]92) -ge 0)
        }
    }
    if ($ch -eq '[') {
        $end = Find-LuaLongBracketEnd $script:LuaConfigText $start
        if ($end -ge 0) {
            $script:LuaConfigCursor = $end
            return [pscustomobject]@{
                Kind = 'long-string'
                Value = '<long-string>'
                Escaped = $false
            }
        }
    }
    if ($ch -eq '{') {
        Read-LuaTable ($Depth + 1)
        return [pscustomobject]@{
            Kind = 'table'
            Value = '<table>'
            Escaped = $false
        }
    }
    $remaining = $script:LuaConfigText.Substring($start)
    $number = [Regex]::Match($remaining, '^[+-]?\d+(?:\.\d+)?')
    if ($number.Success) {
        $script:LuaConfigCursor += $number.Length
        return [pscustomobject]@{
            Kind = 'number'
            Value = $number.Value
            Escaped = $false
        }
    }
    if (Test-LuaIdentifierStart $ch) {
        $literal = Read-LuaIdentifier
        if ($literal -ne 'true' -and $literal -ne 'false') {
            throw "Config.lua contains unsupported value '$literal'."
        }
        return [pscustomobject]@{
            Kind = 'boolean'
            Value = $literal
            Escaped = $false
        }
    }
    throw "Config.lua contains unsupported syntax at offset $start."
}

function Read-LuaTable([int]$Depth) {
    Expect-LuaCharacter '{'
    while ($true) {
        Skip-LuaTrivia
        if ($script:LuaConfigCursor -ge $script:LuaConfigText.Length) {
            throw 'Config.lua contains an unterminated table.'
        }
        if ($script:LuaConfigText[$script:LuaConfigCursor] -eq '}') {
            $script:LuaConfigCursor++
            return
        }

        $name = $null
        if (Test-LuaIdentifierStart $script:LuaConfigText[$script:LuaConfigCursor]) {
            $name = Read-LuaIdentifier
        }
        elseif ($script:LuaConfigText[$script:LuaConfigCursor] -eq '[') {
            $script:LuaConfigCursor++
            Skip-LuaTrivia
            $remaining = $script:LuaConfigText.Substring($script:LuaConfigCursor)
            $index = [Regex]::Match($remaining, '^\d+')
            if (-not $index.Success) {
                throw 'Config.lua table indices must be non-negative integer literals.'
            }
            $script:LuaConfigCursor += $index.Length
            Expect-LuaCharacter ']'
            $name = '[' + $index.Value + ']'
        }
        else {
            throw "Config.lua expected a literal table key at offset $script:LuaConfigCursor."
        }

        Expect-LuaCharacter '='
        $value = Read-LuaValue $Depth
        if ($Depth -eq 1 -and $name -in @('Title', 'Source', 'FileId')) {
            [void]$script:LuaIdentityAssignments.Add([pscustomobject]@{
                Name = $name
                Kind = $value.Kind
                Value = $value.Value
                Escaped = $value.Escaped
            })
        }

        Skip-LuaTrivia
        if ($script:LuaConfigCursor -ge $script:LuaConfigText.Length) {
            throw 'Config.lua contains an unterminated table.'
        }
        $delimiter = $script:LuaConfigText[$script:LuaConfigCursor]
        if ($delimiter -eq ',') {
            $script:LuaConfigCursor++
            continue
        }
        if ($delimiter -eq '}') {
            $script:LuaConfigCursor++
            return
        }
        throw "Config.lua permits only literal table fields; unexpected '$delimiter' at offset $script:LuaConfigCursor."
    }
}

Skip-LuaTrivia
if ((Read-LuaIdentifier) -cne 'return') {
    throw 'Config.lua must consist of one literal return table.'
}
Read-LuaTable 1
Skip-LuaTrivia
if ($script:LuaConfigCursor -ne $script:LuaConfigText.Length) {
    throw 'Config.lua contains executable syntax outside its literal return table.'
}

$assignments = @($script:LuaIdentityAssignments | ForEach-Object { $_ })
function Assert-UniqueLuaValue(
    [string]$Name,
    [string]$ExpectedKind,
    [string]$Expected,
    [string]$Failure) {
    $matches = @($assignments | Where-Object Name -eq $Name)
    if ($matches.Count -ne 1 -or
        [string]$matches[0].Kind -cne $ExpectedKind -or
        [bool]$matches[0].Escaped -or
        [string]$matches[0].Value -cne $Expected) {
        throw $Failure
    }
}
Assert-UniqueLuaValue 'Title' 'string' $ExpectedTitle "Workshop title mismatch or duplicate; expected '$ExpectedTitle'."
Assert-UniqueLuaValue 'Source' 'number' '1' 'Workshop Source must be the unique top-level literal value 1.'
Assert-UniqueLuaValue 'FileId' 'number' ([string]$ExpectedFileId) "Workshop FileId mismatch or duplicate; expected $ExpectedFileId."

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
$expectedSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in $expectedFiles) { [void]$expectedSet.Add($relative) }

$rootPrefix = $root.TrimEnd([char[]]@([char]92, [char]47)) +
    [IO.Path]::DirectorySeparatorChar
$actualFiles = New-Object 'System.Collections.Generic.List[string]'
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
    $full = [IO.Path]::GetFullPath($file.FullName)
    if (-not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Workshop package enumeration escaped its root: $full"
    }
    $relative = $full.Substring($rootPrefix.Length).Replace([char]92, [char]47)
    [void]$actualFiles.Add($relative)
    if (-not $expectedSet.Contains($relative)) {
        throw "Workshop package contains undeclared file: $relative"
    }
}
foreach ($relative in $expectedFiles) {
    $path = Join-Path $root (
        $relative.Replace([char]47, [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Workshop package is missing $relative."
    }
}
if ($actualFiles.Count -ne $expectedFiles.Count) {
    throw "Workshop package file count mismatch: expected=$($expectedFiles.Count) actual=$($actualFiles.Count)"
}

$manifestPath = [IO.Path]::GetFullPath($ExpectedHashManifest)
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Workshop hash manifest is missing: $manifestPath"
}
$hashManifest = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8) |
    ConvertFrom-Json
$properties = @($hashManifest.PSObject.Properties)
if ($properties.Count -ne $expectedFiles.Count) {
    throw "Workshop hash manifest count mismatch: expected=$($expectedFiles.Count) actual=$($properties.Count)"
}
foreach ($relative in $expectedFiles) {
    $property = $hashManifest.PSObject.Properties[$relative]
    if ($null -eq $property -or [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        throw "Workshop hash manifest is missing $relative."
    }
    $path = Join-Path $root (
        $relative.Replace([char]47, [IO.Path]::DirectorySeparatorChar))
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if (-not $actualHash.Equals(([string]$property.Value).Trim(),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Workshop artifact hash mismatch: $relative"
    }
}

Write-Output "Workshop package validated: title='$ExpectedTitle' fileId=$ExpectedFileId source=1 files=$($actualFiles.Count)"
