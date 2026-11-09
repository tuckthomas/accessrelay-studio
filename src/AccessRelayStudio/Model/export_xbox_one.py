"""Prepare the CC0 Xbox One Blender source for Studio's 16 native controls."""

import sys
from math import pi
from pathlib import Path

import bpy
from mathutils import Matrix, Vector


SOURCE = "https://github.com/CasperH2O/3DControllerModels/tree/f0d985618e7c74ca35d4c428228cb5263e3d471e"

# Source axes are +X right, -Y front and +Z up. Blender's glTF export
# converts these into the viewer's +X right, +Z front and +Y up.
CONTROL_GEOMETRY = {
    "A": ("A", "A-Button", "A-Interior", "A-Interior2"),
    "B": ("B", "B-Button", "B-Interior", "B-Interior2"),
    "X": ("X", "X-Button", "X-Interior", "X-Interior2"),
    "Y": ("Y", "Y-Button", "Y-Interior", "Y-Interior2"),
    "DPadUp": ("DPadUp",),
    "DPadDown": ("DPadDown",),
    "DPadLeft": ("DPadLeft",),
    "DPadRight": ("DPadRight",),
    "View": ("Back", "BackSymbol"),
    "Menu": ("Start", "StartSymbol"),
    "LeftStick": ("LeftThumb", "Joystick-Left-Ring"),
    "RightStick": ("RightThumb", "Joystick-Right-Ring"),
    "LeftBumper": ("LeftShoulder",),
    "RightBumper": ("RightShoulder",),
    "LeftTrigger": ("Shoulder-Left-Trigger",),
    "RightTrigger": ("Shoulder-Right-Trigger",),
}

HINGES = {
    "LeftStick": (-3.90, -.80, 2.22),
    "RightStick": (2.00, -.80, -.11),
    "LeftTrigger": (-4.47, .31, 3.97),
    "RightTrigger": (4.47, .31, 3.97),
}


def matte(name, rgb, roughness, metal=0, alpha=1):
    material = bpy.data.materials.new(name)
    material.diffuse_color = (*rgb, alpha)
    material.use_nodes = True
    surface = material.node_tree.nodes.get("Principled BSDF")
    surface.inputs["Base Color"].default_value = (*rgb, alpha)
    surface.inputs["Roughness"].default_value = roughness
    surface.inputs["Metallic"].default_value = metal
    surface.inputs["Alpha"].default_value = alpha
    if alpha < 1:
        material.surface_render_method = "BLENDED"
    return material


def main(path):
    scene = bpy.context.scene
    objects = {item.name: item for item in scene.objects if item.type == "MESH"}
    expected = {source for sources in CONTROL_GEOMETRY.values() for source in sources}
    missing = expected - objects.keys()
    if missing:
        raise ValueError(f"Use the pinned Xbox One source. Missing {', '.join(sorted(missing))}.")

    finish = {
        "face": matte("Satin black top shell", (.024, .027, .031), .46),
        "rear": matte("Black rear shell", (.016, .018, .021), .57),
        "sides": matte("Rubber grip trim", (.011, .013, .016), .76),
        "dark": matte("Black recesses", (.006, .007, .009), .52),
        "button": matte("Graphite controls", (.019, .022, .026), .37),
        "grip": matte("Rubber stick caps", (.014, .016, .019), .71),
        "trim": matte("Metal port trim", (.13, .15, .18), .41, .62),
        "glass": matte("Clear tinted button caps", (.045, .049, .058), .20, alpha=.18),
        "symbol": matte("Silver control legends", (.50, .56, .61), .48),
        "A": matte("A green", (.20, .56, .054), .44),
        "B": matte("B red", (.62, .064, .051), .42),
        "X": matte("X blue", (.048, .29, .67), .43),
        "Y": matte("Y yellow", (.68, .43, .036), .43),
    }
    explicit_finish = {
        "MainBody": "face", "MainBodyTop": "face", "MainBodyBack": "rear",
        "BatteryDoor": "rear", "BatteryDoorInner": "dark", "MainBodySide": "sides",
        "LeftThumb": "grip", "RightThumb": "grip",
        "Joystick-Left-Ring": "grip", "Joystick-Right-Ring": "grip",
        "USBPortInner": "dark", "USBPortOuter": "trim",
    }

    def material_for(name):
        if name in finish:
            return finish[name]
        if name.endswith("-Button"):
            return finish["glass"]
        if "Interior" in name:
            return finish["dark"]
        if name.endswith("Symbol"):
            return finish["symbol"]
        return finish[explicit_finish.get(name, "button")]

    def bounds(names):
        corners = [objects[name].matrix_world @ Vector(corner) for name in names for corner in objects[name].bound_box]
        return (Vector(min(corner[axis] for corner in corners) for axis in range(3)),
                Vector(max(corner[axis] for corner in corners) for axis in range(3)))

    minimum, maximum = bounds(objects)
    scale = 1.968 / (maximum.x - minimum.x)
    center = (minimum + maximum) / 2
    normalize = Matrix.Scale(scale, 4) @ Matrix.Translation(-center)
    guide_min, guide_max = bounds(("LogoInner", "LogoOuter"))
    for source in ("LogoInner", "LogoOuter"):
        bpy.data.objects.remove(objects.pop(source), do_unlink=True)
    source_to_control = {name: control for control, names in CONTROL_GEOMETRY.items() for name in names}

    # Group all of a button's visible surfaces under a real scene pivot. The
    # original parts stay separate from the housing and retain their topology.
    for source, item in objects.items():
        item.name = "Mesh " + source

    groups = {}
    for control, names in CONTROL_GEOMETRY.items():
        low, high = bounds(names)
        pivot = Vector(HINGES[control]) if control in HINGES else (low + high) / 2
        group = bpy.data.objects.new(control, None)
        group.location = normalize @ pivot
        group["controlId"] = control
        scene.collection.objects.link(group)
        groups[control] = group

    for source, item in objects.items():
        world = item.matrix_world.copy()
        group = groups.get(source_to_control.get(source))
        origin = group.location if group else Vector()
        mesh = item.data.copy()
        mesh.transform(Matrix.Translation(-origin) @ normalize @ world)
        mesh.materials.clear()
        mesh.materials.append(material_for(source))
        for face in mesh.polygons:
            face.material_index = 0
        item.data = mesh
        item.animation_data_clear()
        item.parent = group
        item.matrix_parent_inverse = Matrix.Identity(4)
        item.matrix_basis = Matrix.Identity(4)

    for item in list(scene.objects):
        if item.type != "MESH" and item not in groups.values():
            bpy.data.objects.remove(item, do_unlink=True)

    guide_size = (guide_max - guide_min) * scale
    bpy.ops.mesh.primitive_cylinder_add(vertices=48, radius=min(guide_size.x, guide_size.z) / 2,
        depth=guide_size.y, rotation=(pi / 2, 0, 0), location=normalize @ ((guide_min + guide_max) / 2))
    guide = bpy.context.object
    guide.name = "Unmarked center button"
    guide.data.materials.append(finish["button"])

    if len(groups) != 16 or any(not group.children for group in groups.values()):
        raise ValueError("The source must have independent geometry for all 16 native controls.")

    scene["modelFamily"] = "Xbox One"
    scene["source"] = SOURCE
    scene["sourceLicense"] = "CC0-1.0"
    bpy.ops.export_scene.gltf(filepath=str(path), export_format="GLB", export_yup=True,
                             export_extras=True, export_animations=False)


if __name__ == "__main__":
    output = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
    main(output)
