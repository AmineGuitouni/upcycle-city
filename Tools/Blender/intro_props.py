"""Props for the intro cinematic, built with plain bpy/bmesh (no mesh_kit):

    bag_held.fbx     a thin white carrier bag hanging from its two handles (pivot between the handles,
                     where the hand grips), crumpled sides, open mouth
    bag_tangled.fbx  the same plastic twisted into a band that wraps a turtle's neck and flipper, with a
                     torn tail (pivot at the centre of the loop)
    gull.fbx         a low-poly gull: Body, Wing_L, Wing_R (wing origins at the shoulders, for flapping)

Metres, front towards -Y, Z up, exported like the other Upcycle City props. The bags are open sheets (one
surface): the intro gives them the two-sided "UpcycleCity/Plastic Bag" shader.

    flatpak run org.blender.Blender -b --factory-startup --python Tools/Blender/intro_props.py -- \
        Assets/UpcycleCity/Art/UpcycleProps/Intro [preview.png]
"""

import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector, noise

PALETTE = {
    "bag_plastic": ("#F1F0EA", 0.35),
    "gull_white": ("#F2F2EE", 0.6),
    "gull_grey": ("#9AA3AA", 0.6),
    "gull_black": ("#202124", 0.6),
    "gull_beak": ("#E9B52E", 0.4),
}


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def material(name):
    mat = bpy.data.materials.get(name)
    if mat:
        return mat
    hex_color, rough = PALETTE[name]
    rgb = [srgb_to_linear(int(hex_color[i:i + 2], 16) / 255.0) for i in (1, 3, 5)]
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = (*rgb, 1.0)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
        bsdf.inputs["Roughness"].default_value = rough
    return mat


def new_object(name, origin=(0, 0, 0)):
    obj = bpy.data.objects.new(name, bpy.data.meshes.new(name))
    bpy.context.collection.objects.link(obj)
    obj.location = origin
    return obj


def finish(obj, bm, mats, smooth=False):
    bm.normal_update()
    bm.to_mesh(obj.data)
    bm.free()
    for m in mats:
        obj.data.materials.append(material(m))
    for p in obj.data.polygons:
        p.use_smooth = smooth
    return obj


def crumple(verts, amount, scale, seed):
    """Pushes vertices along a noise field: creases and dents like thin plastic."""
    off = Vector((seed * 3.1, seed * 1.7, seed * 2.3))
    for v in verts:
        n = noise.noise_vector(v.co * scale + off)
        v.co += n * amount


# ---------------------------------------------------------------- the bag

