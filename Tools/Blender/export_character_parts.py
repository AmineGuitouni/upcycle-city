"""Export the modular player character for the Character Creator.

Builds the parts in character_model.py (this folder) with the Upcycle City mesh toolkit (read-only archive)
and writes one FBX per part. All parts share one origin: feet on the floor, facing -Y in Blender (+Z in Unity).

  body_male.fbx, body_female.fbx   body and clothes, no hair, no face
  face_<style>.fbx                 eyes, brows and mouth
  beard_<style>.fbx                facial hair
  hair_<style>.fbx                 hair styles (and the beanie)

Run from the Unity project root:

  /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup \
      --python Tools/Blender/export_character_parts.py -- \
      <path to upcycle_city/blender> Assets/UpcycleCity/Art/UpcycleProps/Character [preview_dir]

With a preview_dir it also renders hero.png (reference-style front views) and sheets of every style.
"""

import math
import os
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
LIBRARY_DIR = os.path.abspath(argv[0])
OUTPUT_DIR = os.path.abspath(argv[1])
PREVIEW_DIR = os.path.abspath(argv[2]) if len(argv) > 2 else None
sys.path.insert(0, LIBRARY_DIR)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import mesh_kit as mk  # noqa: E402
import character_model as cm  # noqa: E402


# (voxel size in metres, target face count, solidify thickness for open shells)
LOW_POLY = {
    "body": (0.005, 2200, 0.0),
    "hair": (0.005, 380, 0.008),
    "beard": (0.004, 260, 0.005),
}
# Simple shells already sit on the head's facet grid; fusing them would only make the facets irregular
HAIR_KEEP_GRID = {"buzz", "beanie"}


def _apply(obj, kind, **props):
    mod = obj.modifiers.new(kind.lower(), kind)
    for key, value in props.items():
        setattr(mod, key, value)
    with bpy.context.temp_override(object=obj, active_object=obj, selected_objects=[obj]):
        bpy.ops.object.modifier_apply(modifier=mod.name)


def low_poly(obj, voxel, target_faces, solidify=0.0):
    """Fuse every primitive into one surface, then make it a faceted low-poly mesh.

    The primitives (capsules, spheres, boxes) are melted together with a voxel remesh, so joints, the
    neck and the hands flow into one form instead of reading as separate parts. A collapse decimation
    brings it down to a low-poly budget, every new face takes the material of the nearest original
    face, and everything is flat shaded.
    """
    import bmesh
    from mathutils.bvhtree import BVHTree

    src = bmesh.new()
    src.from_mesh(obj.data)
    src.faces.ensure_lookup_table()
    tree = BVHTree.FromBMesh(src)
    source_mats = [f.material_index for f in src.faces]

    if solidify > 0:
        _apply(obj, "SOLIDIFY", thickness=solidify, offset=-1.0)
    _apply(obj, "REMESH", mode="VOXEL", voxel_size=voxel, adaptivity=0.0)
    faces = len(obj.data.polygons)
    if faces > target_faces:
        _apply(obj, "DECIMATE", decimate_type="COLLAPSE", ratio=target_faces / faces)

    for poly in obj.data.polygons:
        _, _, index, _ = tree.find_nearest(poly.center)
        poly.material_index = source_mats[index] if index is not None else 0
        poly.use_smooth = False
    src.free()


def build_all(coll):
    objects = {}
    for female in (False, True):
        b = cm.build_body(female)
        body = b.finish(coll)
        if cm.LOW_POLY:
            low_poly(body, *LOW_POLY["body"])
        head = cm.build_head(b.name + "_head").finish(coll)
        with bpy.context.temp_override(active_object=body, selected_editable_objects=[body, head]):
            bpy.ops.object.join()
        objects[b.name] = body
    for style in cm.FACE_STYLES:
        b = cm.build_face(style)
        objects[b.name] = b.finish(coll)
    for style in cm.BEARD_STYLES[1:]:
        b = cm.build_beard(style)
        objects[b.name] = b.finish(coll)
        if cm.LOW_POLY:
            low_poly(objects[b.name], *LOW_POLY["beard"])
    for style in cm.HAIR_STYLES[1:]:
        b = cm.build_hair(style)
        objects[b.name] = b.finish(coll)
        if cm.LOW_POLY and style not in HAIR_KEEP_GRID:
            low_poly(objects[b.name], *LOW_POLY["hair"])
    for side in ("right", "left"):
        for pose in cm.FP_POSES:
            b, socket, forward, up = cm.build_fp_arm(side, pose)
            arm = b.finish(coll)
            for name, location in (("ToolSocket", socket), ("ToolSocketForward", forward), ("ToolSocketUp", up)):
                empty = bpy.data.objects.new(name, None)
                empty.empty_display_size = 0.02
                empty.location = location
                coll.objects.link(empty)
                empty.parent = arm
            objects[b.name] = arm
    return objects


