[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $PSScriptRoot '../AccessRelayStudio-Beta'),
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '../artifacts/AutoCaptureModels')
)
$ErrorActionPreference = 'Stop'
# A PowerShell 7 parent can pass its module path to Windows PowerShell during MSBuild.
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$cacheRoot = [IO.Path]::GetFullPath($CacheDirectory)
$modelRoot = Join-Path ([IO.Path]::GetFullPath($InstallDirectory)) 'CaptureModels/nba2k27-automatic-v1'
New-Item -ItemType Directory -Force -Path $cacheRoot,$modelRoot | Out-Null
$assets = @(
    @{ Name='rtmo-t.zip'; Url='https://download.openmmlab.com/mmpose/v1/projects/rtmo/onnx_sdk/rtmo-t_8xb32-600e_body7-416x416-f48f75cb_20231219.zip'; Hash='6c71dea1d9d948917f2c562d58c881e4a11d7c8ce335d2920bcaa73d75e7c898' },
    @{ Name='text-det.onnx'; Url='https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv4/det/en_PP-OCRv3_det_mobile.onnx'; Hash='ea07c15d38ac40cd69da3c493444ec75b44ff23840553ff8ba102c1219ed39c2' },
    @{ Name='text-rec.onnx'; Url='https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv4/rec/en_PP-OCRv4_rec_mobile.onnx'; Hash='e8770c967605983d1570cdf5352041dfb68fa0c21664f49f47b155abd3e0e318' }
)
foreach ($asset in $assets) {
    $file = Join-Path $cacheRoot $asset.Name
    if (-not (Test-Path -LiteralPath $file)) {
        $download = $file + '.' + [guid]::NewGuid().ToString('N') + '.download'
        Invoke-WebRequest -Uri $asset.Url -OutFile $download
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $asset.Hash) { throw "Capture download hash mismatch: $($asset.Name)" }
        Move-Item -LiteralPath $download -Destination $file -Force
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $asset.Hash) { throw "Capture asset hash mismatch: $($asset.Name)" }
}
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $cacheRoot 'rtmo-t.zip'))
try {
    $entry = $archive.GetEntry('end2end.onnx')
    if (-not $entry -or $entry.Length -gt 128MB) { throw 'Invalid pose archive entry.' }
    $captureInput = $entry.Open()
    try {
        $captureOutput = [IO.File]::Create((Join-Path $cacheRoot 'pose.onnx'))
        try { $captureInput.CopyTo($captureOutput) } finally { $captureOutput.Dispose() }
    } finally { $captureInput.Dispose() }
} finally { $archive.Dispose() }
if ((Get-FileHash -LiteralPath (Join-Path $cacheRoot 'pose.onnx')).Hash -ne '20aad6e2e42359cac1c5b4a0b2da00e29bfe91a72a782fdcf287d273a04c1b24') { throw 'Pose model hash mismatch.' }
foreach ($name in @('pose.onnx','text-det.onnx','text-rec.onnx')) { Copy-Item -LiteralPath (Join-Path $cacheRoot $name) -Destination (Join-Path $modelRoot $name) -Force }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../docs/capture-models.md') -Destination (Join-Path $modelRoot 'NOTICE.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../docs/third-party/capture-models-APACHE-2.0.txt') -Destination (Join-Path $modelRoot 'LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../docs/third-party/capture-models-NOTICE.txt') -Destination (Join-Path $modelRoot 'THIRD-PARTY-NOTICES.txt') -Force
$manifest = @{
    schemaVersion = 1
    modelSet = 'nba2k27-automatic-v1'
    license = 'Apache-2.0'
    files = @(
        @{ name = 'pose.onnx'; source = $assets[0].Url; archiveEntry = 'end2end.onnx'; sha256 = '20aad6e2e42359cac1c5b4a0b2da00e29bfe91a72a782fdcf287d273a04c1b24' },
        @{ name = 'text-det.onnx'; source = $assets[1].Url; sha256 = $assets[1].Hash },
        @{ name = 'text-rec.onnx'; source = $assets[2].Url; sha256 = $assets[2].Hash }
    )
}
[IO.File]::WriteAllText((Join-Path $modelRoot 'models.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Write-Output "SUCCESS: verified pose/OCR assets installed in $modelRoot"
