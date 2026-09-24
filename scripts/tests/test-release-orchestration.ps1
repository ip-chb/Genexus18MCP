$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$temp = Join-Path $env:TEMP ('gxmcp-release-orchestration-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $temp 'source'
$publish = Join-Path $temp 'publish'
New-Item -ItemType Directory -Path $repo, (Join-Path $publish 'worker') -Force | Out-Null
try {
    Push-Location $repo
    try {
        git init -q
        git config user.email 'test@example.invalid'
        git config user.name 'release-test'
        Set-Content -LiteralPath (Join-Path $repo 'source.txt') -Value 'committed' -Encoding ascii
        git add source.txt
        git commit -q -m 'fixture source'
        $expectedCommit = (git rev-parse HEAD).Trim()
    } finally { Pop-Location }

    Set-Content -LiteralPath (Join-Path $publish 'GxMcp.Gateway.exe') -Value 'gateway' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publish 'worker\GxMcp.Worker.exe') -Value 'worker' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $publish 'tool_definitions.json') -Value '[]' -Encoding ascii
    $writer = Join-Path $root 'scripts\write-release-manifest.ps1'
    $manifestPath = & pwsh -NoProfile -File $writer -PublishDirectory $publish -Version 3.0.1 -SourceRoot $repo
    $manifest = Get-Content -LiteralPath ($manifestPath | Select-Object -Last 1) -Raw | ConvertFrom-Json
    if ($manifest.sourceCommit -ne $expectedCommit) { throw "Clean source did not bind to HEAD ($expectedCommit)." }
    $verifier = Join-Path $root 'scripts\verify-release-manifest.py'
    & python $verifier $publish --version 3.0.1 --source-commit $expectedCommit *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Committed manifest was not accepted by the verifier.' }

    Set-Content -LiteralPath (Join-Path $repo 'source.txt') -Value 'dirty' -Encoding ascii
    $dirtyPath = & pwsh -NoProfile -File $writer -PublishDirectory $publish -Version 3.0.1 -SourceRoot $repo
    $dirty = Get-Content -LiteralPath ($dirtyPath | Select-Object -Last 1) -Raw | ConvertFrom-Json
    if ($dirty.sourceCommit -ne 'working-tree') { throw 'Dirty source must be marked working-tree.' }
    & python $verifier $publish --version 3.0.1 *> $null
    if ($LASTEXITCODE -eq 0) { throw 'Verifier accepted working-tree provenance.' }

    $zip = Join-Path $temp 'publish.zip'
    Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force
    $sidecar = "$zip.sha256"
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $sidecar -Value "$hash  publish.zip" -Encoding ascii
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    $declared = (Get-Content -LiteralPath $sidecar -Raw).Trim().Split()[0]
    if ($actual -ne $declared) { throw 'Release checksum asset does not match publish.zip.' }
    git -C $root check-ignore --no-index -q publish.zip.sha256
    if ($LASTEXITCODE -ne 0) { throw 'Checksum sidecar must be ignored by the source tree.' }

    $releaseSource = Get-Content (Join-Path $root 'release.ps1') -Raw
    $baseCheckPosition = $releaseSource.IndexOf('Step "Checking release base freshness"', [StringComparison]::Ordinal)
    $issueSnapshotPosition = $releaseSource.IndexOf('Step "Snapshotting release issues"', [StringComparison]::Ordinal)
    $metadataSyncPosition = $releaseSource.IndexOf('Step "Synchronizing release-facing metadata"', [StringComparison]::Ordinal)
    if ($baseCheckPosition -lt 0 -or
        $issueSnapshotPosition -lt 0 -or
        $metadataSyncPosition -lt 0 -or
        $baseCheckPosition -gt $issueSnapshotPosition -or
        $baseCheckPosition -gt $metadataSyncPosition) {
        throw 'Release base freshness must be checked before snapshotting issues or changing release metadata.'
    }
    if (-not $releaseSource.Contains("git ls-remote --heads origin 'refs/heads/main'") -or
        -not $releaseSource.Contains('if ($branch -eq ''main'' -and -not $remoteTag)') -or
        -not $releaseSource.Contains('Test-ReleaseBaseAlignment')) {
        throw 'A new release must compare the live origin/main head while preserving tagged-release resume.'
    }
    $commitIndex = $releaseSource.IndexOf('Committing release source state', [StringComparison]::Ordinal)
    $buildIndex = $releaseSource.IndexOf('# -- 3. Build + zip', [StringComparison]::Ordinal)
    if ($commitIndex -lt 0 -or $buildIndex -lt 0 -or $commitIndex -gt $buildIndex) { throw 'Release source commit must occur before build.' }
    if ($releaseSource -notmatch '''-SourceCommit'', \$releaseSourceCommit') { throw 'Manifest writer is not passed the committed source id.' }
    if ($releaseSource -notmatch '\$releaseExists' -or $releaseSource -notmatch "'release', 'upload'") { throw 'Resume path must upload assets to an existing release instead of creating a duplicate.' }
    $statusStartIndex = $releaseSource.IndexOf("Write-ReleaseStatus -Phase 'starting'", [StringComparison]::Ordinal)
    $tagInitIndex = $releaseSource.IndexOf('$tag = $null', [StringComparison]::Ordinal)
    if ($statusStartIndex -lt 0 -or $tagInitIndex -lt 0 -or $tagInitIndex -gt $statusStartIndex) { throw 'Release status must initialize the tag before the first status write.' }
    foreach ($marker in @('CloseIssuesFile', 'Get-ReleaseIssueNumbers', 'Get-LabeledReleaseIssues', 'release-issues.txt', 'release-issues.json', 'gh api --paginate', 'repos/{owner}/{repo}/issues/$issue', 'per_page=100', 'OutputEncoding', 'Console]::OutputEncoding', 'ReleaseMilestone', 'Tracked issues', 'deduplicated', 'SkipLabeledIssues', 'hasTrackedIssuesInUnreleased', 'release-issues.ps1', 'Get-ReleaseIssueData', 'Assert-ReleaseIssueAction', 'CloseAfterRelease', 'issues = [ordered]', 'ToUpperInvariant()', '$CloseIssues = @(', 'foreach ($issueNumber in @($explicitIssues))', 'foreach ($issueNumber in @($labeledIssues))', 'Get-GxMcpReleaseArtifactFingerprint', 'Get-ReleasePreflightInputs', 'Test-GxMcpReleasePreflightCertificate', 'Get-GxMcpReleaseAssetVerification', 'release-contract.ps1', 'verify-release-manifest.py', '--archive', 'remoteAssetsNeedRepair', 'Test-ReleaseIssuePublicationComment', 'Test-ReleaseIssuePublicationComment -IssueData $commented', 'existing snapshot header does not match', 'preflightSummaryPath', 'ResumeSummaryPath', 'warning baseline is included in the complete preflight')) {
        if ($releaseSource -notmatch [regex]::Escape($marker)) { throw "Release issue batch support is missing: $marker" }
    }

    # Exercise the production issue normalizer instead of only checking source
    # markers. The one-element int[] case used to unwrap to a scalar before
    # concatenation, which could silently lose or duplicate release issues.
    $tokens = $null; $errors = $null
    $releaseAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'release.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw $errors[0] }
    $baseAlignmentFunction = $releaseAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-ReleaseBaseAlignment' }, $true)
    if (-not $baseAlignmentFunction) { throw 'Missing Test-ReleaseBaseAlignment production function.' }
    . ([scriptblock]::Create($baseAlignmentFunction.Extent.Text))
    $remoteMainHead = 'a' * 40
    $releaseHead = 'b' * 40
    $olderHead = 'c' * 40
    if (-not (Test-ReleaseBaseAlignment -LocalHead $remoteMainHead -RemoteMainHead $remoteMainHead -LocalParent $null -LocalSubject $null -Tag 'v3.8.1')) {
        throw 'A local main exactly at origin/main must pass release base validation.'
    }
    if (-not (Test-ReleaseBaseAlignment -LocalHead $releaseHead -RemoteMainHead $remoteMainHead -LocalParent $remoteMainHead -LocalSubject 'release: v3.8.1' -Tag 'v3.8.1')) {
        throw 'A pending release commit directly on origin/main must remain resumable.'
    }
    if (Test-ReleaseBaseAlignment -LocalHead $releaseHead -RemoteMainHead $remoteMainHead -LocalParent $olderHead -LocalSubject 'release: v3.8.1' -Tag 'v3.8.1') {
        throw 'A diverged release commit must fail base validation.'
    }
    if (Test-ReleaseBaseAlignment -LocalHead $releaseHead -RemoteMainHead $remoteMainHead -LocalParent $remoteMainHead -LocalSubject 'release: v3.8.0' -Tag 'v3.8.1') {
        throw 'A release commit for another version must not be treated as resumable.'
    }
    $vsixFunctionNames = @('Get-ReleaseFileSha256', 'Ensure-ReleaseVsixArtifacts')
    foreach ($name in $vsixFunctionNames) {
        $definition = $releaseAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
        if (-not $definition) { throw "Missing production VSIX reconciliation function: $name" }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $vsixRoot = Join-Path $temp 'nexus-ide-3.0.1.vsix'
    $vsixPublishDir = Join-Path $temp 'vsix-publish'
    $vsixPublish = Join-Path $vsixPublishDir 'nexus-ide.vsix'
    New-Item -ItemType Directory -Path $vsixPublishDir -Force | Out-Null
    Set-Content -LiteralPath $vsixRoot -Value 'root-vsix' -Encoding ascii
    Ensure-ReleaseVsixArtifacts -PublishDirectory $vsixPublishDir -VsixPath $vsixRoot
    if (-not (Test-Path -LiteralPath $vsixPublish)) { throw 'Skip-build reconciliation did not populate publish/nexus-ide.vsix.' }
    Remove-Item -LiteralPath $vsixRoot -Force
    Ensure-ReleaseVsixArtifacts -PublishDirectory $vsixPublishDir -VsixPath $vsixRoot
    if ((Get-ReleaseFileSha256 -Path $vsixRoot) -ne (Get-ReleaseFileSha256 -Path $vsixPublish)) { throw 'Skip-build reconciliation did not restore the root VSIX from publish.' }
    Set-Content -LiteralPath $vsixRoot -Value 'different-root' -Encoding ascii
    $mismatchRejected = $false
    try { Ensure-ReleaseVsixArtifacts -PublishDirectory $vsixPublishDir -VsixPath $vsixRoot } catch { $mismatchRejected = $true }
    if (-not $mismatchRejected) { throw 'Mismatched root/publish VSIX artifacts were accepted.' }
    Remove-Item -LiteralPath $vsixPublish, $vsixRoot -Force -ErrorAction SilentlyContinue
    Ensure-ReleaseVsixArtifacts -PublishDirectory $vsixPublishDir -VsixPath $vsixRoot -AllowMissing
    if (Test-Path -LiteralPath $vsixPublish) { throw 'Legacy missing-VSIX reconciliation unexpectedly created an artifact.' }
    $issueFunction = $releaseAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ReleaseIssueNumbers' }, $true)
    if (-not $issueFunction) { throw 'Missing Get-ReleaseIssueNumbers production function.' }
    . ([scriptblock]::Create($issueFunction.Extent.Text))
    function Fail([string]$message) { throw $message }
    $CloseIssuesFile = $null
    $CloseIssues = [int[]](42)
    $single = @(Get-ReleaseIssueNumbers)
    if ($single.Count -ne 1 -or $single[0] -ne 42) { throw 'A scalar-equivalent int[] issue input was not preserved.' }
    $CloseIssues = [int[]](42, 42, 7)
    $deduplicated = @(Get-ReleaseIssueNumbers)
    if (($deduplicated -join ',') -ne '42,7') { throw "Issue array was not deduplicated: $($deduplicated -join ',')" }
    $CloseIssuesFile = Join-Path $temp 'issues.txt'
    Set-Content -LiteralPath $CloseIssuesFile -Value "#7, 42`n42" -Encoding ascii
    $fromFile = @(Get-ReleaseIssueNumbers)
    if (($fromFile -join ',') -ne '42,7') { throw "Explicit and file issue inputs were combined incorrectly: $($fromFile -join ',')" }
    $CloseIssuesFile = Join-Path $temp 'invalid-issues.txt'
    Set-Content -LiteralPath $CloseIssuesFile -Value 'not-an-issue' -Encoding ascii
    try {
        [void](Get-ReleaseIssueNumbers)
        throw 'Invalid issue-file input must fail closed.'
    } catch {
        if ($_.Exception.Message -notmatch 'Invalid issue number') { throw }
    }

    Write-Host 'release-orchestration: exact provenance, dirty rejection and checksum asset checks passed' -ForegroundColor Green
}
finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
