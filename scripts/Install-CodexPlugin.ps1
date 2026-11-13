[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $PSScriptRoot '../AccessRelayStudio-Beta'),
    [string]$MarketplaceDirectory = (Join-Path $PSScriptRoot '../artifacts/CodexPluginMarketplace')
)

$ErrorActionPreference = 'Stop'
$installation = (Resolve-Path -LiteralPath $InstallDirectory).Path
$bridge = Join-Path $installation 'Mcp/AccessRelayStudio.Mcp.exe'
if (-not (Test-Path -LiteralPath $bridge -PathType Leaf)) { throw 'Publish the complete Studio app before installing its Codex plugin.' }
if (-not (Get-Command codex -ErrorAction SilentlyContinue)) { throw 'The Codex CLI is required to register this local desktop plugin.' }
$marketplace = [IO.Path]::GetFullPath($MarketplaceDirectory).TrimEnd('\', '/')
if ($marketplace -eq [IO.Path]::GetPathRoot($marketplace).TrimEnd('\', '/')) { throw 'Choose a dedicated plugin marketplace folder.' }
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../plugins/accessrelay-studio'))
$plugin = Join-Path $marketplace 'plugins/accessrelay-studio'
if ($plugin.Equals($source, [StringComparison]::OrdinalIgnoreCase)) { throw 'The generated plugin must be separate from its source template.' }
New-Item -ItemType Directory -Path (Join-Path $plugin 'assets'), (Join-Path $plugin '.codex-plugin'), (Join-Path $marketplace '.agents/plugins') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'plugin.json') -Destination (Join-Path $plugin 'plugin.json') -Force
Copy-Item -LiteralPath (Join-Path $source 'README.md') -Destination (Join-Path $plugin 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../src/AccessRelayStudio/Assets/AccessRelayStudio.png') -Destination (Join-Path $plugin 'assets/icon.png') -Force
$connection = Get-Content -LiteralPath (Join-Path $source 'mcp.template.json') -Raw | ConvertFrom-Json
$connection.mcpServers.accessrelay.command = $bridge
$connection.mcpServers.accessrelay.cwd = Split-Path -Parent $bridge
$connection | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $plugin 'mcp.json') -Encoding utf8
# Older desktop clients use the supported Codex compatibility manifest.
$manifest = Get-Content -LiteralPath (Join-Path $source 'plugin.json') -Raw | ConvertFrom-Json
$legacy = [ordered]@{ name = $manifest.name; version = $manifest.version; description = $manifest.description;
    author = $manifest.author; requires_local_executor = $true; interface = $manifest.extensions.'com.openai'.interface; mcpServers = './.mcp.json' }
$legacy | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $plugin '.codex-plugin/plugin.json') -Encoding utf8
$connection | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $plugin '.mcp.json') -Encoding utf8
$catalog = [ordered]@{ name = 'accessrelay-local'; interface = @{ displayName = 'AccessRelay Studio' }; plugins = @(
    @{ name = 'accessrelay-studio'; source = @{ source = 'local'; path = './plugins/accessrelay-studio' };
        policy = @{ installation = 'AVAILABLE'; authentication = 'ON_INSTALL' }; category = 'Productivity' }) }
$catalog | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $marketplace '.agents/plugins/marketplace.json') -Encoding utf8
$listed = & codex plugin marketplace list --json
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect Codex marketplace registration.' }
$existing = ($listed | ConvertFrom-Json).marketplaces | Where-Object name -eq 'accessrelay-local'
if ($existing -and -not ([IO.Path]::GetFullPath($existing.root).TrimEnd('\', '/')).Equals($marketplace, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'An AccessRelay marketplace is already registered at another path. It was not replaced.'
}
if (-not $existing) {
    & codex plugin marketplace add $marketplace --json
    if ($LASTEXITCODE -ne 0) { throw 'Codex could not register the generated local marketplace.' }
}
& codex plugin add 'accessrelay-studio@accessrelay-local' --json
if ($LASTEXITCODE -ne 0) { throw 'Codex could not install the AccessRelay plugin.' }
Write-Output "Registered AccessRelay Studio plugin using $bridge"
