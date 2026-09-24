[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$RepositoryRoot,
    [ValidateRange(0, 86400)][int]$WaitSeconds = 0,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-contract.ps1')
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) { $RepositoryRoot = Split-Path -Parent $PSScriptRoot } else { $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot) }
$resolved = [IO.Path]::GetFullPath($Path)
$deadline = (Get-Date).AddSeconds($WaitSeconds)

function Read-ReleaseStatus {
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { return $null }
    try { return Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json }
    catch { throw "Release status file is not valid JSON: $resolved" }
}

function Protect-StatusMessage([object]$Value) {
    return Protect-GxMcpReleaseMessage $Value
}

function Get-SafePublication([object]$Value) {
    if ($null -eq $Value) { return $null }
    try {
        return Get-GxMcpSafeReleaseValue $Value
    } catch {
        return [pscustomobject]@{ state = 'invalid'; error = Protect-StatusMessage $_.Exception.Message }
    }
}

$status = $null
do {
    $status = Read-ReleaseStatus
    if ($null -ne $status -and $status.state -in @('succeeded', 'failed')) { break }
    if ((Get-Date) -ge $deadline) { break }
    Start-Sleep -Seconds 1
} while ($true)

if ($null -eq $status) {
    Write-Error "Release status file not found: $resolved"
    exit 2
}

if ($status.PSObject.Properties['publication'] -and $null -ne $status.publication) {
    $status.publication = Get-SafePublication $status.publication
}
$publicationPath = if ($status.publicationEvidencePath) { [IO.Path]::GetFullPath([string]$status.publicationEvidencePath) } else { "$resolved.publication.json" }
$publication = $null
if (Test-Path -LiteralPath $publicationPath -PathType Leaf) {
    try {
        $publication = Get-SafePublication (Get-Content -LiteralPath $publicationPath -Raw | ConvertFrom-Json)
        $status | Add-Member -NotePropertyName publication -NotePropertyValue $publication -Force
        if ($publication.state) { $status.publicationState = [string]$publication.state }
        if ($publication.error) { $status.publicationError = Protect-StatusMessage $publication.error }
    } catch {
        $publication = [pscustomobject]@{ state = 'invalid'; error = Protect-StatusMessage $_.Exception.Message }
        $status | Add-Member -NotePropertyName publication -NotePropertyValue $publication -Force
        $status.publicationState = 'invalid'
        $status.publicationError = $publication.error
    }
}

$evidenceCheck = Test-GxMcpReleasePublicationEvidence -Status $status -Publication $publication -RepositoryRoot $RepositoryRoot -AllowLegacy:(([string]$status.version) -notmatch '^3\.')
$status | Add-Member -NotePropertyName publicationEvidenceValid -NotePropertyValue ([bool]$evidenceCheck.Valid) -Force
$status | Add-Member -NotePropertyName publicationEvidenceError -NotePropertyValue (Protect-StatusMessage $evidenceCheck.Error) -Force
if (-not $evidenceCheck.Valid -and ([string]$status.state -eq 'succeeded' -or ($publication -and [string]$publication.state -eq 'failed'))) {
    $status.state = 'failed'
    $status.error = "Publication verification failed: $($evidenceCheck.Error)"
}
if ($status.error) { $status.error = Protect-StatusMessage $status.error }
if ($status.publicationError) { $status.publicationError = Protect-StatusMessage $status.publicationError }
if ($status.PSObject.Properties['publication'] -and $null -ne $status.publication) {
    $status.publication = Get-SafePublication $status.publication
}
$status = Get-GxMcpSafeReleaseValue $status

if ($Json) {
    $status | ConvertTo-Json -Depth 20
} else {
    $phase = if ($status.phase) { $status.phase } else { 'unknown' }
    $state = if ($status.state) { $status.state } else { 'unknown' }
    Write-Host ("version={0} tag={1} phase={2} state={3}" -f $status.version, $status.tag, $phase, $state)
    Write-Host ("updatedAtUtc={0} pid={1} exitCode={2}" -f $status.updatedAtUtc, $status.pid, $status.exitCode)
    if ($status.statusFile) { Write-Host "statusFile=$($status.statusFile)" }
    if ($status.stdoutLog) { Write-Host "stdoutLog=$($status.stdoutLog)" }
    if ($status.stderrLog) { Write-Host "stderrLog=$($status.stderrLog)" }
    if ($status.releaseUrl) { Write-Host "releaseUrl=$($status.releaseUrl)" }
    if ($status.workflowRunId) { Write-Host "workflowRunId=$($status.workflowRunId)" }
    if ($status.publicationState) { Write-Host "publicationState=$($status.publicationState)" }
    if ($status.publicationEvidencePath) { Write-Host "publicationEvidence=$($status.publicationEvidencePath)" }
    if ($status.npmVersion) { Write-Host "npmVersion=$($status.npmVersion)" }
    if ($status.nextAction) { Write-Host "nextAction=$($status.nextAction)" }
    if ($status.artifactFingerprint) { Write-Host "artifactFingerprint=$($status.artifactFingerprint)" }
    Write-Host "publicationEvidenceValid=$($status.publicationEvidenceValid)"
    if ($status.publicationEvidenceError) { Write-Host "publicationEvidenceError=$($status.publicationEvidenceError)" -ForegroundColor Red }
    if ($status.publicationError) { Write-Host "publicationError=$($status.publicationError)" -ForegroundColor Red }
    if ($status.error) { Write-Host "error=$($status.error)" -ForegroundColor Red }
}

$state = if ($status.state) { $status.state } else { 'unknown' }
if ($state -eq 'succeeded') { exit 0 }
if ($state -eq 'failed') { exit 1 }
if ((Get-Date) -ge $deadline) { exit 2 }
exit 2
