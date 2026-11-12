[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$CudaRuntimeRoot,
    [string[]]$AppArguments = @()
)

$ErrorActionPreference = 'Stop'
$resolvedApp = (Resolve-Path -LiteralPath $AppPath).Path
$resolvedRuntime = (Resolve-Path -LiteralPath $CudaRuntimeRoot).Path
if (-not (Test-Path -LiteralPath $resolvedApp -PathType Leaf) -or
    [IO.Path]::GetExtension($resolvedApp) -ne '.exe') {
    throw 'AppPath must name the published Windows application executable.'
}
if (-not (Test-Path -LiteralPath $resolvedRuntime -PathType Container)) {
    throw 'CudaRuntimeRoot must name the scoped NVIDIA runtime directory.'
}
$dllDirectories = @(Get-ChildItem -LiteralPath $resolvedRuntime -File -Recurse -Filter '*.dll' |
    ForEach-Object { $_.DirectoryName } | Sort-Object -Unique)
if ($dllDirectories.Count -eq 0) { throw 'No NVIDIA runtime DLLs were found under CudaRuntimeRoot.' }
foreach ($required in @('cublas64_13.dll', 'cublasLt64_13.dll', 'cudnn64_9.dll')) {
    $found = $false
    foreach ($directory in $dllDirectories) {
        if (Test-Path -LiteralPath (Join-Path $directory $required) -PathType Leaf) { $found = $true; break }
    }
    if (-not $found) { throw "Required CUDA provider dependency is missing: $required" }
}
$previousPath = $env:PATH
try {
    # Only this PowerShell process and its child receive the dependency search path.
    # No global PATH, driver, system directory or Python environment is modified.
    $env:PATH = ($dllDirectories -join [IO.Path]::PathSeparator) + [IO.Path]::PathSeparator + $previousPath
    Write-Output 'Starting AccessRelay with the scoped CUDA runtime. Automatic release still requires validated model evidence.'
    & $resolvedApp @AppArguments
}
finally {
    $env:PATH = $previousPath
}
