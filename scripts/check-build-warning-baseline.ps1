[CmdletBinding()]
param(
    [string]$BaselineFile,

    [string]$GxPath,

    [string]$DocumentationFile,

    [switch]$UpdateBaseline,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts\gx-version-catalog.ps1')
$gxCatalog = Get-GxVersionCatalog -Root $root

function Fail-Baseline([string]$Message) {
    Write-Error "Warning baseline failed: $Message"
    exit 1
}

if ([string]::IsNullOrWhiteSpace($BaselineFile)) {
    $BaselineFile = Join-Path $root 'docs\build_warning_baseline.json'
}
$BaselineFile = [System.IO.Path]::GetFullPath($BaselineFile)
if ([string]::IsNullOrWhiteSpace($DocumentationFile)) {
    $DocumentationFile = Join-Path $root 'docs\build_warning_baseline.md'
}
$DocumentationFile = [System.IO.Path]::GetFullPath($DocumentationFile)

function Get-Baseline([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail-Baseline "Baseline manifest not found: $Path"
    }
    try {
        $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    } catch {
        Fail-Baseline "Baseline manifest is not valid JSON: $Path"
    }
    if ($manifest.schemaVersion -ne 1) {
        Fail-Baseline "Unsupported baseline schemaVersion '$($manifest.schemaVersion)'; expected 1."
    }
    if ([string]$manifest.generatedAt -notmatch '^\d{4}-\d{2}-\d{2}$') {
        Fail-Baseline 'generatedAt is required in YYYY-MM-DD form.'
    }
    $entries = @($manifest.warnings)
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) {
        if ([string]::IsNullOrWhiteSpace($entry.code) -or
            [string]::IsNullOrWhiteSpace($entry.file) -or
            $null -eq $entry.line) {
            Fail-Baseline "Every warning entry must contain code, file, and line."
        }
        $key = '{0}|{1}|{2}' -f $entry.code, $entry.file, $entry.line
        if (-not $seen.Add($key)) {
            Fail-Baseline "Duplicate warning entry: $key"
        }
    }
    if ($null -eq $manifest.warningCount) {
        Fail-Baseline 'warningCount is required and must match the warning entries.'
    }
    if ([int]$manifest.warningCount -ne $entries.Count) {
        Fail-Baseline "warningCount=$($manifest.warningCount) does not match the $($entries.Count) warning entries."
    }
    return $manifest
}

function ConvertTo-LfText([string]$Text) {
    if ($null -eq $Text) { return '' }
    return ($Text -replace "`r`n", "`n") -replace "`r", "`n"
}

function Get-WarningProjectLabel([string]$File) {
    if ($File -eq '<global>') { return 'Global' }
    if ($File -match '^src/GxMcp\.Gateway\.Tests/') { return 'GxMcp.Gateway.Tests' }
    if ($File -match '^src/GxMcp\.Gateway/') { return 'GxMcp.Gateway' }
    if ($File -match '^src/GxMcp\.Worker\.Tests/') { return 'GxMcp.Worker.Tests' }
    if ($File -match '^src/GxMcp\.Worker/') { return 'GxMcp.Worker' }
    return 'Other'
}

function Get-WarningCodeLabel([string]$Code) {
    $known = @('CS8600', 'CS8602', 'CS8603', 'CS8604', 'CS8605', 'CS8618', 'CS8620', 'CS8625')
    if ($known -contains $Code.ToUpperInvariant()) { return $Code.ToUpperInvariant() }
    return 'Other'
}

