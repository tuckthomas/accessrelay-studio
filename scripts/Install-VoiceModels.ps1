#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [Parameter(Mandatory)] [ValidateSet('MRX', 'MeanVC2', 'MioCodec')] [string[]] $Model,
    [string] $CacheDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AccessRelayStudio/AssetCache/voice'),
    [string] $Python = 'python'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'This setup command targets Windows x64.'
}

$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $publish 'AccessRelayStudio.exe') -PathType Leaf)) {
    throw 'Choose a published AccessRelay Studio folder containing AccessRelayStudio.exe.'
}

New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
$cache = (Resolve-Path -LiteralPath $CacheDirectory).Path
$audio = Join-Path $PSScriptRoot '../src/AccessRelayStudio/Audio'
$selected = @($Model | Select-Object -Unique)
$audioCppRelease = 'https://github.com/0xShug0/audio.cpp/releases/download/v0.9.4'
$audioCppDllHash = '0789DCAAF524070A25D5266E1326E60AA6BBFCB58DFAFDD8DE2C99171BCB30FD'
$mioUpstream = 'https://huggingface.co/Aratako/MioCodec-25Hz-44.1kHz-v2/resolve/67faba34153fe74e6665991c432a7327e23c5c1c'

function Assert-Hash([string] $Path, [string] $Expected) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $Expected) {
        throw "SHA-256 mismatch for $Path. Expected $Expected, got $actual."
    }
}

function Get-VerifiedDownload([string] $Name, [string] $Url, [string] $Sha256) {
    $path = Join-Path $cache $Name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Assert-Hash $path $Sha256
        return $path
    }

    $partial = Join-Path $cache ('.download-' + [Guid]::NewGuid().ToString('N'))
    try {
        Write-Host "Downloading $Name"
        Invoke-WebRequest -Uri $Url -OutFile $partial
        Assert-Hash $partial $Sha256
        Move-Item -LiteralPath $partial -Destination $path
        return $path
    } finally {
        if (Test-Path -LiteralPath $partial -PathType Leaf) { Remove-Item -LiteralPath $partial }
    }
}

