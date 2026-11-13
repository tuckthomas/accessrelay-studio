[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $AppDirectory,
    [string] $ModelManifest,
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$applicationRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $applicationRoot 'onnxruntime.dll') -PathType Leaf)) {
    throw 'AppDirectory must contain the complete published Windows app, including onnxruntime.dll.'
}
if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $probeDirectory = Join-Path $repositoryRoot 'artifacts/gpu-preprocessing'
    New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
    $OutFile = Join-Path $probeDirectory ('probe-' + [Guid]::NewGuid().ToString('N') + '.json')
}
if (Test-Path -LiteralPath $OutFile) { throw 'Choose a new OutFile; existing probe evidence is never overwritten.' }
$probePath = [IO.Path]::GetFullPath($OutFile)
if (-not (Test-Path -LiteralPath (Split-Path -Parent $probePath) -PathType Container)) {
    throw 'Create the chosen output parent directory before running the probe.'
}
$probeArguments = @('run', '--project', (Join-Path $repositoryRoot 'src/AccessRelayStudio/WindowsVerification/WindowsVerification.csproj'),
    '-c', 'Release', '-p:EnableWindowsTargeting=true', '--', '--cuda-preprocessing-probe', '--app-directory', $applicationRoot, '--out', $probePath)
if (-not [string]::IsNullOrWhiteSpace($ModelManifest)) {
    $probeArguments += @('--model', (Resolve-Path -LiteralPath $ModelManifest).Path)
}
& dotnet @probeArguments
$probeExitCode = $LASTEXITCODE
if (Test-Path -LiteralPath $probePath -PathType Leaf) { Write-Host "Probe report: $probePath" }
else { Write-Warning 'No probe report was produced. Review the build or startup error above.' }
if ($probeExitCode -eq 2) { Write-Warning 'CUDA unavailable. No NVIDIA interop or inference acceptance is established.' }
elseif ($probeExitCode -eq 3) { Write-Warning 'Tensor interoperability only. Supply ModelManifest to check actual CUDA inference parity.' }
elseif ($probeExitCode -eq 0) { Write-Host 'Synthetic CUDA tensor and inference parity passed. Gameplay accuracy and performance acceptance remain separate.' }
else { Write-Warning 'The probe failed. Review its report before enabling experimental GPU preparation.' }
exit $probeExitCode