function Get-GeneratedWarningBaselineBlock {
    param([Parameter(Mandatory = $true)][object]$Manifest)

    $columns = @('CS8600', 'CS8602', 'CS8603', 'CS8604', 'CS8605', 'CS8618', 'CS8620', 'CS8625', 'Other')
    $rows = [ordered]@{}
    foreach ($entry in @($Manifest.warnings)) {
        $project = Get-WarningProjectLabel -File ([string]$entry.file)
        $code = Get-WarningCodeLabel -Code ([string]$entry.code)
        if (-not $rows.Contains($project)) {
            $rows[$project] = [ordered]@{}
            foreach ($column in $columns) { $rows[$project][$column] = 0 }
        }
        $rows[$project][$code]++
    }

    $projectOrder = @('GxMcp.Gateway', 'GxMcp.Gateway.Tests', 'GxMcp.Worker', 'GxMcp.Worker.Tests', 'Other', 'Global')
    $table = [System.Collections.Generic.List[string]]::new()
    $table.Add('| Project | CS8600 | CS8602 | CS8603 | CS8604 | CS8605 | CS8618 | CS8620 | CS8625 | Other | Total |')
    $table.Add('| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |')
    $totals = [ordered]@{}
    foreach ($column in $columns) { $totals[$column] = 0 }
    $grandTotal = 0
    foreach ($project in $projectOrder) {
        if (-not $rows.Contains($project)) { continue }
        $row = $rows[$project]
        $rowTotal = 0
        $values = foreach ($column in $columns) {
            $value = [int]$row[$column]
            $rowTotal += $value
            $totals[$column] += $value
            $value
        }
        $grandTotal += $rowTotal
        $table.Add("| $project | $($values -join ' | ') | $rowTotal |")
    }
    $totalValues = foreach ($column in $columns) { [int]$totals[$column] }
    $table.Add("| **Total** | $($totalValues -join ' | ') | **$grandTotal** |")

    $generatedAt = [string]$Manifest.generatedAt
    $block = @"
<!-- BEGIN GENERATED WARNING BASELINE -->
Captured on $generatedAt from the machine-readable baseline.

The actionable baseline is **$($Manifest.warningCount)** distinct `(code, file, line)` locations. Line-only moves remain visible and do not count as new diagnostics.

$($table -join "`n")
<!-- END GENERATED WARNING BASELINE -->
"@
    return (ConvertTo-LfText ($block.TrimEnd() + "`n"))
}

function Get-GeneratedWarningBaselineMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Raw,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $matches = [regex]::Matches($Raw, '(?s)<!-- BEGIN GENERATED WARNING BASELINE -->.*?<!-- END GENERATED WARNING BASELINE -->')
    if ($matches.Count -ne 1) {
        Fail-Baseline "Warning baseline documentation must contain exactly one generated baseline block: $Path"
    }
    return $matches[0]
}

function Test-WarningBaselineDocumentation {
    param(
        [Parameter(Mandatory = $true)][object]$Manifest,
        [Parameter(Mandatory = $true)][string]$Path
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail-Baseline "Warning baseline documentation not found: $Path"
    }
    $raw = Get-Content -LiteralPath $Path -Raw
    $match = Get-GeneratedWarningBaselineMatch -Raw $raw -Path $Path
    $expected = (Get-GeneratedWarningBaselineBlock -Manifest $Manifest).Trim()
    $actual = $match.Value.Trim()
    if (($actual -replace "`r`n", "`n") -cne ($expected -replace "`r`n", "`n")) {
        Fail-Baseline "Warning baseline documentation is stale relative to $($Manifest.warningCount) JSON entries: $Path"
    }
}

function Get-UpdatedWarningBaselineDocumentation {
    param(
        [Parameter(Mandatory = $true)][object]$Manifest,
        [Parameter(Mandatory = $true)][string]$Path
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Fail-Baseline "Warning baseline documentation template not found: $Path"
    }
    $raw = Get-Content -LiteralPath $Path -Raw
    $match = Get-GeneratedWarningBaselineMatch -Raw $raw -Path $Path
    $replacement = (Get-GeneratedWarningBaselineBlock -Manifest $Manifest).TrimEnd()
    return (ConvertTo-LfText ($raw.Substring(0, $match.Index) + $replacement + $raw.Substring($match.Index + $match.Length)))
}