def export(obj, path):
    for other in bpy.context.view_layer.objects:
        other.select_set(False)
    obj.select_set(True)
    for child in obj.children:   # e.g. the arms' ToolSocket
        child.select_set(True)
    bpy.context.view_layer.objects.active = obj
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"MESH", "EMPTY"}, global_scale=1.0, apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y", bake_space_transform=True,
        use_mesh_modifiers=True, mesh_smooth_type="FACE", add_leaf_bones=False, bake_anim=False)


# ---------------------------------------------------------------- previews

def _setup_render(width, height, dark=True):
    scene = bpy.context.scene
    engines = bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items.keys()
    scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = width, height
    if scene.world is None:
        scene.world = bpy.data.worlds.new("w")
    scene.world.use_nodes = True
    bg = scene.world.node_tree.nodes.get("Background")
    bg.inputs["Color"].default_value = (0.006, 0.006, 0.007, 1) if dark else (0.2, 0.21, 0.23, 1)
    bg.inputs["Strength"].default_value = 1.0
    scene.view_settings.view_transform = "Standard"
    return scene


def _light(name, kind, energy, loc=None, rot=None, size=None):
    data = bpy.data.lights.new(name, kind)
    data.energy = energy
    if size is not None and kind == "SPOT":
        data.spot_size = math.radians(size)
        data.spot_blend = 0.6
    obj = bpy.data.objects.new(name, data)
    if loc:
        obj.location = loc
    if rot:
        obj.rotation_euler = tuple(math.radians(a) for a in rot)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _instance(coll, name, src, x, yaw):
    inst = bpy.data.objects.new(name, src.data)
    inst.location = (x, 0, 0)
    inst.rotation_euler = (0, 0, math.radians(yaw))
    coll.objects.link(inst)


def _camera(loc, target, lens=None, ortho=None):
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    if ortho:
        cam.data.type = "ORTHO"
        cam.data.ortho_scale = ortho
    else:
        cam.data.lens = lens or 50
    cam.location = loc
    cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.collection.objects.link(cam)
    bpy.context.scene.camera = cam
    return cam


def _clear_previews():
    for o in list(bpy.data.objects):
        if o.type in {"CAMERA", "LIGHT"} or o.name.startswith("pv_"):
            bpy.data.objects.remove(o)
    for c in list(bpy.data.collections):
        if c.name.startswith("preview"):
            bpy.data.collections.remove(c)


