#Requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Default')]
param(
    [Parameter(Mandatory, ParameterSetName = 'AssignedMicrophone')]
    [string] $MicrophoneUsbId,

    [Parameter(Mandatory, ParameterSetName = 'PrivateMicrophone')]
    [switch] $PrivateTestMicrophone,

    [string] $SignToolPath,
    [string] $TargetDirectory
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'Build the virtual devices on x64 Windows with the MSVC Rust toolchain.'
}

$micIdentity = $null
if ($PrivateTestMicrophone) {
    $micIdentity = '1209:0001 PRIVATE-TEST'
    Write-Warning 'The test VID/PID 1209:0001 is shared and is for private in-house testing only. Do not redistribute this build.'
}
elseif ($PSBoundParameters.ContainsKey('MicrophoneUsbId')) {
    if ($MicrophoneUsbId -notmatch '\A[0-9a-fA-F]{4}:[0-9a-fA-F]{4}\z') {
        throw 'Supply the assigned USB identity as four hexadecimal VID digits, a colon, and four hexadecimal PID digits.'
    }
    $vendorId = [Convert]::ToUInt16($MicrophoneUsbId.Substring(0, 4), 16)
    $productId = [Convert]::ToUInt16($MicrophoneUsbId.Substring(5, 4), 16)
    if ($vendorId -in @(0, 0xFFFF) -or $productId -in @(0, 0xFFFF) -or ($vendorId -eq 0x1209 -and $productId -lt 0x2000)) {
        throw 'The USB identity is reserved. Use a VID/PID you are authorized to distribute, or -PrivateTestMicrophone for local in-house testing.'
    }
    $micIdentity = $MicrophoneUsbId.ToUpperInvariant()
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$audio = Join-Path $repoRoot 'src/AccessRelayStudio/Audio'
$package = Join-Path $audio 'VirtualMicDriver/Package'
$hostDirectory = Join-Path $audio 'VirtualMicHost'
$manifest = Join-Path $hostDirectory 'Cargo.toml'
$identityPath = Join-Path $hostDirectory 'AccessRelay.VirtualMicUsbId.txt'
$hashes = [ordered]@{
    'usbip2_ude.inf' = '1AE9E8DC497929DE1A6BE1291AE1DCA6A19E7FD6C0351E8C266F65DD91F15073'
    'usbip2_ude.sys' = 'ABD4FC43DCE40E027E4ACC884CBFE92433E026206F6565C5EE5284A39441169A'
    'usbip2_ude.cat' = '84DD6CC5985857ADF1514A9EE202210FF3EE1384C9BE40FD49D6810E23E25F3A'
    'devnode.exe'   = 'CCBF1813C7EF33303A7FEBE6E288F27BFA9B0EA189D1BAAFCBABF2ADA7105964'
    'libusbip.dll'  = 'F123EF5D288B817A890CBE39A3C2D212B3D8B1765C691C8703CCB76C7337986C'
    'resources.dll'= '4BFC862E5891D9B4C12A8EBD43D3BA871EBF94B65F7A00C22527C905949E0FFF'
    'usbip.exe'     = '817DA219FA153B4B37C3BC1AA8FCC63F33E046188C57CC3DD2C60A6EF0883C56'
}

foreach ($name in $hashes.Keys) {
    $file = Join-Path $package $name
    if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -cne $hashes[$name]) {
        throw "Pinned USB/IP package file '$name' is missing or has the wrong SHA-256. Restore the original file before building."
    }
}

if (!$SignToolPath) {
    $SignToolPath = (Get-Command signtool.exe -CommandType Application -ErrorAction SilentlyContinue).Source
}
if (!$SignToolPath) {
    $kitBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $SignToolPath = Get-ChildItem -LiteralPath $kitBin -Directory -Filter '10.*' -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (!$SignToolPath -or !(Test-Path -LiteralPath $SignToolPath -PathType Leaf)) {
    throw 'Install Windows SDK Signing Tools or pass -SignToolPath with the SDK SignTool.exe path.'
}

foreach ($name in 'devnode.exe', 'usbip.exe') {
    & $SignToolPath verify /pa (Join-Path $package $name)
    if ($LASTEXITCODE -ne 0) { throw "USB/IP Authenticode verification failed for $name." }
}
$catalog = Join-Path $package 'usbip2_ude.cat'
foreach ($name in 'usbip2_ude.inf', 'usbip2_ude.sys') {
    & $SignToolPath verify /kp /c $catalog (Join-Path $package $name)
    if ($LASTEXITCODE -ne 0) { throw "USB/IP driver catalog verification failed for $name." }
}

$target = 'x86_64-pc-windows-msvc'
if (!$TargetDirectory) { $TargetDirectory = Join-Path $repoRoot 'artifacts/virtual-devices' }
if (![IO.Path]::IsPathFullyQualified($TargetDirectory)) { throw 'Pass an absolute path for -TargetDirectory.' }
$savedRustFlags = $env:RUSTFLAGS
$savedEncodedFlags = $env:CARGO_ENCODED_RUSTFLAGS
try {
    $env:RUSTFLAGS = '-C target-feature=+crt-static'
    $env:CARGO_ENCODED_RUSTFLAGS = $null
    cargo test --locked --manifest-path $manifest --target $target --target-dir $TargetDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Virtual device tests failed.' }
    cargo build --locked --release --manifest-path $manifest --target $target --target-dir $TargetDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Virtual device release build failed.' }
}
finally {
    $env:RUSTFLAGS = $savedRustFlags
    $env:CARGO_ENCODED_RUSTFLAGS = $savedEncodedFlags
}

$release = Join-Path $TargetDirectory "$target/release"
Copy-Item -LiteralPath (Join-Path $release 'playsignal-virtual-mic-host.exe') -Destination (Join-Path $hostDirectory 'AccessRelay.VirtualMicHost.exe')
Copy-Item -LiteralPath (Join-Path $release 'accessrelay-virtual-xbox-host.exe') -Destination (Join-Path $hostDirectory 'AccessRelay.VirtualXboxHost.exe')
if ($micIdentity) {
    [IO.File]::WriteAllText($identityPath, "$micIdentity`n")
    Write-Host "Built both helpers and configured the virtual microphone for $micIdentity."
}
else {
    [IO.File]::Delete($identityPath)
    Write-Host 'Built both helpers. Virtual microphone output needs an assigned USB identity or explicit private test setup.'
}
