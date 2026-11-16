<#
Inspect is read-only. Apply requires an installed HidHide driver and an explicit
machine-specific plan. It adds only the listed controller paths and Studio app.
Restore removes only entries added by this script and preserves unrelated entries.
The controller stack must be rebuilt separately before isolation is verified.
Official CLI reference: https://docs.nefarius.at/projects/HidHide/CLI-Usage/
#>
[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Apply', 'Restore')][string]$Mode = 'Inspect',
    [Parameter(Mandatory)][string]$PlanPath
)
$ErrorActionPreference = 'Stop'
$planFile = (Resolve-Path -LiteralPath $PlanPath).Path
$plan = Get-Content -LiteralPath $planFile -Raw | ConvertFrom-Json
$backupPath = Join-Path (Split-Path -Parent $planFile) 'isolation-before.json'
$application = [IO.Path]::GetFullPath($plan.application)
if (-not (Test-Path -LiteralPath $application -PathType Leaf) -or [IO.Path]::GetExtension($application) -ne '.exe') {
    throw 'The planned Studio executable is missing.'
}
$deviceIds = @($plan.deviceIds)
if ($deviceIds.Count -lt 2 -or $deviceIds.Count -gt 4 -or $plan.baseDeviceId -notmatch '^USB\\VID_045E&PID_0B00\\') {
    throw 'This plan must identify the verified wired Elite Series 2 and its gamepad interfaces.'
}
foreach ($instance in $deviceIds) {
    if ($instance -notmatch '^(HID|USB)\\VID_045E&PID_(0B00|02FF)([&\\])') {
        throw "Refusing a device outside the planned Xbox gamepad interfaces: $instance"
    }
}
$cliCandidates = @(
    $(if ($plan.PSObject.Properties['installDirectory']) { Join-Path $plan.installDirectory 'HidHideCLI.exe' }),
    $(if ($plan.PSObject.Properties['installDirectory']) { Join-Path $plan.installDirectory 'x64/HidHideCLI.exe' }),
    (Join-Path $PSScriptRoot 'HidHide/HidHideCLI.exe'),
    (Join-Path $PSScriptRoot 'HidHide/x64/HidHideCLI.exe'),
    (Join-Path $env:ProgramFiles 'Nefarius Software Solutions\HidHide\HidHideCLI.exe'),
    (Join-Path $env:ProgramFiles 'Nefarius Software Solutions\HidHide\x64\HidHideCLI.exe')
)
$cli = $cliCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
# The pinned CLI's display conversion matches HarddiskVolume1 as a prefix of
# HarddiskVolume10. Verify the actual whitelist through the documented read IOCTL
# instead of trusting that display path. Configuration writes still use the CLI.
if (-not ('AccessRelay.HidHideReadback' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace AccessRelay {
 public static class HidHideReadback {
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
  [DllImport("kernel32.dll", SetLastError=true)]
  static extern bool DeviceIoControl(SafeFileHandle handle,uint code,IntPtr input,uint inputBytes,byte[] output,uint outputBytes,out uint returned,IntPtr overlapped);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
  static extern uint QueryDosDevice(string name,StringBuilder target,int length);
  public static string KernelPath(string path) {
   if(path.Length<3 || path[1]!=':') throw new ArgumentException("A drive-qualified application path is required.");
   var target=new StringBuilder(32768);
   if(QueryDosDevice(path.Substring(0,2),target,target.Capacity)==0) throw new Win32Exception(Marshal.GetLastWin32Error());
   return target.ToString()+path.Substring(2);
  }
  public static string[] Whitelist() {
   using(var handle=CreateFile(@"\\.\HidHide",0x80000000,7,IntPtr.Zero,3,0,IntPtr.Zero)) {
    if(handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    uint returned;
    // CTL_CODE(32769, 2048, METHOD_BUFFERED, FILE_READ_DATA), upstream v1.5.230.0.
    if(!DeviceIoControl(handle,0x80016000,IntPtr.Zero,0,null,0,out returned,IntPtr.Zero))
     throw new Win32Exception(Marshal.GetLastWin32Error());
    if(returned<2 || returned>262144 || returned%2!=0) throw new InvalidOperationException("Invalid HidHide whitelist size.");
    var buffer=new byte[returned];
    if(!DeviceIoControl(handle,0x80016000,IntPtr.Zero,0,buffer,(uint)buffer.Length,out returned,IntPtr.Zero))
     throw new Win32Exception(Marshal.GetLastWin32Error());
    return Encoding.Unicode.GetString(buffer,0,(int)returned).Split(new[]{'\0'},StringSplitOptions.RemoveEmptyEntries);
   }
  }
 }
}
'@
}
$kernelApplication = [AccessRelay.HidHideReadback]::KernelPath($application)
function Invoke-HidHide([string[]]$Arguments) {
    $result = @(& $cli @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "HidHide failed: $($result -join ' ')" }
    return $result | ForEach-Object { $_.ToString().Trim() }
}
function Read-HidHide {
    $lines = @(Invoke-HidHide -Arguments @('--cloak-state', '--inv-state', '--dev-list', '--app-list'))
    return [pscustomobject]@{
        lines = $lines
        cloaked = $lines -contains '--cloak-on'
        inverse = $lines -contains '--inv-on'
        devices = @($lines | ForEach-Object { if ($_ -match '^--dev-hide "(.+)"$') { $Matches[1] } })
        applications = @($lines | ForEach-Object { if ($_ -match '^--app-reg "(.+)"$') { $Matches[1] } })
        nativeApplications = @([AccessRelay.HidHideReadback]::Whitelist())
    }
}
function Restore-OwnedEntries($before) {
    foreach ($instance in @($before.addedDevices)) { $null = Invoke-HidHide -Arguments @('--dev-unhide', $instance) }
    if ($before.addedApplication) { $null = Invoke-HidHide -Arguments @('--app-unreg', $application) }
    $current = Read-HidHide
    # Do not disable another tool's newly configured hidden devices.
    if (-not $before.cloaked -and $current.devices.Count -eq 0) { $null = Invoke-HidHide -Arguments @('--cloak-off') }
    return Read-HidHide
}
if ($Mode -eq 'Inspect') {
    $driver = Get-Service -Name HidHide -ErrorAction SilentlyContinue
    [pscustomobject]@{
        mode = 'Read-only inspection'; clientToolsPresent = [bool]$cli
        driverInstalled = [bool]$driver; driverRunning = $driver -and $driver.Status -eq 'Running'
        application = $application; baseDeviceId = $plan.baseDeviceId; deviceIds = $deviceIds
        configuration = $(if ($cli -and $driver -and $driver.Status -eq 'Running') { Read-HidHide } else { $null })
        rollback = 'Run this script with -Mode Restore and the same plan.'
        gameplayIsolationVerified = $false
        verification = 'Check fresh Studio input, then stop virtual output and verify physical input is blocked in the destination application.'
    } | ConvertTo-Json -Depth 5
    return
}
if (-not $cli) { throw 'Install the signed official HidHide driver before applying this plan.' }
if ($Mode -eq 'Restore') {
    if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) { throw 'No saved isolation state exists for this plan.' }
    $before = Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json
    if ($before.application -ne $application) { throw 'The saved state belongs to a different application.' }
    if (@($before.addedDevices | Where-Object { $deviceIds -notcontains $_ }).Count) { throw 'The saved state contains devices outside this plan.' }
    $restored = Restore-OwnedEntries $before
    $restored | ConvertTo-Json -Depth 5
    return
}
if (Test-Path -LiteralPath $backupPath) { throw 'A saved isolation state already exists. Inspect or restore it before applying again.' }
$base = Get-PnpDevice -InstanceId $plan.baseDeviceId -PresentOnly
if ($base.Class -ne 'XboxComposite' -or $base.Status -ne 'OK') { throw 'The planned physical Xbox controller is not connected and healthy.' }
foreach ($instance in $deviceIds) {
    $device = Get-PnpDevice -InstanceId $instance -PresentOnly
    $container = (Get-PnpDeviceProperty -InstanceId $instance -KeyName DEVPKEY_Device_ContainerId).Data.ToString()
    if ($device.Status -ne 'OK' -or $container.Trim('{}') -ne $plan.containerId.Trim('{}')) {
        throw "The planned path no longer belongs to the verified controller: $instance"
    }
}
$current = Read-HidHide
if ($current.inverse) { throw 'Inverse cloaking is enabled. Refusing to change another tool''s global visibility policy.' }
if (-not $current.cloaked -and $current.devices.Count -gt 0) { throw 'Enabling cloaking would affect pre-existing hidden-device entries. Review them first.' }
$before = [pscustomobject]@{
    application = $application; cloaked = $current.cloaked; rawState = $current.lines
    addedApplication = $current.nativeApplications -notcontains $kernelApplication
    addedDevices = @($deviceIds | Where-Object { $current.devices -notcontains $_ })
}
$before | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $backupPath -Encoding utf8
try {
    $null = Invoke-HidHide -Arguments @('--app-reg', $application)
    foreach ($instance in $deviceIds) { $null = Invoke-HidHide -Arguments @('--dev-hide', $instance) }
    $null = Invoke-HidHide -Arguments @('--cloak-on')
    $after = Read-HidHide
    if (-not $after.cloaked -or $after.nativeApplications -notcontains $kernelApplication -or @($deviceIds | Where-Object { $after.devices -notcontains $_ }).Count) {
        throw 'The requested isolation configuration could not be verified.'
    }
    $after | ConvertTo-Json -Depth 5
    Write-Output 'Configuration applied. Rebuild the controller stack and verify Studio input plus Xbox isolation before declaring success.'
} catch {
    try { $null = Restore-OwnedEntries $before } catch { Write-Error "Isolation rollback needs attention: $_" -ErrorAction Continue }
    throw
}