function Invoke-Checked([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

$work = $null
try {
    if ('MeanVC2' -in $selected -or 'MioCodec' -in $selected) {
        $compiler = Get-Command cl.exe -ErrorAction SilentlyContinue
        if (-not $compiler -or ($env:VSCMD_ARG_TGT_ARCH -and $env:VSCMD_ARG_TGT_ARCH -ne 'x64')) {
            throw 'Run this command from an x64 MSVC Developer PowerShell or developer command prompt.'
        }

        $sdkName = 'audio-v0.9.4-lib-windows-x64-vulkan-portable.zip'
        $sdkZip = Get-VerifiedDownload $sdkName "$audioCppRelease/$sdkName" `
            '04536D037F665527C2BB9B2E5FBAEEAC53CAFBA82024CDA7A427C49DAE000A78'
        $work = Join-Path $cache ('build-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $work | Out-Null
        $sdk = Join-Path $work 'sdk'
        Expand-Archive -LiteralPath $sdkZip -DestinationPath $sdk
        $runtime = Join-Path $sdk 'libs/audiocpp.dll'
        Assert-Hash $runtime $audioCppDllHash
    }

    foreach ($package in $selected) {
        if ($package -eq 'MRX') {
            $name = 'mrx.int8.onnx'
            $weights = Get-VerifiedDownload $name "https://huggingface.co/audiojs/mrx/resolve/dedc823a0bea2ed085d19e8b7f32589d35169ee8/$name" `
                'D876C92D224F5D8CD2207D528B23369DA895A6DE71FB24132416FCB16F9F8424'
            $destination = Join-Path $publish 'DialogueSeparation'
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            Copy-Item -LiteralPath $weights, (Join-Path $audio 'DialogueSeparation/MRX.LICENSE.txt'), `
                (Join-Path $audio 'DialogueSeparation/README.md') -Destination $destination -Force
            Write-Host 'Installed MRX dialogue separation.'
            continue
        }

        $source = Join-Path $audio "NeuralHost/$package"
        $build = Join-Path $work $package
        New-Item -ItemType Directory -Path $build | Out-Null
        $executable = Join-Path $build "PlaySignal.${package}Host.exe"
        if ($package -eq 'MeanVC2') {
            $name = 'meanvc2-120ms-40ms-q4_k.gguf'
            $weights = Get-VerifiedDownload $name "https://huggingface.co/audio-cpp/audio.cpp-gguf/resolve/a27679cead37af35763d022bf632cd024ffaf482/MeanVC2-GGUF/$name" `
                '29E0B2F519C96242A72632A471EB18F3F3A0F82D699274A40A2974FB196538AA'
        } else {
            $converterName = 'audio-v0.9.4-bin-windows-x64-vulkan-portable.zip'
            $converterZip = Get-VerifiedDownload $converterName "$audioCppRelease/$converterName" `
                '295CA309160E59EC3CE3CFF0E07EDB6709E28D43B4C0A5AD76760938AE6A4E5C'
            $converter = Join-Path $work 'converter'
            Expand-Archive -LiteralPath $converterZip -DestinationPath $converter
            $mio = Get-VerifiedDownload 'miocodec-67faba3.safetensors' "$mioUpstream/model.safetensors" `
                '8E319EF2231BAD184F17CB73FD5A21B685C25C6C1622EF33ED9271187E81CD4A'
            $config = Get-VerifiedDownload 'miocodec-67faba3-config.yaml' "$mioUpstream/config.yaml" `
                'BFABFFFFAAA5709B8DC69585111EE3D53C1B0609C23D293CD1B4903EAFA5BEC1'
            $wavlm = Get-VerifiedDownload 'wavlm_base_plus.pth' 'https://download.pytorch.org/torchaudio/models/wavlm_base_plus.pth' `
                '136A3E720C04F2C77BF7A4DC6A3868B14D5A2C145A988114B733CB1A8428BE98'
            $mioConfig = Join-Path $build 'mio-config'
            New-Item -ItemType Directory -Path $mioConfig | Out-Null
            Copy-Item -LiteralPath $config -Destination (Join-Path $mioConfig 'config.yaml')
            $wavlmTensors = Join-Path $build 'wavlm-torchaudio.safetensors'
            Invoke-Checked $Python @((Join-Path $source 'convert_wavlm.py'), $wavlm, $wavlmTensors)
            $weights = Join-Path $build 'miocodec-25hz-44khz-v2-q8_0.gguf'
            $relativeMio = [IO.Path]::GetRelativePath($build, $mio)
            Push-Location -LiteralPath $build
            try {
                Invoke-Checked (Join-Path $converter 'audiocpp_gguf.exe') @(
                    '--input', "weights=$relativeMio", '--input', 'wavlm_weights=wavlm-torchaudio.safetensors', '--root', 'mio-config',
                    '--model-spec', (Join-Path $converter 'model_specs/miocodec.json'), '--family', 'miocodec',
                    '--type', 'q8_0', '--output', 'miocodec-25hz-44khz-v2-q8_0.gguf'
                )
            } finally { Pop-Location }
            Write-Host "Built MioCodec GGUF SHA-256 $((Get-FileHash -LiteralPath $weights -Algorithm SHA256).Hash)"
        }

        Invoke-Checked $compiler.Source @(
            '/nologo', '/EHsc', '/std:c++20', '/O2', '/MT', '/DNOMINMAX', '/DAUDIOCPP_USE_DLL',
            "/I$(Join-Path $sdk 'include')", "/Fo$(Join-Path $build 'host.obj')", "/Fe$executable",
            (Join-Path $source 'host.cpp'), '/link', (Join-Path $sdk 'libs/audiocpp.lib')
        )

        $destination = Join-Path $publish "NeuralVoice/$package"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath $runtime, $weights -Destination $destination -Force
        foreach ($notice in Get-ChildItem -LiteralPath $source -File -Filter '*.txt') {
            Copy-Item -LiteralPath $notice.FullName -Destination $destination -Force
        }
        Copy-Item -LiteralPath (Join-Path $source 'README.md') -Destination $destination -Force
        Copy-Item -LiteralPath $executable -Destination $destination -Force
        Write-Host "Installed $package. Restart AccessRelay Studio to refresh the model list."
    }
} finally {
    if ($work -and (Test-Path -LiteralPath $work -PathType Container)) {
        $resolvedWork = (Resolve-Path -LiteralPath $work).Path
        if ([IO.Path]::GetDirectoryName($resolvedWork) -eq $cache -and [IO.Path]::GetFileName($resolvedWork) -match '^build-[0-9a-f]{32}$') {
            Remove-Item -LiteralPath $resolvedWork -Recurse -Force
        }
    }
}