def build_bag_held():
    """A thin carrier ("T-shirt") bag hanging from its two handles. Pivot (0,0,0) is the grip point."""
    obj = new_object("bag_held")
    bm = bmesh.new()
    rings, seg = 12, 20
    height, width, depth = 0.3, 0.17, 0.045
    top = -0.13  # the mouth hangs below the grip; the handles span the gap
    grid = []
    for r in range(rings + 1):
        t = r / rings
        z = top - t * height
        # Flat sheet that bellies out a little where the weight sits, gathered at the bottom fold
        belly = 0.8 + 0.45 * math.sin(min(1.0, t * 1.1) * math.pi * 0.9)
        narrow = 1.0 - 0.18 * smooth(0.75, 1.0, t)
        row = []
        for s in range(seg):
            a = s / seg * math.pi * 2
            x = math.cos(a) * width * narrow
            y = math.sin(a) * depth * belly
            # A ragged, uneven mouth
            zz = z + (0.012 * math.sin(a * 5 + 1.3) if r == 0 else 0.0)
            row.append(bm.verts.new((x, y, zz)))
        grid.append(row)
    for r in range(rings):
        for s in range(seg):
            bm.faces.new((grid[r][s], grid[r][(s + 1) % seg], grid[r + 1][(s + 1) % seg], grid[r + 1][s]))
    # Bottom seam: the two faces pinched together
    for s in range(seg // 2):
        a, b = grid[-1][s], grid[-1][seg - 1 - s]
        mid = (a.co + b.co) * 0.5
        a.co = mid + (a.co - mid) * 0.15
        b.co = mid + (b.co - mid) * 0.15
    crumple(bm.verts, 0.016, 8.0, 1)
    crumple(bm.verts, 0.008, 21.0, 2)
    crumple(bm.verts, 0.003, 45.0, 3)

    # Two strap handles from the mouth's side corners up to the hand, flat bands seen face-on
    for side in (-1, 1):
        n = 10
        path = []
        for i in range(n + 1):
            t = i / n
            x = side * (width * (1 - t) * 0.95 + 0.015)
            z = top + (0.0 - top) * math.sin(t * math.pi * 0.5) + 0.02 * math.sin(t * math.pi)
            path.append(Vector((x, 0.0, z)))
        prev = None
        for i, p in enumerate(path):
            d = (path[min(i + 1, n)] - path[max(i - 1, 0)]).normalized()
            perp = Vector((-d.z, 0.0, d.x))                 # across the strap, in the bag's plane
            w = 0.022 * (1.0 - 0.5 * (i / n))
            a = bm.verts.new(p - perp * w + Vector((0, 0.004 * math.sin(i), 0)))
            b = bm.verts.new(p + perp * w)
            if prev:
                bm.faces.new((prev[0], prev[1], b, a))
            prev = (a, b)
    return finish(obj, bm, ["bag_plastic"])


def smooth(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


def build_bag_tangled():
    """Plastic twisted into a band around a neck (radius ~0.075) with a torn tail. Pivot at the loop centre."""
    obj = new_object("bag_tangled")
    bm = bmesh.new()
    n, half_w = 40, 0.035
    rows = []
    for i in range(n + 1):
        t = i / n
        a = t * math.pi * 2.25                       # a bit more than one wrap
        radius = 0.078 + 0.01 * math.sin(a * 3)
        centre = Vector((math.cos(a) * radius, t * 0.05, math.sin(a) * radius))
        radial = Vector((math.cos(a), 0, math.sin(a)))
        axis = Vector((0, 1, 0))
        twist = a * 1.5                                # the band twists as it wraps
        across = (axis * math.cos(twist) + radial * math.sin(twist) * 0.6).normalized()
        w = half_w * (0.7 + 0.3 * math.sin(a * 2 + 1))
        rows.append((bm.verts.new(centre - across * w), bm.verts.new(centre + across * w)))
    for i in range(n):
        a0, b0 = rows[i]
        a1, b1 = rows[i + 1]
        bm.faces.new((a0, b0, b1, a1))
    # The torn tail: a ragged sheet trailing off the loop
    last = rows[-1]
    prev = last
    for k in range(1, 7):
        t = k / 6
        drift = Vector((0.03 * k, 0.06 * k, -0.02 * k + 0.01 * math.sin(k * 2)))
        w = half_w * (1.4 - t * 0.9)
        mid = (prev[0].co + prev[1].co) * 0.5 + Vector((0.02, 0.05, -0.012))
        a = bm.verts.new(mid + Vector((-w, 0, 0.01 * math.sin(k * 3))))
        b = bm.verts.new(mid + Vector((w, 0.01, -0.01 * math.cos(k * 2))))
        bm.faces.new((prev[0], prev[1], b, a))
        prev = (a, b)
    crumple(bm.verts, 0.01, 18.0, 4)
    return finish(obj, bm, ["bag_plastic"])


# ---------------------------------------------------------------- the gull

def ellipsoid(bm, centre, radii, subdiv, mat_index):
    created = bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=1.0)
    for v in created["verts"]:
        v.co = Vector((v.co.x * radii[0], v.co.y * radii[1], v.co.z * radii[2])) + Vector(centre)
    for f in {f for v in created["verts"] for f in v.link_faces}:
        f.material_index = mat_index
    return created["verts"]


def build_gull():
    parts = []
    body = new_object("Body")
    bm = bmesh.new()
    verts = ellipsoid(bm, (0, 0, 0), (0.07, 0.2, 0.07), 2, 0)
    for v in verts:
        if v.co.y > 0.1:   # tapered tail
            v.co.x *= 0.6
            v.co.z = v.co.z * 0.5 + 0.01
    ellipsoid(bm, (0, -0.19, 0.04), (0.05, 0.065, 0.05), 1, 0)            # head
    beak = bmesh.ops.create_cone(bm, cap_ends=True, segments=5, radius1=0.014, radius2=0.002, depth=0.06)
    bmesh.ops.transform(bm, matrix=Matrix.Translation((0, -0.27, 0.035)) @ Matrix.Rotation(math.radians(90), 4, "X"),
                        verts=beak["verts"])
    for f in {f for v in beak["verts"] for f in v.link_faces}:
        f.material_index = 2
    for side in (-1, 1):
        ellipsoid(bm, (side * 0.03, -0.225, 0.055), (0.008, 0.008, 0.008), 1, 3)   # eyes
    parts.append(finish(body, bm, ["gull_white", "gull_grey", "gull_beak", "gull_black"]))

    for side, tag in ((-1, "R"), (1, "L")):   # Unity view: Blender -X is the bird's right
        wing = new_object(f"Wing_{tag}", (side * 0.05, -0.02, 0.02))
        bm = bmesh.new()
        # A long tapered wing, two segments (arm and hand), grey with black tips
        outline = [(0.0, -0.05), (0.18, -0.06), (0.36, -0.03), (0.46, 0.02), (0.36, 0.07), (0.18, 0.08), (0.0, 0.07)]
        top = [bm.verts.new((side * x, y, 0.008)) for x, y in outline]
        bot = [bm.verts.new((side * x, y, -0.008)) for x, y in outline]
        f1 = bm.faces.new(top if side > 0 else list(reversed(top)))
        f2 = bm.faces.new(list(reversed(bot)) if side > 0 else bot)
        for i in range(len(outline)):
            j = (i + 1) % len(outline)
            bm.faces.new((top[i], top[j], bot[j], bot[i]))
        bmesh.ops.triangulate(bm, faces=[f1, f2])
        for f in bm.faces:
            f.material_index = 1 if abs(f.calc_center_median().x) > 0.34 else 0
        parts.append(finish(wing, bm, ["gull_grey", "gull_black"]))
    return parts


# ---------------------------------------------------------------- export

def export(objs, path):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"MESH"}, global_scale=1.0, apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y", bake_space_transform=True,
        use_mesh_modifiers=True, mesh_smooth_type="FACE", add_leaf_bones=False, bake_anim=False)
    print(f"[intro_props] exported {path}")