def render_hero(objects, path):
    """Front views lit like the reference: a dark room, a soft key from above, a floor pool."""
    scene = _setup_render(2000, 1150, dark=True)
    coll = bpy.data.collections.new("preview_hero")
    scene.collection.children.link(coll)
    looks = [("male", "tired", "messy", "beard", -12), ("female", "tired", "long", None, 12),
             ("male", "wide", "beanie", "stubble", -30), ("female", "squint", "bun", None, 30)]
    for i, (sex, face, hair, beard, yaw) in enumerate(looks):
        x = (i - 1.5) * 0.95
        for part in (f"body_{sex}", f"face_{face}", f"hair_{hair}", f"beard_{beard}" if beard else None):
            if part:
                _instance(coll, f"pv_{part}_{i}", objects[part], x, yaw)
    floor = bpy.data.meshes.new("pv_floor")
    floor.from_pydata([(-6, -6, 0), (6, -6, 0), (6, 6, 0), (-6, 6, 0)], [], [(0, 1, 2, 3)])
    fo = bpy.data.objects.new("pv_floor", floor)
    fm = bpy.data.materials.new("pv_floor_mat")
    fm.use_nodes = True
    fm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.03, 0.028, 0.027, 1)
    fm.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.8
    fo.data.materials.append(fm)
    coll.objects.link(fo)
    _light("key", "SPOT", 650, loc=(0, -3.2, 4.4), rot=(36, 0, 0), size=75)
    _light("fill", "SUN", 0.5, rot=(60, 0, -20))
    _light("rim", "SUN", 1.2, rot=(60, 0, 170))
    _camera((0, -7.2, 1.15), (0, 0, 0.95), lens=42)
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def render_sheet(objects, path, combos, head_only=True):
    scene = _setup_render(2400, 560 if head_only else 1100, dark=False)
    coll = bpy.data.collections.new("preview_sheet")
    scene.collection.children.link(coll)
    spacing = 0.42 if head_only else 0.85
    for c, parts in enumerate(combos):
        for part in parts:
            if part:
                _instance(coll, f"pv_{part}_{c}", objects[part], c * spacing, -25)
    _light("key", "SUN", 3.5, rot=(50, 0, -30))
    _light("fill", "SUN", 1.2, rot=(70, 0, 150))
    width = len(combos) * spacing
    if head_only:
        _camera(((len(combos) - 1) * spacing / 2, -6, 1.55), ((len(combos) - 1) * spacing / 2, 0, 1.55),
                ortho=width)
    else:
        _camera(((len(combos) - 1) * spacing / 2, -8, 0.95), ((len(combos) - 1) * spacing / 2, 0, 0.95),
                ortho=width)
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def render_first_person(objects, path):
    """What the player sees: both arms from the eye, 60 degree vertical field of view."""
    scene = _setup_render(1600, 900, dark=False)
    scene.world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.45, 0.55, 0.65, 1)
    coll = bpy.data.collections.new("preview_fp")
    scene.collection.children.link(coll)
    for i, (right, left) in enumerate((("grip", "open"), ("open", "open"))):
        for name in (f"fp_arm_right_{right}", f"fp_arm_left_{left}"):
            _instance(coll, f"pv_{name}_{i}", objects[name], i * 10.0, 0)
    _light("key", "SUN", 3.0, rot=(40, 0, -30))
    _light("fill", "SUN", 1.0, rot=(70, 0, 150))
    for i in range(2):
        cam = _camera((i * 10.0, 0, 0), (i * 10.0, -1, 0))
        cam.data.sensor_fit = "VERTICAL"
        cam.data.angle = math.radians(60)
        scene.render.filepath = f"{path[:-4]}_{i}.png"
        bpy.ops.render.render(write_still=True)
        bpy.data.objects.remove(cam)


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mk.reset()
    mk.SCENE_UNITS_PER_METRE = 1.0
    coll = bpy.data.collections.new("character")
    bpy.context.scene.collection.children.link(coll)
    objects = build_all(coll)

    for name, obj in objects.items():
        export(obj, os.path.join(OUTPUT_DIR, name + ".fbx"))
    print("EXPORTED", len(objects), "character parts")

    if not PREVIEW_DIR:
        return
    os.makedirs(PREVIEW_DIR, exist_ok=True)
    for obj in objects.values():
        obj.hide_render = True
    _clear_previews()
    render_first_person(objects, os.path.join(PREVIEW_DIR, "first_person.png"))
    _clear_previews()
    render_hero(objects, os.path.join(PREVIEW_DIR, "hero.png"))
    for sex in ("male", "female"):
        _clear_previews()
        combos = [(f"body_{sex}", f"face_{f}") for f in cm.FACE_STYLES]
        combos += [(f"body_{sex}", "face_tired", f"beard_{s}") for s in cm.BEARD_STYLES[1:]]
        combos += [(f"body_{sex}", "face_tired", f"hair_{s}") for s in cm.HAIR_STYLES[1:]]
        render_sheet(objects, os.path.join(PREVIEW_DIR, f"heads_{sex}.png"), combos)
    print("PREVIEWS", PREVIEW_DIR)


main()
