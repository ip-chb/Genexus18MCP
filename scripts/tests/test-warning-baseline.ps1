$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$scriptPath = Join-Path $PSScriptRoot '../check-build-warning-baseline.ps1'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw $errors[0] }
foreach ($name in @('Warning-Key', 'Warning-Pair', 'Compare-WarningLocations', 'ConvertTo-LfText', 'Get-WarningProjectLabel', 'Get-WarningCodeLabel', 'Get-GeneratedWarningBaselineBlock', 'Get-GeneratedWarningBaselineMatch')) {
    $definition = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $definition) { throw "Missing production function: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$baseline = @(
    [pscustomobject]@{ code = 'CS8600'; file = 'src/A.cs'; line = 10 }
    [pscustomobject]@{ code = 'CS8602'; file = 'src/A.cs'; line = 20 }
)
$current = @(
    [pscustomobject]@{ code = 'CS8600'; file = 'src/A.cs'; line = 11 }
    [pscustomobject]@{ code = 'CS8602'; file = 'src/A.cs'; line = 20 }
    [pscustomobject]@{ code = 'CS8604'; file = 'src/A.cs'; line = 30 }
)
$comparison = Compare-WarningLocations -Baseline $baseline -Current $current
if (@($comparison.Moved).Count -ne 1) { throw 'A line-only shift must be classified as moved.' }
if (@($comparison.New).Count -ne 1 -or $comparison.New[0].code -ne 'CS8604') { throw 'A new warning code must remain a blocking new diagnostic.' }
if (@($comparison.Removed).Count -ne 0) { throw 'Moved diagnostics must not be reported as removed.' }

$syntheticManifest = [pscustomobject]@{
    generatedAt = '2026-09-23'
    warningCount = 1
    warnings = @([pscustomobject]@{ code = 'CS8600'; file = 'src/GxMcp.Gateway/Example.cs'; line = 4 })
}
$generatedBlock = Get-GeneratedWarningBaselineBlock -Manifest $syntheticManifest
if ($generatedBlock -notmatch 'BEGIN GENERATED WARNING BASELINE' -or $generatedBlock -notmatch '\*\*1\*\*' -or $generatedBlock -notmatch '\| GxMcp\.Gateway \| 1 \|') {
    throw 'Warning baseline documentation renderer did not produce a complete table.'
}
if ($generatedBlock.Contains("`r`n")) { throw 'Warning baseline renderer emitted CRLF instead of repository LF.' }

$manifest = Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) '..\docs\build_warning_baseline.json') -Raw | ConvertFrom-Json
$doc = Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) '..\docs\build_warning_baseline.md') -Raw
if ($doc -notmatch "(?m)\|\s*\*\*$($manifest.warningCount)\*\*\s*\|\s*$") {
    throw "Warning baseline Markdown total does not match JSON warningCount=$($manifest.warningCount)."
}

$staleRoot = Join-Path $env:TEMP ('gxmcp-warning-doc-stale-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staleRoot -Force | Out-Null
try {
    $staleJson = Join-Path $staleRoot 'baseline.json'
    $staleDoc = Join-Path $staleRoot 'baseline.md'
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) '..\docs\build_warning_baseline.json') -Destination $staleJson
    $staleContent = (Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) '..\docs\build_warning_baseline.md') -Raw).Replace("**$($manifest.warningCount)**", "**$([int]$manifest.warningCount + 1)**")
    Set-Content -LiteralPath $staleDoc -Value $staleContent -Encoding utf8
    $baselineScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../check-build-warning-baseline.ps1'))
    $staleOutput = @(& pwsh -NoProfile -File $baselineScript -ValidateOnly -BaselineFile $staleJson -DocumentationFile $staleDoc 2>&1)
    if ($LASTEXITCODE -eq 0) { throw 'Stale warning baseline documentation was accepted.' }
    if (($staleOutput -join "`n") -notmatch 'stale') { throw 'Stale documentation failure did not explain the mismatch.' }
} finally {
    if (Test-Path -LiteralPath $staleRoot) { Remove-Item -LiteralPath $staleRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

$negativeRoot = Join-Path $env:TEMP ('gxmcp-warning-doc-negative-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $negativeRoot -Force | Out-Null
try {
    $negativeJson = Join-Path $negativeRoot 'baseline.json'
    $negativeDoc = Join-Path $negativeRoot 'baseline.md'
    $missingDate = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) '..\docs\build_warning_baseline.json') -Raw | ConvertFrom-Json
    $missingDate.PSObject.Properties.Remove('generatedAt')
    $missingDate | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $negativeJson -Encoding utf8
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) '..\docs\build_warning_baseline.md') -Destination $negativeDoc
    $dateOutput = @(& pwsh -NoProfile -File $baselineScript -ValidateOnly -BaselineFile $negativeJson -DocumentationFile $negativeDoc 2>&1)
    if ($LASTEXITCODE -eq 0 -or ($dateOutput -join "`n") -notmatch 'generatedAt') { throw 'A warning manifest without generatedAt was accepted.' }

    $duplicateDoc = Join-Path $negativeRoot 'duplicate.md'
    $baseDoc = Get-Content -LiteralPath $negativeDoc -Raw
    $block = [regex]::Match($baseDoc, '(?s)<!-- BEGIN GENERATED WARNING BASELINE -->.*?<!-- END GENERATED WARNING BASELINE -->').Value
    Set-Content -LiteralPath $duplicateDoc -Value ($baseDoc + "`n" + $block) -Encoding utf8
    $duplicateOutput = @(& pwsh -NoProfile -File $baselineScript -ValidateOnly -BaselineFile (Join-Path (Split-Path -Parent $PSScriptRoot) '..\docs\build_warning_baseline.json') -DocumentationFile $duplicateDoc 2>&1)
    if ($LASTEXITCODE -eq 0 -or ($duplicateOutput -join "`n") -notmatch 'exactly one') { throw 'Duplicate generated warning blocks were accepted.' }
} finally {
    if (Test-Path -LiteralPath $negativeRoot) { Remove-Item -LiteralPath $negativeRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host 'warning-baseline: move-aware comparison, rendering and stale-doc rejection passed' -ForegroundColor Green
