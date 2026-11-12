[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $PSScriptRoot '../AccessRelayStudio-Beta'),
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '../artifacts/BetaBuild'),
    [string]$LegacyRunDirectory,
    [string]$CudaRuntimeRoot,
    [switch]$CreateDesktopShortcut,
    [switch]$DeferRunActivation,
    [switch]$ActivateOnly
)

$ErrorActionPreference = 'Stop'

function Test-SameOrChildPath([string]$Candidate, [string]$Parent) {
    return $Candidate.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or
        $Candidate.StartsWith($Parent + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Copy-OptionalAssetTree([string]$Source, [string]$Destination) {
    $sourceItem = Get-Item -LiteralPath $Source -Force
    $destinationItem = Get-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
    if ($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        # Enumerate no children of a link. Copy its identity, including a relative symlink target,
        # rather than copying model weights through a junction into another installation.
        if ($sourceItem.LinkType -notin @('Junction', 'SymbolicLink') -or @($sourceItem.Target).Count -ne 1 -or
            [string]::IsNullOrWhiteSpace(@($sourceItem.Target)[0])) {
            throw "Unsupported optional-asset reparse point: $Source"
        }
        $target = [string]@($sourceItem.Target)[0]
        if ($destinationItem) {
            if ($destinationItem.LinkType -eq $sourceItem.LinkType -and
                @($destinationItem.Target).Count -eq 1 -and
                ([string]@($destinationItem.Target)[0]).Replace('/', '\').Equals($target.Replace('/', '\'), [StringComparison]::OrdinalIgnoreCase)) { return }
            throw "Optional-asset link destination already exists with a different identity: $Destination. It was left unchanged."
        }
        New-Item -ItemType $sourceItem.LinkType -Path $Destination -Target $target | Out-Null
        return
    }
    # A stale destination link must not redirect regular-file copies outside this installation.
    if ($destinationItem -and ($destinationItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Optional-asset destination is a link but its source is not: $Destination. It was left unchanged."
    }
    if ($sourceItem.PSIsContainer) {
        if ($destinationItem -and -not $destinationItem.PSIsContainer) {
            throw "Optional-asset directory destination is an existing file: $Destination"
        }
        if (-not $destinationItem) { New-Item -ItemType Directory -Path $Destination | Out-Null }
        foreach ($child in Get-ChildItem -LiteralPath $Source -Force) {
            Copy-OptionalAssetTree -Source $child.FullName -Destination (Join-Path $Destination $child.Name)
        }
    }
    else {
        if ($destinationItem -and $destinationItem.PSIsContainer) {
            throw "Optional-asset file destination is an existing directory: $Destination"
        }
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
    }
}

$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\', '/')
$build = [IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\', '/')
$project = Join-Path $PSScriptRoot '../src/AccessRelayStudio/AccessRelayStudio.csproj'
$legacy = if ($LegacyRunDirectory) { [IO.Path]::GetFullPath($LegacyRunDirectory).TrimEnd('\', '/') } else { $null }
if ($ActivateOnly -and ($DeferRunActivation -or $CudaRuntimeRoot)) {
    throw 'ActivateOnly cannot defer activation or move a CUDA runtime. Publish those assets first.'
}
if ($install -eq [IO.Path]::GetPathRoot($install).TrimEnd('\') -or
    (Test-SameOrChildPath $install $build) -or (Test-SameOrChildPath $build $install)) {
    throw 'Choose a dedicated installation directory and a separate build directory.'
}
if ($legacy -and ($legacy -eq [IO.Path]::GetPathRoot($legacy).TrimEnd('\') -or
    (Test-SameOrChildPath $install $legacy) -or (Test-SameOrChildPath $legacy $install) -or
    (Test-SameOrChildPath $build $legacy) -or (Test-SameOrChildPath $legacy $build))) {
    throw 'The legacy Run path must be a dedicated directory separate from the installation and build directories.'
}
foreach ($process in @(Get-Process AccessRelayStudio -ErrorAction SilentlyContinue)) {
    if (-not $ActivateOnly -and $process.Path -and ($process.Path.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase) -or
        ($legacy -and -not $DeferRunActivation -and $process.Path.StartsWith($legacy + '\', [StringComparison]::OrdinalIgnoreCase)))) {
        throw 'Close the running AccessRelay installation before publishing its beta replacement. No process was terminated.'
    }
}
New-Item -ItemType Directory -Path $install -Force | Out-Null

# Preserve separately installed voice weights/native helpers before publishing new app files.
$existingLegacy = if ($legacy) { Get-Item -LiteralPath $legacy -ErrorAction SilentlyContinue } else { $null }
$alreadyActive = $existingLegacy -and $existingLegacy.LinkType -and @($existingLegacy.Target)[0] -eq $install
if (-not $ActivateOnly -and $legacy -and (Test-Path -LiteralPath $legacy) -and -not $alreadyActive) {
    foreach ($asset in @('NeuralVoice', 'DialogueSeparation')) {
        $assetSource = Join-Path $legacy $asset
        $assetItem = Get-Item -LiteralPath $assetSource -Force -ErrorAction SilentlyContinue
        if ($assetItem -and $assetItem.PSIsContainer) {
            if ([IO.Path]::GetFullPath($assetSource) -ne [IO.Path]::GetFullPath((Join-Path $install $asset))) {
                Write-Output "Preserving optional assets: $asset"
                Copy-OptionalAssetTree -Source $assetSource -Destination (Join-Path $install $asset)
            }
        }
    }
}

if (-not $ActivateOnly) {
    & (Join-Path $PSScriptRoot 'Build-DesktopUi.ps1')
    # Keep each referenced project's intermediate files separate under the selected build folder.
    & dotnet publish $project -c Release --artifacts-path $build -o $install -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "Beta publication failed with exit code $LASTEXITCODE. The existing Run path was not changed." }
}
$executable = Join-Path $install 'AccessRelayStudio.exe'
$version = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
if ($version -notmatch '^1\.9\.0-beta\.') { throw "Expected the beta executable, found version $version." }

if (-not $ActivateOnly -and $legacy -and -not $alreadyActive) {
    $identity = Join-Path $legacy 'VirtualMicrophone/AccessRelay.VirtualMicUsbId.txt'
    if (Test-Path -LiteralPath $identity -PathType Leaf) {
        Copy-Item -LiteralPath $identity -Destination (Join-Path $install 'VirtualMicrophone/AccessRelay.VirtualMicUsbId.txt') -Force
    }
}

if ($CudaRuntimeRoot) {
    $cudaSource = (Resolve-Path -LiteralPath $CudaRuntimeRoot).Path.TrimEnd('\', '/')
    $cudaTarget = [IO.Path]::GetFullPath((Join-Path $install 'CudaRuntime'))
    # Verify the exact absolute source and destination before a recursive directory move.
    if (-not $cudaTarget.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $cudaSource -eq [IO.Path]::GetPathRoot($cudaSource).TrimEnd('\') -or
        $cudaTarget.StartsWith($cudaSource + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'CUDA runtime move must use a dedicated explicit source and a child of this beta installation.'
    }
    if ($cudaSource -ne $cudaTarget -and -not (Test-Path -LiteralPath $cudaTarget)) {
        Move-Item -LiteralPath $cudaSource -Destination $cudaTarget
        # Retain the prior scoped path for existing diagnostic commands and launchers.
        New-Item -ItemType Junction -Path $cudaSource -Target $cudaTarget | Out-Null
    }
}

$backup = $null
if ($legacy -and -not $DeferRunActivation) {
    foreach ($process in @(Get-Process AccessRelayStudio -ErrorAction SilentlyContinue)) {
        if ($process.Path -and $process.Path.StartsWith($legacy + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The beta is ready, but close the old Run application before switching its path. No process was terminated.'
        }
    }
    $parent = [IO.Path]::GetFullPath((Split-Path -Parent $legacy)).TrimEnd('\', '/')
    $legacyItem = Get-Item -LiteralPath $legacy -ErrorAction SilentlyContinue
    if ($legacyItem -and $legacyItem.LinkType) {
        if (@($legacyItem.Target)[0] -ne $install) {
            # Versioned installations can replace the launch link without touching the old
            # installation or its running process. Retain the old junction as a rollback path.
            if ($legacyItem.LinkType -ne 'Junction' -or @($legacyItem.Target).Count -ne 1) {
                throw 'The existing Run path is not a supported directory junction; it was left unchanged.'
            }
            $backup = [IO.Path]::GetFullPath((Join-Path $parent ('Run-Previous-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
            if ((Split-Path -Parent $legacy) -ne $parent -or (Split-Path -Parent $backup) -ne $parent -or
                $legacyItem.FullName -ne $legacy -or (Test-Path -LiteralPath $backup)) {
                throw 'The launch-junction backup failed its workspace containment check.'
            }
            # Rename only the verified junction entry. Never enumerate, move or delete its target.
            Rename-Item -LiteralPath $legacy -NewName (Split-Path -Leaf $backup)
            try { New-Item -ItemType Junction -Path $legacy -Target $install | Out-Null }
            catch {
                if (-not (Test-Path -LiteralPath $legacy)) {
                    Rename-Item -LiteralPath $backup -NewName (Split-Path -Leaf $legacy)
                }
                throw
            }
        }
    }
    else {
        if ($legacyItem) {
            $backup = [IO.Path]::GetFullPath((Join-Path $parent ('Run-Stable-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
            # Both resolved names must remain direct children of the explicitly selected parent.
            if ((Split-Path -Parent $legacy) -ne $parent -or (Split-Path -Parent $backup) -ne $parent -or
                (Test-Path -LiteralPath $backup) -or $install.StartsWith($legacy + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'The stable backup path failed its workspace containment check.'
            }
            Move-Item -LiteralPath $legacy -Destination $backup
        }
        try { New-Item -ItemType Junction -Path $legacy -Target $install | Out-Null }
        catch {
            if ($backup -and -not (Test-Path -LiteralPath $legacy)) {
                # Restore the already checked sibling path if link creation fails.
                Move-Item -LiteralPath $backup -Destination $legacy
            }
            throw
        }
    }
}

if ($CreateDesktopShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $desktop 'AccessRelay Studio Beta.lnk'))
    $shortcut.TargetPath = $executable; $shortcut.WorkingDirectory = $install
    $shortcut.Description = "AccessRelay Studio $version"; $shortcut.Save()
}
[pscustomobject]@{ Version = $version; Executable = $executable; RunPath = $legacy; RunActivationDeferred = [bool]$DeferRunActivation; StableBackup = $backup } | Format-List