function Write-WarningBaselineDocumentation {
    param(
        [Parameter(Mandatory = $true)][object]$Manifest,
        [Parameter(Mandatory = $true)][string]$Path
    )
    $updated = Get-UpdatedWarningBaselineDocumentation -Manifest $Manifest -Path $Path
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, $updated, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

$manifest = $null
if ((Test-Path -LiteralPath $BaselineFile -PathType Leaf) -or -not $UpdateBaseline) {
    $manifest = Get-Baseline $BaselineFile
}
if ($ValidateOnly) {
    Test-WarningBaselineDocumentation -Manifest $manifest -Path $DocumentationFile
    Write-Host "Warning baseline manifest and documentation valid: $(@($manifest.warnings).Count) distinct locations." -ForegroundColor Green
    exit 0
}

if ([string]::IsNullOrWhiteSpace($GxPath)) {
    $GxPath = if (-not [string]::IsNullOrWhiteSpace($env:GX_PATH)) {
        $env:GX_PATH
    } else {
        Get-GxPrimaryInstallPath -Catalog $gxCatalog
    }
}
$sdkMarker = Join-Path $GxPath 'Artech.Architecture.Common.dll'
if (-not (Test-Path -LiteralPath $sdkMarker -PathType Leaf)) {
    Fail-Baseline "GeneXus SDK for primary major $(Get-GxPrimaryMajor -Catalog $gxCatalog) not found under '$GxPath'. Set -GxPath or GX_PATH."
}
$env:GX_PATH = $GxPath

$rootFull = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\') + '\'
function Normalize-WarningFile([string]$File) {
    $trimmed = $File.Trim().TrimEnd(']')
    try {
        $full = [System.IO.Path]::GetFullPath($trimmed)
        if ($full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
            return $full.Substring($rootFull.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
        }
    } catch { }
    return $trimmed.Replace('\', '/')
}

function Warning-Key($Entry) {
    return '{0}|{1}|{2}' -f $Entry.code, $Entry.file, $Entry.line
}

function Warning-Pair($Entry) {
    return '{0}|{1}' -f $Entry.code, $Entry.file
}

function Compare-WarningLocations {
    param(
        [object[]]$Baseline,
        [object[]]$Current
    )
    $baselineList = @($Baseline)
    $currentList = @($Current)
    $baselineKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $baselineList) { [void]$baselineKeys.Add((Warning-Key $entry)) }
    $currentKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $currentList) { [void]$currentKeys.Add((Warning-Key $entry)) }

    $baselineGroups = @{}
    foreach ($entry in $baselineList) {
        $pair = Warning-Pair $entry
        if (-not $baselineGroups.ContainsKey($pair)) { $baselineGroups[$pair] = @() }
        $baselineGroups[$pair] += $entry
    }
    $currentGroups = @{}
    foreach ($entry in $currentList) {
        $pair = Warning-Pair $entry
        if (-not $currentGroups.ContainsKey($pair)) { $currentGroups[$pair] = @() }
        $currentGroups[$pair] += $entry
    }

    $moved = New-Object System.Collections.Generic.List[object]
    foreach ($pair in $baselineGroups.Keys) {
        if (-not $currentGroups.ContainsKey($pair)) { continue }
        $oldLines = @($baselineGroups[$pair] | ForEach-Object line | Sort-Object)
        $newLines = @($currentGroups[$pair] | ForEach-Object line | Sort-Object)
        if ($oldLines.Count -eq $newLines.Count -and (@($oldLines) -join ',') -ne (@($newLines) -join ',')) {
            $first = $baselineGroups[$pair][0]
            [void]$moved.Add([PSCustomObject]@{
                code = $first.code
                file = $first.file
                baselineLines = @($oldLines)
                currentLines = @($newLines)
            })
        }
    }
    $movedPairs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $moved) { [void]$movedPairs.Add((Warning-Pair $entry)) }

    $newWarnings = @($currentList | Where-Object {
        $key = Warning-Key $_
        if ($baselineKeys.Contains($key)) { return $false }
        $pair = Warning-Pair $_
        # A pair with the same cardinality is a line move, not a new warning.
        if ($movedPairs.Contains($pair)) { return $false }
        return $true
    })
    $removedWarnings = @($baselineList | Where-Object {
        $key = Warning-Key $_
        if ($currentKeys.Contains($key)) { return $false }
        return -not $movedPairs.Contains((Warning-Pair $_))
    })
    [PSCustomObject]@{
        New = $newWarnings
        Removed = $removedWarnings
        Moved = $moved.ToArray()
    }
}

$solution = Join-Path $root 'Genexus18MCP.sln'
$buildOutput = @(& dotnet build $solution '-c' 'Release' '-t:Rebuild' '--nologo' '-v:minimal' "-p:GX_PATH=$GxPath" 2>&1 | ForEach-Object { $_.ToString() })
$buildExit = $LASTEXITCODE

