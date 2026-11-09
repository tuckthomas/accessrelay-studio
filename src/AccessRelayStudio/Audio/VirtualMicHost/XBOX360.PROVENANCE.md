# AccessRelay virtual Xbox controller host

`AccessRelay.VirtualXboxHost.exe` presents a wired Xbox 360-compatible controller through a loopback USB/IP connection to the existing signed Windows host controller. Windows selects its own XUSB driver. The helper binds only to `127.0.0.1` on an automatically assigned TCP port and detaches only its own USB/IP port when stdin closes.

The USB configuration descriptor and initial interrupt status packets come from [ViGEmBus `sys/XusbPdo.cpp` at `d986e1d93708ec9b11049542fa6027272cce716c`](https://github.com/nefarius/ViGEmBus/blob/d986e1d93708ec9b11049542fa6027272cce716c/sys/XusbPdo.cpp). The [BSD-3-Clause license](ViGEmBus.LICENSE.txt) accompanies the derived bytes. The initial gamepad report uses neutral stick positions. The XUSB request handlers and compatible ID descriptor follow Microsoft's [XUSB request definitions](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xusbi/6e2478bf-d8f0-4e19-a489-7989a34918d1) and [OS descriptor format](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-xusbi/601a3107-6583-4e80-867e-118829cb573e). The helper targets Windows XInput.

The host uses the same [MIT-licensed USB/IP transport](https://github.com/network-audio/usbip-audio/tree/58032ab0664ffec1d0c178f40a3a69359c21f597) as the virtual microphone. Notices for the transport and its dependencies accompany both binaries.

The helper receives the absolute path to the bundled `usbip.exe` as its sole argument. After Windows first polls the gamepad interrupt endpoint, the helper writes `READY` to stdout. Stdin is a sequence of 12-byte little-endian XInput states containing the button mask (`uint16`), left and right trigger (`uint8` each), and left X, left Y, right X and right Y (`int16` each). The helper sends distinct states in order as Windows polls. It disconnects if the 64-report queue fills or its oldest report waits 250 ms. The stall diagnostic goes to stderr.

Build and test with an x64 MSVC Rust toolchain from the repository root.

```powershell
./scripts/Build-VirtualDevices.ps1
```
