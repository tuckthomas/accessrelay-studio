using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EliteScriptStudio.Audio;

internal static class VirtualMicDriver
{
    private const string HardwareId = @"ROOT\USBIP_WIN2\UDE";
    private const uint PresentByService = 0x102;
    private static readonly Guid HostControllerInterface = new("B4030C06-DC5F-4FCC-87EB-E5515A0935C0");
    private static readonly string PackageDirectory = Path.Combine(AppContext.BaseDirectory, "VirtualMicrophone");
    private static readonly string[] RuntimeFiles =
        ["msvcp140.dll", "msvcp140_atomic_wait.dll", "vcruntime140.dll", "vcruntime140_1.dll"];
    private static readonly (string File, string Sha256)[] PackageHashes =
    [
        ("usbip2_ude.inf", "1AE9E8DC497929DE1A6BE1291AE1DCA6A19E7FD6C0351E8C266F65DD91F15073"),
        ("usbip2_ude.sys", "ABD4FC43DCE40E027E4ACC884CBFE92433E026206F6565C5EE5284A39441169A"),
        ("usbip2_ude.cat", "84DD6CC5985857ADF1514A9EE202210FF3EE1384C9BE40FD49D6810E23E25F3A"),
        ("devnode.exe", "CCBF1813C7EF33303A7FEBE6E288F27BFA9B0EA189D1BAAFCBABF2ADA7105964"),
        ("libusbip.dll", "F123EF5D288B817A890CBE39A3C2D212B3D8B1765C691C8703CCB76C7337986C"),
        ("resources.dll", "4BFC862E5891D9B4C12A8EBD43D3BA871EBF94B65F7A00C22527C905949E0FFF"),
        ("usbip.exe", "817DA219FA153B4B37C3BC1AA8FCC63F33E046188C57CC3DD2C60A6EF0883C56")
    ];

    internal static string UsbipExecutable => Path.Combine(PackageDirectory, "usbip.exe");

    internal static bool IsAvailable =>
        CM_Get_Device_Interface_List_SizeW(out uint length, in HostControllerInterface, null, 0) == 0 && length > 1;

    internal static Task InstallAsync() => Task.Run(Install);

    private static void Install()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The bundled virtual device driver requires x64 Windows.");