def preview(path):
    scene = bpy.context.scene
    for o in bpy.data.objects:
        o.hide_render = False
    bpy.data.objects["bag_tangled"].location = (0.45, 0, -0.25)
    bpy.data.objects["Body"].location = (-0.5, 0, -0.2)
    bpy.data.objects["Wing_L"].location = (-0.45, -0.02, -0.18)
    bpy.data.objects["Wing_R"].location = (-0.55, -0.02, -0.18)
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    scene.collection.objects.link(cam)
    cam.location = (0.2, -1.6, 0.35)
    cam.rotation_euler = (Vector((0, 0, -0.22)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    scene.camera = cam
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.rotation_euler = (math.radians(50), 0, math.radians(30))
    scene.collection.objects.link(sun)
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.color_type = "MATERIAL"
    scene.world = bpy.data.worlds.new("W")
    scene.world.color = (0.25, 0.3, 0.35)
    scene.render.resolution_x, scene.render.resolution_y = 900, 600
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = argv[0] if argv else "Assets/UpcycleCity/Art/UpcycleProps/Intro"
    os.makedirs(out, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    held = build_bag_held()
    tangled = build_bag_tangled()
    gull = build_gull()
    export([held], os.path.join(out, "bag_held.fbx"))
    export([tangled], os.path.join(out, "bag_tangled.fbx"))
    export(gull, os.path.join(out, "gull.fbx"))
    if len(argv) > 1:
        preview(argv[1])


if __name__ == "__main__":
    main()
