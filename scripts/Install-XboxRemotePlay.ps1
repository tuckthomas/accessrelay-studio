# Install the optional media runtime used by direct Xbox Remote Play.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitOperatingSystem -or $env:OS -ne 'Windows_NT') {
    throw 'Direct Xbox Remote Play requires 64-bit Windows.'
}
$package = 'gstreamer-1.0-msvc-x86_64-1.26.10.msi'
$uri = "https://gstreamer.freedesktop.org/data/pkg/windows/1.26.10/msvc/$package"
$expected = 'a863bf3faa49e9f33bd3cc42967b473482d4dc98655ed95cba1ac59f26fb0cfb'
$file = Join-Path ([IO.Path]::GetTempPath()) ("AccessRelay-" + [guid]::NewGuid() + '.msi')
try {
    curl.exe --fail --location --retry 3 --silent --show-error --output $file $uri
    if ($LASTEXITCODE -ne 0) { throw 'The GStreamer download failed.' }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) {
        throw 'The downloaded GStreamer installer did not match the published checksum.'
    }
    $installer = Start-Process msiexec.exe -ArgumentList @('/i', ('"' + $file + '"'), '/quiet', '/norestart', 'ADDLOCAL=ALL') -Wait -PassThru
    if ($installer.ExitCode -notin @(0, 3010)) { throw "GStreamer installation returned $($installer.ExitCode). Run the installer from an administrator PowerShell." }
    Write-Output 'GStreamer 1.26.10 installed. Restart Studio to open direct Xbox Remote Play.'
}
finally {
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
}
