"""Build an original, articulated Elite Series 2 GLB using only Python's stdlib."""
from __future__ import annotations

import argparse
import json
import math
import struct
import zlib
from pathlib import Path


TAU = math.tau
RIGHT_OUTLINE = [
    (0.00, .646), (.24, .648), (.31, .652), (.39, .684), (.46, .685),
    (.53, .666), (.59, .633), (.65, .597), (.685, .542), (.75, .462),
    (.80, .352), (.85, .215), (.90, .068), (.943, -.092), (.973, -.256),
    (.983, -.397), (.970, -.510), (.933, -.594), (.867, -.661),
    (.793, -.689), (.724, -.665), (.620, -.567), (.527, -.458),
    (.435, -.364), (.373, -.321), (.304, -.308), (.155, -.306), (0.0, -.306),
]


def cross(a, b, c):
    u = tuple(y - x for x, y in zip(a, b))
    v = tuple(y - x for x, y in zip(a, c))
    return (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])


def unit(v):
    length = math.sqrt(sum(x * x for x in v))
    return tuple(x / length for x in v) if length > 1e-10 else (0.0, 0.0, 1.0)


def order_ccw(points):
    area = sum(x * next_y - y * next_x for (x, y), (next_x, next_y) in zip(points, points[1:] + points[:1]))
    return points if area > 0 else list(reversed(points))


def spline(points, divisions=3):
    result = []
    for i, b in enumerate(points):
        a, c, d = points[i - 1], points[(i + 1) % len(points)], points[(i + 2) % len(points)]
        for j in range(divisions):
            t = j / divisions
            result.append(tuple(.5 * (2 * p1 + (p2 - p0) * t + (2*p0 - 5*p1 + 4*p2 - p3) * t*t
                                       + (3*p1 - p0 - 3*p2 + p3) * t*t*t)
                                for p0, p1, p2, p3 in zip(a, b, c, d)))
    return result


def triangulate(points):
    """Triangulate a simple counterclockwise outline, including concave grip gaps."""
    active = list(range(len(points)))
    triangles = []
    def signed(a, b, c):
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    while len(active) > 3:
        for k, center in enumerate(active):
            left, right = active[k - 1], active[(k + 1) % len(active)]
            a, b, c = points[left], points[center], points[right]
            if signed(a, b, c) <= 1e-10:
                continue
            if any(i not in (left, center, right)
                   and min(signed(a, b, points[i]), signed(b, c, points[i]), signed(c, a, points[i])) > 1e-9
                   for i in active):
                continue
            triangles.append((left, center, right))
            active.pop(k)
            break
        else:
            raise ValueError("Invalid polygon while generating the controller.")
    return triangles + [tuple(active)]


def ring_triangles(width, height, closed=True):
    result = []
    for level in range(height - 1):
        for index in range(width if closed else width - 1):
            a = level * width + index
            b = level * width + (index + 1) % width
            result.extend(((a, b, b + width), (a, b + width, a + width)))
    return result


def png(width, height, pixels):
    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))
    scanlines = b"".join(b"\x00" + pixels[y * width * 3:(y + 1) * width * 3] for y in range(height))
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">2I5B", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(scanlines, 9)) + chunk(b"IEND", b""))


