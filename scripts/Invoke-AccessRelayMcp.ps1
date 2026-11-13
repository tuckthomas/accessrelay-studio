#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = 'Call')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Call')][string]$ToolName,
    [Parameter(ParameterSetName = 'Call')][string]$ArgumentsJson = '{}',
    [Parameter(Mandatory = $true, ParameterSetName = 'List')][switch]$ListTools,
    [string]$InstallDirectory = (Join-Path $PSScriptRoot '../AccessRelayStudio-Beta'),
    [ValidateRange(5, 180)][int]$TimeoutSeconds = 30
)

# A real stdio MCP client for connected-computer sessions without injected MCP tools.
# Studio retains its normal access, review and device-activation restrictions.
$ErrorActionPreference = 'Stop'
$bridge = Join-Path (Resolve-Path -LiteralPath $InstallDirectory).Path 'Mcp/AccessRelayStudio.Mcp.exe'
if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) { throw 'The installed AccessRelay MCP executable is missing.' }
if ($ArgumentsJson.Length -gt 1024 * 1024) { throw 'Tool arguments exceed the local client limit.' }
$toolArguments = ConvertFrom-Json -InputObject $ArgumentsJson
if ($null -eq $toolArguments -or $toolArguments -isnot [pscustomobject]) { throw 'ArgumentsJson must be a JSON object.' }

$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $bridge
$start.WorkingDirectory = Split-Path -Parent $bridge
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $start.RedirectStandardOutput = $start.RedirectStandardError = $true
$start.StandardOutputEncoding = $start.StandardErrorEncoding = New-Object System.Text.UTF8Encoding($false)
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $start
$rpcId = 0
$started = $false

function Invoke-McpRequest([string]$Method, [object]$Parameters) {
    $script:rpcId++
    $request = @{ jsonrpc = '2.0'; id = $script:rpcId; method = $Method; params = $Parameters }
    $process.StandardInput.WriteLine((ConvertTo-Json -InputObject $request -Depth 32 -Compress))
    $process.StandardInput.Flush()
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        $lineTask = $process.StandardOutput.ReadLineAsync()
        if (-not $lineTask.Wait($remaining)) { throw "MCP request timed out: $Method" }
        $line = $lineTask.Result
        if ($null -eq $line) { throw "The MCP server closed before replying to $Method." }
        if ($line.Length -gt 2 * 1024 * 1024) { throw 'The MCP reply exceeds the local client limit.' }
        if ($line.Trim()) {
            $reply = ConvertFrom-Json -InputObject $line
            if ($reply.PSObject.Properties['id'] -and $reply.id -eq $script:rpcId) {
                if ($reply.PSObject.Properties['error']) { throw (ConvertTo-Json -InputObject $reply.error -Compress -Depth 8) }
                return $reply.result
            }
        }
        if ([DateTime]::UtcNow -ge $deadline) { throw "MCP request timed out: $Method" }
    }
}

try {
    $started = $process.Start()
    if (-not $started) { throw 'The MCP bridge could not start.' }
    # Drain SDK diagnostic stderr separately; stdout contains only the MCP result.
    $diagnostics = $process.StandardError.ReadToEndAsync()
    $null = Invoke-McpRequest 'initialize' @{
        protocolVersion = '2025-11-25'; capabilities = @{}
        clientInfo = @{ name = 'AccessRelay connected-computer client'; version = '1.0' }
    }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.Flush()
    $catalog = Invoke-McpRequest 'tools/list' @{}
    if ($ListTools) { ConvertTo-Json -InputObject $catalog -Depth 32; return }
    if (-not @($catalog.tools | Where-Object name -eq $ToolName).Count) { throw "The installed server does not offer $ToolName." }
    $result = Invoke-McpRequest 'tools/call' @{ name = $ToolName; arguments = $toolArguments }
    # Preserve isError and structuredContent. A discovered tool is not a successful live call.
    ConvertTo-Json -InputObject $result -Depth 32
}
finally {
    if ($started) {
        if (-not $process.HasExited) {
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(1000)) { $process.Kill(); $process.WaitForExit(1000) | Out-Null }
        }
    }
    $process.Dispose()
}
