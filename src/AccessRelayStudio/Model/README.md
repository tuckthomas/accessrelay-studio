# Controller models and schematic

AccessRelay Studio includes interactive Xbox One-style, Xbox Elite Series 2, DualShock 4 and Switch Pro illustrations. The 3D models animate independently named button, trigger and stick meshes. The Elite layout adds four articulated rear paddles. The native 2D schematic is available for the Elite layout and when the 3D viewer cannot load. It uses original vector outlines and interactive callouts.

Controller names identify compatible hardware or the layout being illustrated. AccessRelay Studio is an independent project and has no affiliation with Microsoft, Sony Interactive Entertainment or Nintendo.

## Xbox One-style model

`XboxOne.glb` is adapted from **Microsoft Xbox One.blend** by CasperH2O and contributors under CC0 1.0. The export groups meshes under 16 named control pivots, applies a black PBR finish and replaces the original center-button mark with an unmarked button. [Pinned model](https://github.com/CasperH2O/3DControllerModels/blob/f0d985618e7c74ca35d4c428228cb5263e3d471e/Models/Microsoft%20Xbox%20One.blend) · [CC0 legal code](XboxOne.CC0-1.0.txt)

## Xbox Elite Series 2 illustration

`EliteSeries2Articulated.glb` adapts the same housing with the Elite layout and four individually articulated paddles. The paddle meshes and additional controls originate from `EliteSeries2.glb`, a Studio-generated model with original procedural textures. `EliteSeries2.glb` is an input to the exporter.

## DualShock 4 and Switch Pro

`DualShock4.glb` and `SwitchPro.glb` use the MIT-licensed geometry from 3D Controller Overlay by Larf. The export uses unmarked center buttons and adds positional face-button labels. [Source revisions, license and rebuild instructions](3DcoPlus.PROVENANCE.md)

A connected DualSense uses the labeled DualShock 4 illustration with DualSense control names. The public source omits the previous dedicated DualSense mesh and its embedded textures. The controller input and bindings still use the connected DualSense.

## Rebuild the Xbox and Elite GLBs

Use Python 3 and Blender 4.5.15 LTS. Run the commands from this directory after downloading the pinned Blender model linked above.

```powershell
python generate_elite_series2.py
blender --background --factory-startup --disable-autoexec 'Microsoft Xbox One.blend' --python-exit-code 1 --python export_xbox_one.py -- XboxOne.glb
blender --background --factory-startup --disable-autoexec --python-exit-code 1 --python export_controller_variants.py -- EliteSeries2Articulated.glb XboxOne.glb EliteSeries2.glb
```

Khronos glTF Validator reported zero errors and zero warnings for these exports. `XboxOne.glb` contains 16 control nodes. The two Elite files each contain 20.