$warningPattern = '^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*warning\s+(?<code>[A-Za-z]+\d+):'
$globalWarningPattern = '(?i)\bwarning\s+(?<code>[A-Za-z]+\d+):'
$current = @()
foreach ($outputLine in $buildOutput) {
    if ($outputLine -match $warningPattern) {
        $current += [PSCustomObject]@{
            code = $Matches.code.ToUpperInvariant()
            file = Normalize-WarningFile $Matches.file
            line = [int]$Matches.line
        }
    } elseif ($outputLine -match $globalWarningPattern) {
        $current += [PSCustomObject]@{
            code = $Matches.code.ToUpperInvariant()
            file = '<global>'
            line = 0
        }
    }
}

$distinct = @($current | Sort-Object code,file,line -Unique)
$msb3277 = @($distinct | Where-Object { $_.code -eq 'MSB3277' })
if ($buildExit -ne 0) {
    $tail = ($buildOutput | Select-Object -Last 12) -join "`n"
    Fail-Baseline "Release rebuild exited with code $buildExit.`n$tail"
}
if ($msb3277.Count -gt 0) {
    Fail-Baseline "MSB3277 is present in the Release build."
}

if ($UpdateBaseline) {
    $newManifest = [ordered]@{
        schemaVersion = 1
        generatedAt = (Get-Date).ToString('yyyy-MM-dd')
        warningCount = $distinct.Count
        warnings = @($distinct)
    }
    $json = ConvertTo-LfText (($newManifest | ConvertTo-Json -Depth 4) + "`n")
    $documentation = Get-UpdatedWarningBaselineDocumentation -Manifest $newManifest -Path $DocumentationFile
    $jsonTemporary = "$BaselineFile.$([guid]::NewGuid().ToString('N')).tmp"
    $documentationTemporary = "$DocumentationFile.$([guid]::NewGuid().ToString('N')).tmp"
    $oldJson = if (Test-Path -LiteralPath $BaselineFile -PathType Leaf) { [IO.File]::ReadAllBytes($BaselineFile) } else { $null }
    $oldDocumentation = if (Test-Path -LiteralPath $DocumentationFile -PathType Leaf) { [IO.File]::ReadAllBytes($DocumentationFile) } else { $null }
    [System.IO.File]::WriteAllText($jsonTemporary, $json, [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText($documentationTemporary, $documentation, [Text.UTF8Encoding]::new($false))
    $jsonMoved = $false
    $documentationMoved = $false
    try {
        Move-Item -LiteralPath $jsonTemporary -Destination $BaselineFile -Force
        $jsonMoved = $true
        Move-Item -LiteralPath $documentationTemporary -Destination $DocumentationFile -Force
        $documentationMoved = $true
    } catch {
        if ($jsonMoved -and $null -ne $oldJson) { [IO.File]::WriteAllBytes($BaselineFile, $oldJson) }
        if ($documentationMoved -and $null -ne $oldDocumentation) { [IO.File]::WriteAllBytes($DocumentationFile, $oldDocumentation) }
        throw
    } finally {
        foreach ($temporary in @($jsonTemporary, $documentationTemporary)) {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
        }
    }
    Write-Host "Warning baseline updated: $($distinct.Count) distinct locations at $BaselineFile" -ForegroundColor Green
    exit 0
}

$comparison = Compare-WarningLocations -Baseline @($manifest.warnings) -Current $distinct
Test-WarningBaselineDocumentation -Manifest $manifest -Path $DocumentationFile
$newWarnings = @($comparison.New)
$removedWarnings = @($comparison.Removed)
$movedWarnings = @($comparison.Moved)

Write-Host "Release warning locations: $($distinct.Count) (baseline $(@($manifest.warnings).Count)); new $($newWarnings.Count); moved $($movedWarnings.Count); removed $($removedWarnings.Count)."
if ($newWarnings.Count -gt 0) {
    $details = ($newWarnings | ForEach-Object { "{0} {1}:{2}" -f $_.code, $_.file, $_.line }) -join ', '
    Fail-Baseline "New warning locations detected (line-only moves are reported separately): $details"
}
if ($movedWarnings.Count -gt 0) {
    $details = ($movedWarnings | ForEach-Object { "{0} {1} [$($_.baselineLines -join ',') -> $($_.currentLines -join ',')]" }) -join ', '
    Write-Host "Line-only warning moves: $details" -ForegroundColor DarkGray
}
Write-Host "Warning baseline passed; no new locations and no MSB3277." -ForegroundColor Green