class Model:
    def __init__(self):
        self.binary = bytearray()
        self.doc = {"asset": {"version": "2.0", "generator": "Elite Script Studio original parametric model"},
                    "scene": 0, "scenes": [{"name": "Elite Series 2", "nodes": []}],
                    "nodes": [], "meshes": [], "materials": [], "buffers": [],
                    "bufferViews": [], "accessors": [], "images": [], "textures": [],
                    "samplers": [{"magFilter": 9729, "minFilter": 9987, "wrapS": 10497, "wrapT": 10497}]}

    def append(self, key, data):
        result = len(self.doc[key])
        self.doc[key].append(data)
        return result

    def view(self, data, target=None):
        self.binary.extend(b"\x00" * (-len(self.binary) % 4))
        view = {"buffer": 0, "byteOffset": len(self.binary), "byteLength": len(data)}
        if target is not None:
            view["target"] = target
        self.binary.extend(data)
        return self.append("bufferViews", view)

    def material(self, name, color, roughness, metal=0, texture=None, normal=None, emission=None):
        pbr = {"baseColorFactor": [*color, 1], "roughnessFactor": roughness, "metallicFactor": metal}
        if texture is not None:
            pbr["baseColorTexture"] = {"index": texture}
        item = {"name": name, "pbrMetallicRoughness": pbr}
        if normal is not None:
            item["normalTexture"] = {"index": normal, "scale": .48}
        if emission is not None:
            item["emissiveFactor"] = list(emission)
        return self.append("materials", item)

    def texture(self, name, pixels, width=64, height=64):
        image = self.append("images", {"name": name, "mimeType": "image/png",
                                      "bufferView": self.view(png(width, height, pixels))})
        return self.append("textures", {"name": name, "sampler": 0, "source": image})

    def accessor(self, data, dimension, datatype, target):
        vectors = [tuple(row) for row in data]
        code, component_type = {"f": ("f", 5126), "H": ("H", 5123)}[datatype]
        view = self.view(b"".join(struct.pack(f"<{dimension}{code}", *row) for row in vectors), target)
        item = {"bufferView": view, "componentType": component_type, "count": len(vectors),
                "type": {1: "SCALAR", 2: "VEC2", 3: "VEC3", 4: "VEC4"}[dimension]}
        if target != 34962 or dimension == 3:
            item["min"] = [min(row[i] for row in vectors) for i in range(dimension)]
            item["max"] = [max(row[i] for row in vectors) for i in range(dimension)]
        return self.append("accessors", item)

    def primitive(self, vertices, triangles, material, textured=False):
        normals = [[0.0, 0.0, 0.0] for vertex in vertices]
        for i, j, k in triangles:
            normal = cross(vertices[i], vertices[j], vertices[k])
            for index in (i, j, k):
                for axis in range(3):
                    normals[index][axis] += normal[axis]
        normals = [unit(v) for v in normals]
        attrs = {"POSITION": self.accessor(vertices, 3, "f", 34962),
                 "NORMAL": self.accessor(normals, 3, "f", 34962)}
        if textured:
            attrs["TEXCOORD_0"] = self.accessor([shell_uv(x, y) for x, y, _ in vertices], 2, "f", 34962)
            tangents = [(*unit((1 - x*x, -x*y, -x*z)), -1 if z > 0 else 1) for x, y, z in normals]
            attrs["TANGENT"] = self.accessor(tangents, 4, "f", 34962)
        return {"attributes": attrs, "indices": self.accessor([(i,) for face in triangles for i in face], 1, "H", 34963),
                "material": material}

    def part(self, name, surfaces, pivot=(0, 0, 0), input_part=False):
        mesh = self.append("meshes", {"name": name, "primitives": surfaces})
        node = {"name": name, "mesh": mesh, "translation": pivot}
        if input_part:
            node["extras"] = {"controlId": name, "independent": True}
        index = self.append("nodes", node)
        self.doc["scenes"][0]["nodes"].append(index)

    def write(self, path):
        self.doc["buffers"] = [{"byteLength": len(self.binary)}]
        encoded = json.dumps(self.doc, ensure_ascii=True, separators=(",", ":")).encode()
        encoded += b" " * (-len(encoded) % 4)
        self.binary.extend(b"\x00" * (-len(self.binary) % 4))
        with path.open("wb") as output:
            output.write(struct.pack("<4s2I", b"glTF", 2, 28 + len(encoded) + len(self.binary)))
            output.write(struct.pack("<I4s", len(encoded), b"JSON"))
            output.write(encoded)
            output.write(struct.pack("<I4s", len(self.binary), b"BIN\x00"))
            output.write(self.binary)


