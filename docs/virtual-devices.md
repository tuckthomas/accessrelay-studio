# Building virtual devices

AccessRelay uses two Rust helpers and a shared, signed USB/IP host controller for Windows x64. The controller helper presents an Xbox 360-compatible XInput device. The microphone helper presents a USB Audio Class 2 capture device that other Windows apps can select as **AccessRelay Virtual Microphone**.

Install PowerShell 7, the [x64 MSVC Rust toolchain](https://rustup.rs/), and the Visual Studio C++ build tools with the Windows SDK Signing Tools. From the repository root run the following command before building the .NET project.

```powershell
./scripts/Build-VirtualDevices.ps1
```

The script verifies the seven vendored USB/IP files against pinned SHA-256 values. It then verifies both utility signatures and checks that the INF and SYS are signed by the included catalog under Windows kernel-mode signing policy. It runs both helpers’ tests and builds their executables from `Cargo.lock`. It stages the helpers for the app without installing a driver or attaching a device. On Windows without the required MSVC runtime in System32, install the [official Microsoft Visual C++ x64 Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist).

For a checkout in a deeply nested folder, pass `-TargetDirectory` with a short absolute path for Cargo's build files.

The driver binaries are unmodified files from [usbip-win2 0.9.8.1](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.1). The [package provenance](../src/AccessRelayStudio/Audio/VirtualMicDriver/Package/USBIP-WIN2.PROVENANCE.md) records the release archive hash and all seven file hashes. The [USB/IP license](../src/AccessRelayStudio/Audio/VirtualMicDriver/Package/USBIP-WIN2.BSD-2-Clause.txt) and [CLI11 license](../src/AccessRelayStudio/Audio/VirtualMicDriver/Package/CLI11.BSD-3-Clause.txt) accompany the files. The [Xbox helper provenance](../src/AccessRelayStudio/Audio/VirtualMicHost/XBOX360.PROVENANCE.md) covers the ViGEmBus-derived descriptors.

## Virtual microphone USB identity

The default build supports virtual controller output and headphone preview. Virtual microphone output requires a USB identity. Use a VID/PID that you are authorized to distribute. The [pid.codes application process](https://pid.codes/howto/) requires a public repository with the device source and an open-source license.

```powershell
./scripts/Build-VirtualDevices.ps1 -MicrophoneUsbId $assignedUsbId
```

Set `$assignedUsbId` to the assigned VID and PID in four-digit hexadecimal `VID:PID` form. The script stages `AccessRelay.VirtualMicUsbId.txt`. The helper reads it before attaching, and Studio uses the same identity to find and exclude its virtual microphone from physical inputs. When changing identities or returning to the default build, use a fresh .NET build or publish directory so it contains the current payload.

For private in-house testing only, the [pid.codes test PID](https://pid.codes/1209/0001/) is available explicitly.

```powershell
./scripts/Build-VirtualDevices.ps1 -PrivateTestMicrophone
```

The private test VID/PID `1209:0001` is shared and unsuitable for distribution. Keep all builds produced with this option in-house. The generated file marks the identity `PRIVATE-TEST`. An unmarked test VID/PID is rejected.

## Windows setup

Use **Settings → Install virtual device driver** in Studio if the signed host controller is missing. Windows requests administrator approval for this installation. Once installed, Studio attaches and detaches its virtual devices from a standard account. The USB/IP connection stays on the local PC.