        using var installLock = new Mutex(false, @"Local\PlaySignal.UsbipDriverSetup");
        try
        {
            if (!installLock.WaitOne(TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException("Another AccessRelay virtual device setup is already running.");
        }
        catch (AbandonedMutexException) { }

        try
        {
            if (IsAvailable)
                return;

            uint status = CM_Get_Device_ID_List_SizeW(out uint length, "usbip2_ude", PresentByService);
            if (status != 0)
                throw new InvalidOperationException($"Couldn’t check the existing USB/IP host controller (0x{status:X}).");
            if (length > 1)
                throw new InvalidOperationException("An existing USB/IP host controller is stopped. Check its status in Windows Device Manager before retrying.");

            InstallPackage();
        }
        finally { installLock.ReleaseMutex(); }
    }

    private static void InstallPackage()
    {
        if (RuntimeFiles.Any(file => !File.Exists(Path.Combine(Environment.SystemDirectory, file))))
            throw new InvalidOperationException("The bundled USB/IP setup utility needs the Microsoft Visual C++ x64 runtime.");

        // Keep the checked package files read-locked until the installer returns.
        var locks = new List<FileStream>();
        try
        {
            foreach (var (file, expected) in PackageHashes)
            {
                string path = Path.Combine(PackageDirectory, file);
                if (!File.Exists(path))
                    throw new InvalidOperationException($"The AccessRelay package is missing {file}.");
                var stream = File.OpenRead(path);
                locks.Add(stream);
                if (Convert.ToHexString(SHA256.HashData(stream)) != expected)
                    throw new InvalidOperationException($"The bundled virtual device file {file} did not pass its integrity check.");
            }

            using var installer = Process.Start(ElevatedInstaller()) ?? throw new InvalidOperationException("Couldn’t open the bundled virtual device driver installer.");
            installer.WaitForExit();
            if (installer.ExitCode != 0)
                throw new InvalidOperationException($"Windows driver setup failed (exit code 0x{installer.ExitCode:X8}).");

            var elapsed = Stopwatch.StartNew();
            while (!IsAvailable && elapsed.Elapsed < TimeSpan.FromSeconds(10))
                Thread.Sleep(100);
            if (!IsAvailable)
                throw new InvalidOperationException("Windows has installed the USB/IP transport, but it hasn’t started. Restart Windows and try again.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Virtual device driver setup was cancelled.", error);
        }
        finally
        {
            foreach (var stream in locks)
                stream.Dispose();
        }
    }

    private static ProcessStartInfo ElevatedInstaller()
    {
        // The elevated loader must never load a dependency from the portable app's writable directory.
        // Only the trusted inbox PowerShell is elevated. Its fixed command stages verified files under Program Files.
        string source = Convert.ToBase64String(Encoding.UTF8.GetBytes(PackageDirectory));
        string manifest = string.Join('\n', PackageHashes.Where(item => item.File != "usbip.exe")
            .Select(item => $"{item.File}|{item.Sha256}"));
        string hashes = Convert.ToBase64String(Encoding.UTF8.GetBytes(manifest));
        string script = $$"""
            $ErrorActionPreference = 'Stop'
            $PSModuleAutoLoadingPreference = 'None'
            $source = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{source}}'))
            $files = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{hashes}}')).Split([char]10)
            $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
            if (![IO.Directory]::Exists($programFiles)) { exit 1 }
            $stage = [IO.Path]::Combine($programFiles, ('PlaySignal-Usbip-' + [Guid]::NewGuid().ToString('N')))
            $directorySecurity = [Security.AccessControl.DirectorySecurity]::new()
            $directorySecurity.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
            $fileSecurity = [Security.AccessControl.FileSecurity]::new()
            $fileSecurity.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)')
            $env:Path = [Environment]::SystemDirectory
            [Environment]::CurrentDirectory = [Environment]::SystemDirectory
            $exitCode = 1
            $staged = $false
            try {
                if ([IO.Directory]::Exists($stage)) { exit 1 }
                [void][IO.Directory]::CreateDirectory($stage, $directorySecurity)
                if ([IO.File]::GetAttributes($stage) -band [IO.FileAttributes]::ReparsePoint) { exit 1 }
                $staged = $true
                foreach ($entry in $files) {
                    $name, $expected = $entry.Split('|')
                    $target = [IO.Path]::Combine($stage, $name)
                    $inputFile = [IO.File]::OpenRead([IO.Path]::Combine($source, $name))
                    try {
                        $outputFile = [IO.FileStream]::new($target, [IO.FileMode]::CreateNew,
                            [Security.AccessControl.FileSystemRights]::Write, [IO.FileShare]::None, 4096,
                            [IO.FileOptions]::None, $fileSecurity)
                        try { $inputFile.CopyTo($outputFile) }
                        finally { $outputFile.Dispose() }
                    }
                    finally { $inputFile.Dispose() }
                    $stream = [IO.File]::OpenRead($target)
                    $sha = [Security.Cryptography.SHA256]::Create()
                    try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
                    finally { $sha.Dispose(); $stream.Dispose() }
                    if ($actual -cne $expected) { exit 2 }
                }
                & ([IO.Path]::Combine($stage, 'devnode.exe')) install ([IO.Path]::Combine($stage, 'usbip2_ude.inf')) '{{HardwareId}}'
                $exitCode = $LASTEXITCODE
            }
            finally {
                if ($staged) {
                    foreach ($entry in $files) { [IO.File]::Delete([IO.Path]::Combine($stage, $entry.Split('|')[0])) }
                    [IO.Directory]::Delete($stage)
                }
            }
            exit $exitCode
            """;

        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return start;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, in Guid interfaceClass, string? deviceId, uint flags);
}
