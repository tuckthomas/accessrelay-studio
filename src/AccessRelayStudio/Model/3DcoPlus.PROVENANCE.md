# DualShock 4 and Switch Pro model provenance

The meshes for `DualShock4.glb` and `SwitchPro.glb` were created by Larf for 3D Controller Overlay. The source files come from the MIT-licensed 3Dco+ fork at revision `73b3a602a0b2afeb2fc7a2e9db940f9d2004ef9c`. [Original project](https://github.com/larfingshnew/3d-controller-overlay) · [Pinned DualShock 4 source](https://github.com/Khyretos/3dco-plus/tree/73b3a602a0b2afeb2fc7a2e9db940f9d2004ef9c/assets/models/DualShock%204) · [Pinned Switch Pro source](https://github.com/Khyretos/3dco-plus/tree/73b3a602a0b2afeb2fc7a2e9db940f9d2004ef9c/assets/models/Switch%20Pro) · [MIT license](3DcoPlus.MIT.txt)

Studio's export applies the source part offsets, converts the coordinate system, normalizes the width and names 16 independent control pivots. It preserves the artist-authored normals on the adapted parts, uses new unmarked center buttons and adds positional face legends. The DualShock 4 touchpad remains visible as part of the illustration. Windows gamepad input for the model uses the standard control set shared by Studio.

To reproduce with Blender 4.5.15 LTS, run from this directory and pass the pinned archive's `assets/models` directory to the exporter.

```powershell
blender --background --factory-startup --disable-autoexec --python-exit-code 1 --python export_3dco_gamepads.py -- path/to/assets/models .
```

| Export | SHA-256 |
| --- | --- |
| DualShock4.glb | `46DBBF21B4FD2BD2CFE4F339C530729E685701BB49EDD39B68FC3E8EA8B67D78` |
| SwitchPro.glb | `4FA1F0AE8634102B9BC99A07A4E07FB2BB8ADE449BE4034E1F671A80091843F7` |
