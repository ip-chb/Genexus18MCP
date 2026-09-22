#requires -Version 7
<#
.SYNOPSIS
  Ad-hoc MCP probe against a scratch Streamable-HTTP gateway.
.DESCRIPTION
  Minimal initialize + tools/call client for single-tool live SDK verification
  without the full LiveE2E harness (scripts/test-live.ps1). Reuses the MCP
  session id across invocations via -SessionFile so a scratch KB stays
  selected between probes. Prints the tool's result text (JSON-in-JSON) to
  stdout. See docs/agent_playbook.md "Proving SDK fixes against a real KB"
  for the surrounding scratch-gateway cycle (build, temp config with an
  unused port, GX_CONFIG_PATH + GXMCP_LOG_DIR, launch, verify port).
.EXAMPLE
  # Start a scratch gateway first (unused port, McpStdio=false), then:
  pwsh -File scripts/mcp-probe.ps1 -BaseUrl http://127.0.0.1:55171/mcp -Tool genexus_whoami
  pwsh -File scripts/mcp-probe.ps1 -Tool genexus_module -Arguments '{"action":"list","kb":"scratch"}'
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:55171/mcp',
    [string]$SessionFile = (Join-Path ([IO.Path]::GetTempPath()) 'gxmcp-probe.session.txt'),
    [Parameter(Mandatory = $true)][string]$Tool,
    [string]$Arguments = '{}',
    [ValidateRange(5, 3600)][int]$TimeoutSec = 600
)

$ErrorActionPreference = 'Stop'

function Invoke-McpPost([string]$Body, [string]$SessionId) {
    $headers = @{
        Accept = 'application/json, text/event-stream'
    }
    if (-not [string]::IsNullOrWhiteSpace($SessionId)) {
        $headers['Mcp-Session-Id'] = $SessionId
    }
    $response = Invoke-WebRequest -Uri $BaseUrl -Method Post -Body $Body `
        -ContentType 'application/json' -Headers $headers `
        -UseBasicParsing -TimeoutSec $TimeoutSec
    return $response
}

function Read-SsePayload([string]$Raw) {
    $payloads = @()
    foreach ($line in ($Raw -split "`n")) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('data: ')) { $payloads += $trimmed.Substring(6) }
    }
    if ($payloads.Count -eq 0) { $payloads = @($Raw) }
    return $payloads[-1]
}

$sessionId = ''
if (Test-Path -LiteralPath $SessionFile -PathType Leaf) {
    $sessionId = (Get-Content -LiteralPath $SessionFile -Raw).Trim()
}
if ([string]::IsNullOrWhiteSpace($sessionId)) {
    $init = @{
        jsonrpc = '2.0'
        id = 1
        method = 'initialize'
        params = @{
            protocolVersion = '2025-03-26'
            capabilities = @{}
            clientInfo = @{ name = 'mcp-probe'; version = '1.0' }
        }
    } | ConvertTo-Json -Depth 10
    $initResponse = Invoke-McpPost $init ''
    $sessionId = $initResponse.Headers['Mcp-Session-Id']
    if ($sessionId -is [array]) { $sessionId = $sessionId[0] }
    if ([string]::IsNullOrWhiteSpace($sessionId)) { throw 'initialize did not return Mcp-Session-Id' }
    $notif = @{ jsonrpc = '2.0'; method = 'notifications/initialized' } | ConvertTo-Json -Depth 5
    Invoke-McpPost $notif $sessionId | Out-Null
    Set-Content -LiteralPath $SessionFile -Value $sessionId -NoNewline
    Write-Verbose "New MCP session saved to $SessionFile"
}

try {
    $parsedArgs = $Arguments | ConvertFrom-Json
}
catch {
    throw "Arguments is not valid JSON: $($_.Exception.Message)"
}
$call = @{
    jsonrpc = '2.0'
    id = 2
    method = 'tools/call'
    params = @{ name = $Tool; arguments = $parsedArgs }
} | ConvertTo-Json -Depth 20
$callResponse = Invoke-McpPost $call $sessionId
$payload = Read-SsePayload $callResponse.Content | ConvertFrom-Json
$text = $payload.result.content[0].text
if ([string]::IsNullOrWhiteSpace($text)) {
    $payload | ConvertTo-Json -Depth 30
}
else {
    $text
}
