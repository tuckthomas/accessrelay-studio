"""Export the articulated DualShock 4 and Switch Pro from pinned 3Dco+ OBJ files."""

import json
import math
from pathlib import Path
import sys

import bpy
from mathutils import Vector


CONTROLS = {
    "a_button": "A",
    "b_button": "B",
    "x_button": "X",
    "y_button": "Y",
    "start_button": "Menu",
    "back_button": "View",
    "left_trigger": "LeftTrigger",
    "right_trigger": "RightTrigger",
    "left_bumper": "LeftBumper",
    "right_bumper": "RightBumper",
    "left_stick": "LeftStick",
    "right_stick": "RightStick",
    "left_cap": "LeftStick",
    "right_cap": "RightStick",
    "dpad_up": "DPadUp",
    "dpad_down": "DPadDown",
    "dpad_left": "DPadLeft",
    "dpad_right": "DPadRight",
}

PLAYSTATION_MARKS = {
    "A": ("×", (0.29, 0.61, 0.91)),
    "B": ("○", (0.91, 0.24, 0.33)),
    "X": ("□", (0.8, 0.43, 0.72)),
    "Y": ("△", (0.27, 0.81, 0.69)),
}
NINTENDO_MARKS = {key: (letter, (0.65, 0.67, 0.72)) for key, letter in zip("ABXY", "BAYX")}


def world_axes(value):
    # Source parts lie face-up along +Y with their shoulders toward -Z.
    return Vector((value[0], -value[1], -value[2]))


def read_obj(path):
    """Read the pinned artist-authored positions and corner normals."""
    positions, normals, faces, corner_normals = [], [], [], []
    for line in path.read_text(encoding="utf-8").splitlines():
        key, _, values = line.partition(" ")
        if key == "v":
            positions.append(world_axes(tuple(map(float, values.split()))))
        elif key == "vn":
            normals.append(world_axes(tuple(map(float, values.split()))))
        elif key == "f":
            corners = [token.split("/") for token in values.split()]
            faces.append([int(corner[0]) - 1 for corner in corners])
            corner_normals.extend(normals[int(corner[-1]) - 1] for corner in corners)
    return positions, faces, corner_normals


def surface(name, rgb, roughness=0.48):
    material = bpy.data.materials.new(name)
    material.diffuse_color = (*rgb, 1.0)
    material.use_nodes = True
    shader = material.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = material.diffuse_color
    shader.inputs["Roughness"].default_value = roughness
    return material


def obj_mesh(name, positions, faces, material, normals=None):
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(positions, [], faces)
    mesh.update()
    if normals:
        for polygon in mesh.polygons:
            polygon.use_smooth = True
        mesh.normals_split_custom_set(normals)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    mesh.materials.append(material)
    return obj


def mark_segments(mark):
    if mark == "×":
        return [((-1, -1), (1, 1)), ((-1, 1), (1, -1))]
    if mark == "○":
        vertices = [(math.cos(i * math.tau / 24), math.sin(i * math.tau / 24)) for i in range(24)]
    elif mark == "△":
        vertices = [(0, 1.17), (-1, -0.75), (1, -0.75)]
    else:
        vertices = [(-0.85, -0.85), (0.85, -0.85), (0.85, 0.85), (-0.85, 0.85)]
    return list(zip(vertices, vertices[1:] + vertices[:1]))


def add_mark(parent, positions, mark, rgb):
    center = Vector((0, 0, 0))
    for position in positions:
        center += position
    center /= len(positions)
    center.y = min(position.y for position in positions) - 0.001
    material = surface(parent.name + " legend", rgb)
    if mark.isascii():
        font = bpy.data.curves.new(parent.name + " legend", "FONT")
        font.body = mark
        font.align_x = "CENTER"
        font.align_y = "CENTER"
        font.size = 0.061
        font.materials.append(material)
        label = bpy.data.objects.new(font.name, font)
        bpy.context.collection.objects.link(label)
        label.location = center
        label.rotation_euler.x = math.pi / 2
    else:
        points, faces = [], []
        for start, end in mark_segments(mark):
            a, b = Vector(start) * 0.02, Vector(end) * 0.02
            direction = (b - a).normalized()
            stroke = Vector((-direction.y, direction.x)) * 0.0025
            faces.append(tuple(range(len(points), len(points) + 4)))
            points.extend(Vector((p.x, 0, p.y)) for p in (a - stroke, b - stroke, b + stroke, a + stroke))
        label = obj_mesh(material.name, points, faces, material)
        label.location = center
    label.parent = parent


def part_offset(records, record):
    offset = world_axes(record["position"])
    if record["parent"] >= 0:
        offset += part_offset(records, records[record["parent"]])
    return offset


def convert(source, output, marks):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    records = json.loads((source / "info.json").read_text(encoding="utf-8"))["parts"]
    loaded = []
    bounds = []
    for record in records:
        stem = Path(record["filename"]).stem
        # The source's two white touch overlays are runtime markers.
        if not record["visible"] or stem.startswith("touch_point"):
            continue
        offset = part_offset(records, record)
        local, faces, normals = read_obj(source / record["filename"])
        world = [position + offset for position in local]
        bounds.extend(world)
        loaded.append((stem, record, world, faces, normals, offset))

    low = Vector(tuple(min(point[axis] for point in bounds) for axis in range(3)))
    high = Vector(tuple(max(point[axis] for point in bounds) for axis in range(3)))
    origin = (low + high) / 2
    scale = 1.968 / (high.x - low.x)
    controls = {}
    for stem, record, world, faces, normals, offset in loaded:
        control = CONTROLS.get(stem)
        material = surface(stem.replace("_", " "), record["color"])
        positions = [(position - origin) * scale for position in world]
        if stem == "guide_button":
            low = Vector(tuple(min(point[axis] for point in positions) for axis in range(3)))
            high = Vector(tuple(max(point[axis] for point in positions) for axis in range(3)))
            size = high - low
            bpy.ops.mesh.primitive_cylinder_add(vertices=40, radius=min(size.x, size.z) / 2,
                depth=size.y, rotation=(math.pi / 2, 0, 0), location=(low + high) / 2)
            button = bpy.context.object
            button.name = "Unmarked center button"
            button.data.materials.append(material)
            continue
        if control is None:
            obj_mesh(stem, positions, faces, material, normals)
            continue
        if control not in controls:
            pivot = offset + world_axes(record["pivot_offset"])
            if not any(pivot):
                pivot = sum(world, Vector((0, 0, 0))) / len(world)
            anchor = bpy.data.objects.new(control, None)
            bpy.context.collection.objects.link(anchor)
            anchor.location = (pivot - origin) * scale
            controls[control] = anchor
        anchor = controls[control]
        local = [point - anchor.location for point in positions]
        mesh = obj_mesh(stem, local, faces, material, normals)
        mesh.parent = anchor
        if control in marks:
            add_mark(mesh, local, *marks[control])

    assert set(controls) == set(CONTROLS.values())
    bpy.ops.export_scene.gltf(filepath=str(output), export_format="GLB", export_cameras=False,
                              export_lights=False, export_animations=False)


if __name__ == "__main__":
    source_root, output_root = map(Path, sys.argv[sys.argv.index("--") + 1:])
    convert(source_root / "DualShock 4", output_root / "DualShock4.glb", PLAYSTATION_MARKS)
    convert(source_root / "Switch Pro", output_root / "SwitchPro.glb", NINTENDO_MARKS)