def loft(model, outline, profile, material, textured=False, z_offset=None):
    outline = order_ccw(outline)
    count = len(outline)
    vertices = [(x * scale, y * scale, z + (z_offset(x * scale, y * scale, z) if z_offset else 0))
                for scale, z in profile for x, y in outline]
    caps = triangulate(outline)
    side_material, rear_material, front_material = material if isinstance(material, tuple) else (material,) * 3
    side = model.primitive(vertices, ring_triangles(count, len(profile)), side_material)
    rear = model.primitive(vertices[:count], [(c, b, a) for a, b, c in caps], rear_material, textured)
    front = model.primitive(vertices[-count:], caps, front_material, textured)
    return [side, rear, front]


def radial(model, profile, material, steps=48):
    vertices = [(r * math.cos(TAU * i / steps), r * math.sin(TAU * i / steps), z)
                for r, z in profile for i in range(steps)]
    triangles = ring_triangles(steps, len(profile))
    return model.primitive(vertices, triangles, material)


def ellipse(model, rx, ry, thickness, material, steps=36):
    points = [(rx * math.cos(TAU * i / steps), ry * math.sin(TAU * i / steps)) for i in range(steps)]
    return loft(model, points, [(.83, -thickness), (1.0, -thickness * .65), (1.0, -thickness * .30), (.84, 0.0)], material)


def polygon_surface(model, outline, z_function, material, rear=False, textured=False):
    points = order_ccw(outline)
    triangles = triangulate(points)
    if rear:
        triangles = [(c, b, a) for a, b, c in triangles]
    return model.primitive([(x, y, z_function(x, y)) for x, y in points], triangles, material, textured)


def stroke(model, points, radius, depth, material):
    primitives = []
    for a, b in zip(points, points[1:]):
        dx, dy = b[0] - a[0], b[1] - a[1]
        length = math.hypot(dx, dy)
        nx, ny = -dy * radius / length, dx * radius / length
        outline = [(a[0]+nx, a[1]+ny), (a[0]-nx, a[1]-ny),
                   (b[0]-nx, b[1]-ny), (b[0]+nx, b[1]+ny)]
        primitives.append(polygon_surface(model, outline, lambda _x, _y: depth, material))
    return primitives


def shell_uv(x, y):
    return ((x + 1.03) / 2.06, (.72 - y) / 1.44)


def shell_textures(model, name, grip, base):
    """Bake the rubber panel pattern into the shell to avoid intersecting overlay meshes."""
    width, height = 768, 544
    colors, normals = bytearray(), bytearray()
    edges = list(zip(grip, grip[1:] + grip[:1]))
    for row in range(height):
        y = .72 - (row + .5) * 1.44 / height
        hits = sorted(x1 + (y - y1) * (x2 - x1) / (y2 - y1)
                      for (x1, y1), (x2, y2) in edges if (y1 > y) != (y2 > y))
        intervals = list(zip(hits[::2], hits[1::2]))
        for column in range(width):
            x = abs((column + .5) * 2.06 / width - 1.03)
            if not any(left <= x <= right for left, right in intervals):
                colors.extend(base)
                normals.extend((128, 128, 255))
                continue
            u, v = TAU * (column + row) / 8, TAU * (column - row) / 8
            sine_u, sine_v, cos_u, cos_v = math.sin(u), math.sin(v), math.cos(u), math.cos(v)
            noise = ((column * 49 + row * 73 + column * row * 7) % 29 - 14) / 255
            luminance = round(17 + 13 * (.5 + .5 * cos_u * cos_v) + 22 * noise)
            colors.extend((luminance, luminance, luminance + 2))
            n = unit((.32 * (sine_u * cos_v + cos_u * sine_v), .32 * (sine_u * cos_v - cos_u * sine_v), 1))
            normals.extend(round((component * .5 + .5) * 255) for component in n)
    return (model.texture(f"{name} color", bytes(colors), width, height),
            model.texture(f"{name} rubber normal", bytes(normals), width, height))


