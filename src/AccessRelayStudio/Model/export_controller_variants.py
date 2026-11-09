"""Adapt the CC0 Xbox One housing to the Studio Elite Series 2 layout."""

import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree


DPAD = ("DPadUp", "DPadDown", "DPadLeft", "DPadRight")
PADDLES = ("SmallLeft", "SmallRight", "LargeLeft", "LargeRight")


def import_glb(path):
    current = set(bpy.context.scene.objects)
    bpy.ops.import_scene.gltf(filepath=str(path))
    return set(bpy.context.scene.objects) - current


def bounds(objects):
    corners = [item.matrix_world @ Vector(corner) for item in objects for corner in item.bound_box]
    return (Vector(min(corner[axis] for corner in corners) for axis in range(3)),
            Vector(max(corner[axis] for corner in corners) for axis in range(3)))


def group(name, location):
    item = bpy.data.objects.new(name, None)
    item.location = location
    item["controlId"] = name
    bpy.context.scene.collection.objects.link(item)
    return item


def place(item, transform, parent=None):
    """Bake a world-space transform and keep the pivot axes in Studio's frame."""
    origin = parent.location if parent else Vector()
    item.data.transform(Matrix.Translation(-origin) @ transform @ item.matrix_world)
    item.parent = parent
    item.matrix_parent_inverse = Matrix.Identity(4)
    item.matrix_basis = Matrix.Identity(4)


def remove(item):
    bpy.data.objects.remove(item, do_unlink=True)


def recolor(material, color, roughness=None, metallic=None):
    surface = next(node for node in material.node_tree.nodes if node.type == "BSDF_PRINCIPLED")
    surface.inputs["Base Color"].default_value = (*color, 1)
    if roughness is not None:
        surface.inputs["Roughness"].default_value = roughness
    if metallic is not None:
        surface.inputs["Metallic"].default_value = metallic
    material.diffuse_color = (*color, 1)


def shell_surface(item):
    world = item.matrix_world
    vertices = [world @ vertex.co for vertex in item.data.vertices]
    faces = [tuple(face.vertices) for face in item.data.polygons]
    return BVHTree.FromPolygons(vertices, faces)


def rear_plate(depth, material):
    # A continuous molded back covers the standard controller's battery door.
    # Edge widths follow the deck rather than stretching across the grips.
    edge = [(-.27, .36), (-.19, .39), (-.02, .38), (.15, .51), (.31, .57),
            (.44, .54), (.51, .48), (.55, .38), (.575, .34)]
    columns = 49
    rows = 81
    vertices = []
    for row in range(rows):
        up = edge[0][0] + (edge[-1][0] - edge[0][0]) * row / (rows - 1)
        (low, low_width), (high, high_width) = next(
            (a, b) for a, b in zip(edge, edge[1:]) if up <= b[0] + 1e-9)
        mix = (up - low) / (high - low)
        width = low_width + (high_width - low_width) * mix
        for column in range(columns):
            across = 2 * column / (columns - 1) - 1
            x = across * width
            y = depth(x, up) + .013 - .004 * across ** 8
            vertices.append((x, y, up))
    faces = []
    for row in range(rows - 1):
        for column in range(columns - 1):
            a = row * columns + column
            faces.append((a, a + columns, a + columns + 1, a + 1))
    mesh = bpy.data.meshes.new("Molded rear deck")
    mesh.from_pydata(vertices, [], faces)
    mesh.materials.append(material)
    for face in mesh.polygons:
        face.use_smooth = True
    item = bpy.data.objects.new("Molded rear deck", mesh)
    bpy.context.scene.collection.objects.link(item)


