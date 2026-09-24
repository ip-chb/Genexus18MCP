$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$contractPath = Join-Path $root 'scripts/release-contract.ps1'
if (-not (Test-Path -LiteralPath $contractPath -PathType Leaf)) {
    throw 'Shared release contract helper is missing.'
}
. $contractPath

$temp = Join-Path $env:TEMP ('gxmcp-release-contract-' + [guid]::NewGuid().ToString('N'))
$artifactRoot = Join-Path $temp 'repo'
$publish = Join-Path $artifactRoot 'publish'
New-Item -ItemType Directory -Path (Join-Path $publish 'worker') -Force | Out-Null
try {
    $artifactContents = [ordered]@{
        'publish/GxMcp.Gateway.exe' = 'gateway'
        'publish/worker/GxMcp.Worker.exe' = 'worker'
        'publish/tool_definitions.json' = '{}'
        'publish/nexus-ide.vsix' = 'vsix'
    }
    foreach ($entry in $artifactContents.GetEnumerator()) {
        $path = Join-Path $artifactRoot ($entry.Key -replace '/', '\')
        [IO.File]::WriteAllText($path, [string]$entry.Value, [Text.UTF8Encoding]::new($false))
    }
    $sourceBinaryContents = [ordered]@{
        'src/GxMcp.Gateway/bin/Release/net10.0-windows/GxMcp.Gateway.exe' = 'gateway'
        'src/GxMcp.Worker/bin/x86/Release/GxMcp.Worker.exe' = 'worker'
    }
    foreach ($entry in $sourceBinaryContents.GetEnumerator()) {
        $path = Join-Path $artifactRoot ($entry.Key -replace '/', '\')
        $parent = Split-Path -Parent $path
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        [IO.File]::WriteAllText($path, [string]$entry.Value, [Text.UTF8Encoding]::new($false))
    }
    $processTrxRoot = Join-Path $temp 'process-trx'
    New-Item -ItemType Directory -Path $processTrxRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $processTrxRoot 'process.trx'), '<TestRun><ResultSummary><Counters total="4" /></ResultSummary></TestRun>')

    $parts = @(
        foreach ($relativePath in $artifactContents.Keys) {
            $hash = (Get-FileHash -LiteralPath (Join-Path $artifactRoot ($relativePath -replace '/', '\')) -Algorithm SHA256).Hash.ToLowerInvariant()
            "$relativePath=$hash"
        }
    )
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $expectedPayload = $parts -join "`n"
        $expectedFingerprint = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($expectedPayload))) -replace '-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
    $fingerprint = Get-GxMcpReleaseArtifactFingerprint -RepositoryRoot $artifactRoot
    if ($fingerprint -cne $expectedFingerprint) {
        throw "Canonical artifact fingerprint mismatch: expected $expectedFingerprint, got $fingerprint."
    }

    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish.zip') -Value 'publish-zip' -NoNewline
    Set-Content -LiteralPath (Join-Path $artifactRoot 'publish.zip.sha256') -Value 'checksum' -NoNewline
    Set-Content -LiteralPath (Join-Path $artifactRoot 'nexus-ide-3.9.1.vsix') -Value 'vsix' -NoNewline
    $remoteAssets = @(
        foreach ($name in @('publish.zip', 'publish.zip.sha256', 'nexus-ide-3.9.1.vsix')) {
            $path = Join-Path $artifactRoot $name
            [pscustomobject]@{
                name = $name
                size = (Get-Item -LiteralPath $path).Length
                digest = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
                state = 'uploaded'
                updatedAt = '2026-09-24T12:00:00Z'
            }
        }
    )
    $assetState = Get-GxMcpReleaseAssetVerification -Assets $remoteAssets -RepositoryRoot $artifactRoot -Version '3.9.1'
    if (-not $assetState.isValid -or @($assetState.assets).Count -ne 3) {
        throw "Exact remote assets did not match local bytes: $($assetState.error)"
    }
    $remoteAssets[0].digest = 'sha256:' + ('0' * 64)
    $assetState = Get-GxMcpReleaseAssetVerification -Assets $remoteAssets -RepositoryRoot $artifactRoot -Version '3.9.1'
    if ($assetState.isValid) { throw 'A stale remote asset digest must invalidate publication.' }
    $remoteAssets[0].digest = 'sha256:' + (Get-FileHash -LiteralPath (Join-Path $artifactRoot 'publish.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    $legacyAssets = @($remoteAssets | Where-Object name -eq 'publish.zip')
    $legacyState = Get-GxMcpReleaseAssetVerification -Assets $legacyAssets -RepositoryRoot $artifactRoot -Version '2.6.8' -AllowLegacy
    if (-not $legacyState.isValid) { throw 'Legacy publish.zip-only recovery must remain supported.' }
    $legacyAssets[0].digest = $null
    $legacyState = Get-GxMcpReleaseAssetVerification -Assets $legacyAssets -RepositoryRoot $artifactRoot -Version '2.6.8' -AllowLegacy
    if (-not $legacyState.isValid) { throw 'Legacy publish.zip without a GitHub digest must remain supported.' }

    $catalog = [pscustomobject]@{
        primaryMajor = '18'
        supportedMajors = @([pscustomobject]@{ major = '18'; defaultInstallPath = 'C:\SDK\GeneXus18' })
    }
    $inputs = Resolve-GxMcpReleasePreflightInputs `
        -Root $artifactRoot `
        -Catalog $catalog `
        -GxPath 'C:\SDK\GeneXus18' `
        -Version '3.9.1' `
        -SourceCommit ('a' * 40) `
        -LiveKbPath 'C:\KBs\Fixture' `
        -LiveFixtureManifest 'C:\Fixtures\fixture.json' `
        -RequireLive `
        -RequireBuildAll `
        -ArtifactFingerprint $fingerprint
    $summary = [pscustomobject]@{
        schemaVersion = 'gxmcp-release-preflight/1'
        status = 'passed'
        root = $inputs.root
        version = $inputs.version
        sourceCommit = $inputs.sourceCommit
        gxPath = $inputs.gxPath
        liveKbPath = $inputs.liveKbPath
        liveMode = $inputs.liveMode
        liveMajors = @($inputs.liveMajors)
        liveGxPathMap = @($inputs.liveGxPathMap)
        liveFixtureManifest = $inputs.liveFixtureManifest
        liveKbSource = $inputs.liveKbSource
        liveFixtureSource = $inputs.liveFixtureSource
        requireLive = $inputs.requireLive
        requireBuildAll = $inputs.requireBuildAll
        skipLive = $inputs.skipLive
        skipWarningBaseline = $inputs.skipWarningBaseline
        artifactFingerprint = $inputs.artifactFingerprint
        processSmokeMode = 'serial-after-parallel'
        processSmokeTestCount = 4
        processSmokeResultsPath = $processTrxRoot
        processSmokeBinaryFingerprint = Get-GxMcpReleaseProcessSmokeFingerprint -RepositoryRoot $artifactRoot
        phases = @(
            foreach ($phaseName in (Get-GxMcpMandatoryPreflightPhaseNames)) {
                $command = switch ($phaseName) {
                    'release metadata parity' { 'python scripts/verify-release-metadata.py --version 3.9.1' }
                    'tool contract validation' { 'python scripts/validate-tool-contracts.py' }
                    'operation contract inventory' { 'python scripts/generate-operation-contract-inventory.py --check' }
                    'v3 plan readiness' { 'python scripts/validate-v3-plan.py --require-ready' }
                    'warning baseline documentation parity' { 'pwsh -File scripts/check-build-warning-baseline.ps1 -ValidateOnly' }
                    'Python script tests' { 'python -m unittest discover -s scripts/tests -v' }
                    'PowerShell script tests' { 'pwsh -File scripts/tests/run-release-script-tests.ps1' }
                    'CLI tests' { 'npm test' }
                    'CLI lint' { 'npm run lint' }
                    'Nexus IDE checks' { 'npm --prefix src/nexus-ide run check' }
                    'solution build and tests' { 'dotnet test Genexus18MCP.sln -c Release --filter Category!=ProcessSmoke -v:minimal' }
                    'solution process smoke tests' { 'dotnet test Genexus18MCP.sln -c Release --no-build --no-restore --filter Category=ProcessSmoke --logger trx;LogFilePrefix=process-smoke --results-directory C:/temp/process-smoke -v:minimal' }
                    'Release warning baseline' { 'pwsh -File scripts/check-build-warning-baseline.ps1 -BaselineFile docs/build_warning_baseline.json' }
                    'live KB gate' { 'pwsh -File scripts/test-live.ps1 -KbPath C:/KBs/Fixture -SkipBuild' }
                }
                [pscustomobject]@{ name = $phaseName; status = 'passed'; command = $command; exitCode = 0 }
            }
        )
    }
    if (-not (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed)) {
        throw 'Matching release preflight inputs must be reusable.'
    }
    if (-not (Test-GxMcpReleasePreflightCertificate -Summary $summary -Expected $inputs)) {
        throw 'A complete matching preflight summary must be a valid skip certificate.'
    }
    $summary.processSmokeTestCount = 0
    if (Test-GxMcpReleasePreflightCertificate -Summary $summary -Expected $inputs) {
        throw 'A process lane that selected zero tests must not certify release reuse.'
    }
    $summary.processSmokeTestCount = 4
    $summary.phases[11].status = 'timeout'
    if (Test-GxMcpReleasePreflightCertificate -Summary $summary -Expected $inputs) {
        throw 'A timed-out mandatory process phase must not certify release reuse.'
    }
    $summary.phases[11].status = 'passed'

    $summary.liveFixtureManifest = 'C:\Fixtures\different.json'
    if (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed) {
        throw 'A different live fixture manifest must invalidate preflight reuse.'
    }
    $summary.liveFixtureManifest = $inputs.liveFixtureManifest

    $summary.requireBuildAll = $false
    if (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed) {
        throw 'A weaker Build All policy must not reuse a stricter preflight.'
    }
    $summary.requireBuildAll = $true

    $summary.skipWarningBaseline = $true
    if (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed) {
        throw 'A skipped warning baseline must not reuse a complete preflight.'
    }
    $summary.skipWarningBaseline = $false

    $summary.status = 'failed'
    if (-not (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs)) {
        throw 'Build reuse should be allowed to rerun after a matching failed preflight.'
    }
    if (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed) {
        throw 'SkipBuild and SkipTests must require a passed preflight.'
    }
    $summary.status = 'passed'

    $summary.PSObject.Properties.Remove('liveFixtureManifest')
    if (Test-GxMcpReleasePreflightCompatibility -Summary $summary -Expected $inputs -RequirePassed) {
        throw 'A legacy summary without live gate identity must fail closed.'
    }

    $remoteAssets[0].digest = 'sha256:' + (Get-FileHash -LiteralPath (Join-Path $artifactRoot 'publish.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    $snapshotRecord = [pscustomobject]@{ number = 42; title = 'Issue'; url = 'https://example.invalid/issues/42' }
    $snapshot = [pscustomobject]@{
        schema = 'gxmcp-release-issues/1'; version = '3.9.1'; tag = 'v3.9.1'; issues = @($snapshotRecord)
    }
    if (-not (Test-GxMcpReleaseIssueSnapshotShape -Snapshot $snapshot).Valid) { throw 'A valid issue snapshot shape was rejected.' }
    $snapshot.issues = $null
    if ((Test-GxMcpReleaseIssueSnapshotShape -Snapshot $snapshot).Valid) { throw 'A null issue snapshot was accepted.' }
    $snapshot.issues = @($snapshotRecord, $snapshotRecord)
    if ((Test-GxMcpReleaseIssueSnapshotShape -Snapshot $snapshot).Valid) { throw 'Duplicate issue snapshot records were accepted.' }

    $publicationStatus = [pscustomobject]@{ version = '3.9.1'; tag = 'v3.9.1'; tagCommit = ('a' * 40) }
    $publicationEvidence = [pscustomobject]@{
        schemaVersion = 'gxmcp-release-publication/1'; state = 'verified'; tag = 'v3.9.1'; version = '3.9.1'
        expectedCommit = ('a' * 40); sourceCommit = ('a' * 40); tagCommit = ('a' * 40)
        npmVersion = '3.9.1'; npmGitHead = ('a' * 40); releaseUrl = 'https://github.com/lennix1337/Genexus18MCP/releases/tag/v3.9.1'; assetsUpdatedAtUtc = '2026-09-24T12:00:00Z'
        workflowRunId = '77'; workflowStatus = 'completed'; workflowConclusion = 'success'; workflowUrl = 'https://github.com/lennix1337/Genexus18MCP/actions/runs/77'
        releasePublishedAtUtc = '2026-09-24T11:59:00Z'; assets = $remoteAssets
    }
    if (-not (Test-GxMcpReleasePublicationEvidence -Status $publicationStatus -Publication $publicationEvidence -RepositoryRoot $artifactRoot).Valid) {
        throw 'Complete publication evidence must validate.'
    }
    $publicationEvidence.state = 'failed'
    if ((Test-GxMcpReleasePublicationEvidence -Status $publicationStatus -Publication $publicationEvidence).Valid) {
        throw 'Failed publication evidence must not validate.'
    }
    $publicationEvidence.state = 'verified'
    $publicationEvidence.assets = @($publicationEvidence.assets | Select-Object -First 2)
    if ((Test-GxMcpReleasePublicationEvidence -Status $publicationStatus -Publication $publicationEvidence).Valid) {
        throw 'Publication evidence with missing assets must not validate.'
    }

    $run = [pscustomobject]@{
        headSha = ('a' * 40); headBranch = 'v3.9.1'; event = 'release'; createdAt = '2026-09-24T12:00:01Z'
    }
    if (-not (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1' -MinimumCreatedAtUtc '2026-09-24T12:00:00Z')) {
        throw 'The exact release workflow run must match.'
    }
    $run.createdAt = '2026-09-24T11:59:59Z'
    if (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1' -MinimumCreatedAtUtc '2026-09-24T12:00:00Z') {
        throw 'A workflow that predates the current assets must not verify them.'
    }
    $run.createdAt = '2026-09-24T12:00:01Z'
    $run.headBranch = 'v3.9.0'
    if (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1') {
        throw 'A same-commit workflow for another tag must not match.'
    }
    $run.headBranch = 'v3.9.1'; $run.event = 'push'
    if (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1') {
        throw 'A same-tag push workflow must not match release publication.'
    }
    $run.event = 'workflow_dispatch'
    if (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1') {
        throw 'Manual repair must not match a first-publication verification.'
    }
    if (-not (Test-GxMcpReleaseWorkflowRun -Run $run -ExpectedCommit $inputs.sourceCommit -Tag 'v3.9.1' -AllowDispatch)) {
        throw 'An exact manual repair workflow must match an idempotent resume.'
    }

    foreach ($relativePath in @('release.ps1', 'scripts/release-preflight.ps1', 'scripts/release-doctor.ps1', 'scripts/verify-release-publication.ps1')) {
        $source = Get-Content -LiteralPath (Join-Path $root ($relativePath -replace '/', '\')) -Raw
        if ($source -notmatch 'release-contract\.ps1') {
            throw "$relativePath does not use the shared release contract."
        }
    }

    Write-Host 'release-contract: canonical fingerprint, full preflight identity and exact workflow matching passed' -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
}