def build():
    model = Model()
    graphite = model.material("Satin graphite shell", (.018, .021, .025), .53, .06)
    back = model.material("Rear satin graphite", (.016, .019, .023), .55, .05)
    seam = model.material("Dark perimeter seam", (.008, .010, .013), .58, .03)
    bumper = model.material("Graphite shoulder buttons", (.016, .019, .024), .44, .09)
    black = model.material("Button recess", (.006, .008, .010), .42)
    cap = model.material("Matte thumb rubber", (.015, .017, .020), .81)
    ring = model.material("Machined thumb ring", (.035, .041, .048), .42, .43)
    face = model.material("Gloss black action buttons", (.013, .016, .020), .29, .13)
    legend = model.material("Dark silver legends", (.14, .16, .18), .53, .05)
    chrome = model.material("Brushed paddle steel", (.135, .145, .16), .36, .62)
    dpad = [model.material(f"Faceted D-pad shade {i + 1}", (g, g + .008, g + .017), .33, .64)
            for i, g in enumerate((.24, .19, .28, .22))]
    led = model.material("Soft white indicator", (.78, .82, .88), .32, emission=(.24, .28, .32))

    full_outline = RIGHT_OUTLINE + [(-x, y) for x, y in RIGHT_OUTLINE[-2:0:-1]]
    outline = order_ccw(spline(full_outline))
    def body_depth(x, y, z):
        # Grip ends curve toward the rear to model the contoured housing.
        drop = max(0.0, -y - .27) * .13 * min(1.0, abs(x) / .64)
        return -drop + .015 * (1 - (x/.99)**2) * (1 if z > 0 else -1)
    body_profile = [(.85, -.218), (.91, -.207), (.963, -.179), (.994, -.13), (1, -.075),
                    (1, -.021), (.997, .04), (.983, .110), (.951, .170), (.888, .214)]
    # Front controls sit in the clean center deck. Rubber panels extend down the grips.
    grip_outline = spline([(.69,.38),(.77,.294),(.84,.112),(.888,-.095),(.894,-.28),
                          (.859,-.435),(.788,-.552),(.711,-.594),(.641,-.558),(.548,-.468),
                          (.458,-.391),(.388,-.303),(.437,-.202),(.56,-.068),(.654,.08),(.694,.209)])
    rear_grip_outline = spline([(.65,.38),(.72,.345),(.79,.209),(.846,.035),(.883,-.139),
                               (.890,-.316),(.846,-.463),(.773,-.564),(.701,-.575),
                               (.601,-.509),(.486,-.401),(.379,-.301),(.434,-.212),
                               (.517,-.164),(.564,-.05),(.578,.119),(.606,.278)])
    front_color, front_normal = shell_textures(model, "Front shell", grip_outline, (41, 44, 48))
    rear_color, rear_normal = shell_textures(model, "Rear shell", rear_grip_outline, (37, 40, 44))
    front_deck = model.material("Front deck and grip rubber", (1, 1, 1), .58, .06, front_color, front_normal)
    rear_deck = model.material("Rear deck and grip rubber", (1, 1, 1), .61, .06, rear_color, rear_normal)
    model.part("Housing", loft(model, outline, body_profile, (graphite, rear_deck, front_deck),
                               textured=True, z_offset=body_depth))
    seam_profile = [(1.0004, -.046), (1.001, -.038), (1.0004, -.030)]
    seam_vertices = [(x * scale, y * scale, z + body_depth(x * scale, y * scale, z))
                     for scale, z in seam_profile for x, y in outline]
    model.part("Perimeter seam", [model.primitive(seam_vertices, ring_triangles(len(outline), 3), seam)])

    # Rear center deck and the black shoulder deck frame the moving controls.
    rear_cover = [(-.49,.49),(-.58,.28),(-.54,.06),(-.42,-.16),(-.26,-.25),
                  (.26,-.25),(.42,-.16),(.54,.06),(.58,.28),(.49,.49)]
    model.part("Rear cover", [polygon_surface(model, spline(rear_cover), lambda _x, _y: -.238, back, rear=True)])
    upper = spline([(-.62,.54),(-.41,.58),(-.32,.60),(.32,.60),(.41,.58),(.62,.54),
                    (.51,.45),(.42,.40),(.29,.38),(-.29,.38),(-.42,.40),(-.51,.45)])
    model.part("Upper face trim", [polygon_surface(model, upper, lambda _x, _y: .231, back)])

    for sign, label in [(-1, "Left"), (1, "Right")]:
        bumper_outline = [(sign*x, y) for x, y in [(-.17,.02),(-.11,.065),(.08,.071),
                         (.19,.015),(.21,-.035),(.14,-.055),(-.13,-.042)]]
        model.part(label + "Bumper", loft(model, spline(bumper_outline), [(.83,-.085),(1,-.057),(1,.029),(.82,.054)], bumper),
                   (sign*.526, .581, .132), input_part=True)
        trigger_outline = [(sign*x, y) for x, y in [(-.145,-.06),(-.13,.097),(-.05,.133),(.063,.122),
                         (.139,.066),(.16,-.005),(.119,-.096),(-.07,-.089)]]
        model.part(label + "Trigger", loft(model, spline(trigger_outline),
                   [(.64,-.193),(.87,-.168),(1,-.121),(.96,-.060),(.80,.018)], bumper),
                   (sign*.57, .548, -.106), input_part=True)

    for name, x, y in [("LeftStick", -.51,.307), ("RightStick", .27,.006)]:
        model.part(f"{name} recess", [radial(model, [(.135,-.010),(.146,.006),(.139,.019),(.121,.026),(.100,.020)], black)],
                   (x, y, .219))
        surfaces = [
            radial(model, [(.0001,-.017),(.051,-.017),(.056,.019),(.049,.053),(.067,.070)], black),
            radial(model, [(.086,.069),(.104,.072),(.111,.085),(.109,.103),(.097,.114)], ring),
            radial(model, [(.101,.102),(.104,.113),(.098,.121),(.079,.125),(.063,.119),(.040,.116),(.0001,.113)], cap),
        ]
        # Shallow ridges catch light around the separate joystick cap.
        count = 96
        vertices = []
        for z in (.086, .100, .111):
            for i in range(count):
                a = i * TAU / count
                r = (.110 if i % 2 == 0 else .107) - (.003 if z == .111 else 0)
                vertices.append((r*math.cos(a), r*math.sin(a), z))
        surfaces.append(model.primitive(vertices, ring_triangles(count, 3), cap))
        model.part(name, surfaces, (x, y, .220), input_part=True)

    letters = {
        "A": [[(-.018,-.021),(0,.024),(.018,-.021)], [(-.011,-.006),(.011,-.006)]],
        "B": [[(-.016,-.023),(-.016,.023),(.006,.023),(.017,.015),(.017,.008),(.008,.001),
               (-.015,.001)], [(.008,.001),(.019,-.005),(.020,-.016),(.010,-.023),(-.016,-.023)]],
        "X": [[(-.017,-.021),(.017,.021)],[(-.017,.021),(.017,-.021)]],
        "Y": [[(-.017,.021),(0,-.001),(.017,.021)],[(0,-.001),(0,-.022)]],
    }
    for name, x, y in [("A",.518,.169),("B",.654,.301),("X",.378,.303),("Y",.516,.435)]:
        model.part(f"{name} rim", [radial(model, [(.075,-.003),(.077,.012),(.072,.025),(.063,.026),(.062,.010)], black)], (x,y,.221))
        surface = [radial(model, [(.0001,-.008),(.062,-.008),(.065,.012),(.061,.031),
                                  (.050,.043),(.029,.046),(.0001,.047)], face)]
        for path in letters[name]:
            surface.extend(stroke(model, path, .0036, .0475, legend))
        model.part(name, surface, (x,y,.229), input_part=True)

    for name, x in [("View",-.145),("Menu",.145)]:
        surface = ellipse(model, .046, .041, .027, face)
        lines = [[(-.018,y),(.018,y)] for y in (-.013,0,.013)] if name == "Menu" else [
            [(-.020,-.010),(-.020,.015),(.011,.015),(.011,.008)],
            [(-.009,-.016),(.019,-.016),(.019,.006),(-.009,.006),(-.009,-.016)],
        ]
        for line in lines:
            surface.extend(stroke(model, line, .0022, .001, legend))
        model.part(name, surface, (x,.309,.253), input_part=True)

    dpad_center = (-.268,-.019,.239)
    model.part("D-pad well", [radial(model, [(.163,-.009),(.175,.002),(.174,.012),(.162,.020),(.148,.016)], black)], dpad_center)
    for i, (name, degrees) in enumerate([("DPadRight",0),("DPadUp",90),("DPadLeft",180),("DPadDown",270)]):
        theta = math.radians(degrees)
        angles = [theta + math.radians(-44.3 + 88.6 * t / 9) for t in range(10)]
        # A flat center facet transitions into two beveled radial planes.
        verts = [(r * math.cos(angle), r * math.sin(angle), z)
                 for r,z in [(.001,.023),(.066,.026),(.115,.034),(.158,.042),(.166,.027)] for angle in angles]
        tris = [(c,b,a) for a,b,c in ring_triangles(10,5,closed=False)]
        model.part(name, [model.primitive(verts, tris, dpad[i])], dpad_center, input_part=True)

    model.part("Unmarked center button", ellipse(model, .071,.066,.032, black), (0,.502,.266))
    model.part("Profile switch", ellipse(model, .047,.025,.019, black), (0,.202,.246))
    for i, y in enumerate((.03,-.003,-.036), 1):
        model.part(f"Profile indicator {i}", ellipse(model, .019,.0042,.002, led if i == 1 else legend), (0,y,.235))
    model.part("Top USB-C inset", ellipse(model, .068,.016,.006, black), (0,.622,-.113))

    small_outline = [(-.101,.023),(-.085,.051),(-.044,.061),(.071,.058),(.103,.048),
                     (.116,.017),(.112,-.023),(.088,-.046),(.010,-.039),(-.045,-.031),(-.084,-.054),(-.102,-.037)]
    large_outline = [(-.099,.089),(-.074,.122),(.035,.131),(.094,.112),(.119,.076),
                     (.124,.037),(.108,-.020),(.094,-.087),(.053,-.166),(.026,-.183),
                     (.002,-.163),(.008,-.108),(.018,-.052),(-.004,-.005),(-.045,.018),(-.097,.032)]
    for name, sign in [("Left",1),("Right",-1)]:
        small = [(sign * -x, y) for x,y in small_outline]
        large = [(sign * x, y) for x,y in large_outline]
        model.part("Small"+name, loft(model, spline(small), [(.85,-.047),(1,-.037),(1,-.013),(.84,.004)], chrome),
                   (sign*.337,-.022,-.248), input_part=True)
        model.part("Large"+name, loft(model, spline(large), [(.87,-.065),(1,-.049),(1,-.013),(.84,.003)], chrome),
                   (sign*.356,-.227,-.248), input_part=True)

    return model


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path, nargs="?", default=Path(__file__).with_name("EliteSeries2.glb"))
    args = parser.parse_args()
    model = build()
    model.write(args.output)
    inputs = [node["name"] for node in model.doc["nodes"] if node.get("extras", {}).get("independent")]
    print(f"{args.output.name}  {args.output.stat().st_size:,} bytes  {len(inputs)} separate input nodes")
    print(", ".join(inputs))