def elite_series_2(source, details):
    xbox = {item.name: item for item in import_glb(source)}
    donor = {item.name.removesuffix(".001"): item for item in import_glb(details)}
    scene = bpy.context.scene
    bpy.context.view_layer.update()

    for name in ("A green", "B red", "X blue", "Y yellow"):
        recolor(bpy.data.materials[name], (.45, .49, .53), .41, .15)
    recolor(bpy.data.materials["Satin black top shell"], (.019, .021, .024), .50)
    recolor(bpy.data.materials["Black rear shell"], (.018, .02, .023), .59)
    recolor(bpy.data.materials["Rubber grip trim"], (.009, .011, .013), .83)
    for name in ("Left", "Right"):
        ring = xbox[f"Mesh Joystick-{name}-Ring"]
        material = ring.active_material.copy()
        material.name = "Machined stick ring"
        recolor(material, (.075, .082, .089), .35, .78)
        ring.active_material = material

    front = shell_surface(xbox["Mesh MainBody"])
    rear_surfaces = tuple(shell_surface(xbox[name]) for name in
                          ("Mesh MainBodyBack", "Mesh MainBodySide", "Mesh BatteryDoor", "Mesh BatteryDoorInner"))

    def depth(surface, x, up, facing):
        origin = Vector((x, facing * -2, up))
        cast = Vector((0, facing, 0))
        hit, _, _, _ = surface.ray_cast(origin, cast, 4)
        return hit.y if hit else None

    def front_depth(x, up):
        hit = depth(front, x, up, 1)
        if hit is None:
            raise ValueError("Missing front surface under an Elite detail")
        return hit

    def rear_depth(x, up):
        hits = [hit for surface in rear_surfaces if (hit := depth(surface, x, up, -1)) is not None]
        return max(hits) if hits else -.065

    rear_plate(rear_depth, bpy.data.materials["Black rear shell"])

    center = sum((xbox[name].location for name in DPAD), Vector()) / len(DPAD)
    for name in DPAD:
        item = xbox[name]
        for mesh in list(item.children):
            remove(mesh)
        remove(item)
        sector = donor[name]
        shift = Vector((center.x - sector.location.x, -.258 - sector.location.y, center.z - sector.location.z))
        sector.name = f"{name} facet"
        control = group(name, Vector((center.x, -.258, center.z)))
        place(sector, Matrix.Translation(shift), control)

    original_share, _ = bounds([xbox["Mesh ShareButton"]])
    for name in ("Mesh ShareButton", "Mesh ShareButtonSymbol"):
        remove(xbox[name])
    kept = {*DPAD, *PADDLES, "Profile switch", "Profile indicator 1", "Profile indicator 2", "Profile indicator 3"}
    for name in ("Profile switch", "Profile indicator 1", "Profile indicator 2", "Profile indicator 3"):
        item = donor[name]
        is_switch = name == "Profile switch"
        up = .182 if is_switch else item.location.z + .08
        front_face = original_share.y if is_switch else front_depth(item.location.x, up) - .006
        low, _ = bounds([item])
        shift = Vector((0, front_face - low.y, up - item.location.z))
        stretch = Matrix.Diagonal((1.14, 1, 1.24, 1)) if is_switch else Matrix.Identity(4)
        place(item, Matrix.Translation(shift) @ Matrix.Translation(item.location)
              @ stretch @ Matrix.Translation(-item.location))

    for name in PADDLES:
        item = donor[name]
        rest = item.location.copy()
        rest.x *= 1.1
        rest.z += .015 if name.startswith("Small") else .055
        sign = 1 if rest.x > 0 else -1
        pad_center = Vector((rest.x, rear_depth(rest.x, rest.z) + .025, rest.z))
        item.name = f"{name} paddle"
        control = group(name, pad_center)
        for vertex in item.data.vertices:
            x, rear, up = vertex.co
            if name.startswith("Large"):
                x += sign * .85 * max(0, -.035 - up)
                up *= .91
            # Keep the paddle blade curved but preserve its original smooth bevel.
            lean = min(.055, max(-.025, sign * x * .40 - up * .13))
            vertex.co = (x, rear + lean, up)
        item.data.update()
        item.parent = control
        item.matrix_parent_inverse = Matrix.Identity(4)
        item.matrix_basis = Matrix.Identity(4)

    for name, item in donor.items():
        if name not in kept:
            remove(item)
    scene["modelFamily"] = "Xbox Elite Series 2-inspired"


def main(args):
    target, source, details = args
    bpy.ops.wm.read_factory_settings(use_empty=True)
    elite_series_2(source, details)
    bpy.ops.export_scene.gltf(filepath=str(Path(target)), export_format="GLB", export_yup=True,
                              export_extras=True, export_animations=False)


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
