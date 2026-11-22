"""A stylised low-poly green sea turtle for the intro cinematic, built with plain bpy/bmesh (no mesh_kit).

The turtle is split into animatable parts so Unity can swim it procedurally (SeaTurtle.cs):
    Shell        carapace + plastron + tail (the body; never rotated on its own)
    Head         origin at the neck, so it can bob and turn
    Flipper_FL   front-left flipper, origin at the shoulder
    Flipper_FR   front-right
    Flipper_BL   back-left, origin at the hip
    Flipper_BR   back-right

Modelled in metres, front towards -Y, belly on z = 0, flat shaded (the Upcycle City house style).
Material names: shell_dark, shell_light, belly, skin, skin_spot, eye. Unity turns them into Pollution Lit
materials on import (PollutionModelPostprocessor), so the dead turtle greys and grimes in smog.

Run headless (Blender 5.x; the Flatpak build is fine):
    flatpak run org.blender.Blender -b --factory-startup --python Tools/Blender/sea_turtle.py -- \
        Assets/UpcycleCity/Art/UpcycleProps/Intro [preview.png]
"""

import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

# ---------------------------------------------------------------- palette

PALETTE = {
    "shell_dark": ("#5A4426", 0.5),
    "shell_light": ("#A0803E", 0.45),
    "belly": ("#D8C98C", 0.7),
    "skin": ("#93A86A", 0.7),
    "skin_spot": ("#5E6E3E", 0.75),
    "eye": ("#141212", 0.2),
}

LENGTH = 0.95  # nose to tail, roughly an adult green sea turtle


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def material(name):
    mat = bpy.data.materials.get(name)
    if mat is not None:
        return mat
    hex_color, roughness = PALETTE[name]
    rgb = [srgb_to_linear(int(hex_color[i:i + 2], 16) / 255.0) for i in (1, 3, 5)]
    mat = bpy.data.materials.new(name)
    mat.diffuse_color = (*rgb, 1.0)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
    return mat


# ---------------------------------------------------------------- mesh helpers


def new_object(name, origin):
    mesh = bpy.data.meshes.new(name)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.location = origin
    return obj


def ellipsoid(bm, centre, radii, subdiv=2, mat_index=0, squash_bottom=None):
    """Adds a faceted ellipsoid (an icosphere scaled per axis). Optionally flattens it below a height."""
    created = bmesh.ops.create_icosphere(bm, subdivisions=subdiv, radius=1.0)
    verts = created["verts"]
    for v in verts:
        v.co = Vector((v.co.x * radii[0], v.co.y * radii[1], v.co.z * radii[2])) + Vector(centre)
        if squash_bottom is not None and v.co.z < squash_bottom:
            v.co.z = squash_bottom + (v.co.z - squash_bottom) * 0.15
    faces = {f for v in verts for f in v.link_faces}
    for f in faces:
        f.material_index = mat_index
    return verts, faces


def paddle(bm, length, width, thickness, sweep_deg, side, mat_index):
    """A flat, tapered flipper paddle starting at the local origin and reaching out along +X (or -X)."""
    verts, faces = ellipsoid(bm, (0.0, 0.0, 0.0), (length * 0.5, width * 0.5, thickness * 0.5), 2, mat_index)
    for v in verts:
        # Taper the tip into a point and curve the trailing edge back, like a real flipper
        t = (v.co.x / (length * 0.5) + 1.0) * 0.5          # 0 at the root, 1 at the tip
        v.co.y *= 1.0 - 0.55 * t * t
        v.co.y += 0.35 * width * t * t                       # trailing tip sweeps back
        v.co.x += length * 0.5                               # root at the origin
        v.co.x *= side
    rot = Matrix.Rotation(math.radians(sweep_deg * side), 4, "Z")
    bmesh.ops.transform(bm, matrix=rot, verts=verts)
    return verts, faces


def finish(obj, bm, mats):
    bm.normal_update()
    bm.to_mesh(obj.data)
    bm.free()
    for m in mats:
        obj.data.materials.append(material(m))
    for p in obj.data.polygons:
        p.use_smooth = False
    return obj


def mottle(faces, light_index, dark_index, seed):
    """Alternates two materials on a shell so the facets read as scutes."""
    for f in faces:
        c = f.calc_center_median()
        h = math.sin(c.x * 53.1 + seed) * math.cos(c.y * 37.7 - seed) + math.sin((c.x + c.y) * 21.3)
        f.material_index = light_index if h > 0.15 else dark_index


# ---------------------------------------------------------------- parts


