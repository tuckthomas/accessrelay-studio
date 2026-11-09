# Bundled USB/IP transport

AccessRelay distributes the x64 USB/IP host controller from `usbip-win2` 0.9.8.1 and its client and device setup utilities. The `USBip-0.9.8.1-x64.exe` release archive has SHA-256 `38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A`. The seven files below were unpacked unchanged without executing the release installer. [Pinned upstream release](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.1)

| File | SHA-256 |
| --- | --- |
| usbip2_ude.inf | `1AE9E8DC497929DE1A6BE1291AE1DCA6A19E7FD6C0351E8C266F65DD91F15073` |
| usbip2_ude.sys | `ABD4FC43DCE40E027E4ACC884CBFE92433E026206F6565C5EE5284A39441169A` |
| usbip2_ude.cat | `84DD6CC5985857ADF1514A9EE202210FF3EE1384C9BE40FD49D6810E23E25F3A` |
| devnode.exe | `CCBF1813C7EF33303A7FEBE6E288F27BFA9B0EA189D1BAAFCBABF2ADA7105964` |
| libusbip.dll | `F123EF5D288B817A890CBE39A3C2D212B3D8B1765C691C8703CCB76C7337986C` |
| resources.dll | `4BFC862E5891D9B4C12A8EBD43D3BA871EBF94B65F7A00C22527C905949E0FFF` |
| usbip.exe | `817DA219FA153B4B37C3BC1AA8FCC63F33E046188C57CC3DD2C60A6EF0883C56` |

Windows SDK SignTool verified both the INF and SYS against the catalog using its `/kp` kernel-mode policy. The catalog is signed by Microsoft Windows Hardware Compatibility Publisher with a Microsoft timestamp dated September 26, 2026. The two utilities have valid Authenticode signatures from Cloudyne Systems. The bundled DLLs are pinned by the archive and file hashes. [USB/IP BSD 2-Clause license](USBIP-WIN2.BSD-2-Clause.txt) · [CLI11 BSD 3-Clause license](CLI11.BSD-3-Clause.txt)

The app installs only the root virtual host controller and its INF. Windows elevates the in-box setup launcher, which checks the package again after staging it in a temporary administrator-only folder. The command follows upstream's host controller registration step. The upstream USB hub filter, server service, GUI and installer are absent. An existing running host controller is reused. Windows loads its in-box USB Audio 2.0 class driver for the emulated recording device. [Upstream installer source](https://github.com/vadimgrn/usbip-win2/blob/v.0.9.8.1/userspace/innosetup/setup.iss)

The upstream executables use the Microsoft Visual C++ x64 runtime. Their non-Windows runtime imports are `msvcp140.dll`, `msvcp140_atomic_wait.dll`, `vcruntime140.dll` and `vcruntime140_1.dll`. The copies in Windows System32 are used when present. [Microsoft C++ runtime deployment](https://learn.microsoft.com/en-us/cpp/windows/deployment-in-visual-cpp?view=msvc-170)
