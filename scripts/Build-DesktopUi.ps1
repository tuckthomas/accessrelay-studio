[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$interfaceDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../src/AccessRelayStudio/DesktopUi'))
if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
    throw 'Building the desktop interface requires pnpm and Node.js on the development PC. The installed app does not require either.'
}
if (-not (Test-Path -LiteralPath (Join-Path $interfaceDirectory 'node_modules/.bin/vite.cmd'))) {
    & pnpm --dir $interfaceDirectory install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw 'Desktop interface dependency installation failed.' }
}
& pnpm --dir $interfaceDirectory build
if ($LASTEXITCODE -ne 0) { throw 'Desktop interface build failed. The existing installation was left unchanged.' }
if (-not (Test-Path -LiteralPath (Join-Path $interfaceDirectory 'dist/index.html') -PathType Leaf)) {
    throw 'The desktop interface build did not produce its entry point.'
}