def build_shell():
    obj = new_object("Shell", (0.0, 0.0, 0.0))
    bm = bmesh.new()
    # Carapace: a low dome, longer than wide, slightly pointed at the back
    verts, faces = ellipsoid(bm, (0.0, 0.02, 0.1), (0.35, 0.45, 0.095), 2, 0, squash_bottom=0.07)
    for v in verts:
        if v.co.y > 0.2:
            v.co.x *= 1.0 - (v.co.y - 0.2) * 0.9  # narrow towards the tail
        v.co.z += 0.02 * math.cos(v.co.x * 6.0)   # a gentle ridge down the spine
    mottle(faces, 1, 0, 1.7)
    # Plastron (belly plate)
    ellipsoid(bm, (0.0, 0.0, 0.07), (0.29, 0.38, 0.055), 2, 2)
    # Tail: a small cone
    tail = bmesh.ops.create_cone(bm, cap_ends=True, segments=5, radius1=0.035, radius2=0.004, depth=0.12)
    m = Matrix.Translation((0.0, 0.5, 0.08)) @ Matrix.Rotation(math.radians(-95), 4, "X")
    bmesh.ops.transform(bm, matrix=m, verts=tail["verts"])
    for f in {f for v in tail["verts"] for f in v.link_faces}:
        f.material_index = 3
    return finish(obj, bm, ["shell_dark", "shell_light", "belly", "skin"])


def build_head():
    neck = Vector((0.0, -0.42, 0.11))
    obj = new_object("Head", neck)
    bm = bmesh.new()
    # Neck stub tucked under the shell, then the head itself (local coords from the neck)
    ellipsoid(bm, (0.0, -0.05, 0.0), (0.07, 0.09, 0.055), 1, 0)
    verts, faces = ellipsoid(bm, (0.0, -0.15, 0.02), (0.078, 0.1, 0.062), 2, 0)
    for v in verts:
        if v.co.y < -0.19:
            v.co.z -= (-.19 - v.co.y) * 0.5  # beak slopes down
    for f in faces:
        c = f.calc_center_median()
        if c.z > 0.05 and abs(c.x) < 0.06:
            f.material_index = 1  # darker head scales on top
    for side in (-1, 1):
        ellipsoid(bm, (side * 0.06, -0.18, 0.04), (0.016, 0.02, 0.018), 1, 2)
    return finish(obj, bm, ["skin", "skin_spot", "eye"])


def build_flipper(name, shoulder, side, length, width, sweep):
    obj = new_object(name, shoulder)
    bm = bmesh.new()
    verts, faces = paddle(bm, length, width, 0.035, sweep, side, 0)
    for f in faces:
        c = f.calc_center_median()
        if (abs(c.x) * 31.0 + c.y * 17.0) % 1.0 > 0.62:
            f.material_index = 1  # scale spots
    return finish(obj, bm, ["skin", "skin_spot"])


def build_turtle():
    parts = [build_shell(), build_head()]
    for side, tag in ((-1, "L"), (1, "R")):
        parts.append(build_flipper(f"Flipper_F{tag}", Vector((side * 0.24, -0.2, 0.1)), side, 0.5, 0.17, 22))
        parts.append(build_flipper(f"Flipper_B{tag}", Vector((side * 0.17, 0.33, 0.08)), side, 0.2, 0.12, 48))
    return parts


# ---------------------------------------------------------------- export


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def export(parts, out_dir):
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, "sea_turtle.fbx")
    bpy.ops.object.select_all(action="DESELECT")
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        object_types={"MESH"},
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        bake_space_transform=True,
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
        bake_anim=False,
    )
    print(f"[sea_turtle] exported {path}")


def render_preview(path):
    scene = bpy.context.scene
    cam_data = bpy.data.cameras.new("PreviewCam")
    cam = bpy.data.objects.new("PreviewCam", cam_data)
    scene.collection.objects.link(cam)
    cam.location = (1.1, -1.25, 1.35)
    direction = Vector((0.0, 0.0, 0.1)) - cam.location
    cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    cam_data.lens = 45
    scene.camera = cam
    sun_data = bpy.data.lights.new("Sun", "SUN")
    sun_data.energy = 3.0
    sun = bpy.data.objects.new("Sun", sun_data)
    sun.rotation_euler = (math.radians(50), 0, math.radians(30))
    scene.collection.objects.link(sun)
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.render.resolution_x = 900
    scene.render.resolution_y = 700
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print(f"[sea_turtle] preview {path}")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out_dir = argv[0] if argv else "Assets/UpcycleCity/Art/UpcycleProps/Intro"
    clear_scene()
    parts = build_turtle()
    export(parts, out_dir)
    if len(argv) > 1:
        render_preview(argv[1])


if __name__ == "__main__":
    main()
