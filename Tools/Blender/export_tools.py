"""Export the player's gear (upcycle_tools.py, this folder) to FBX for Unity.

Run from the Unity project root:

  /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup \
      --python Tools/Blender/export_tools.py -- \
      <path to blender-tests/upcycle_city/blender> Assets/UpcycleCity/Art/UpcycleProps/Tools [preview_dir]

Writes <output>/<model>.fbx with each model's moving parts and markers as children (see upcycle_tools.py).
With a preview_dir it also renders a close-up of each model for review.
"""

import math
import os
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
LIBRARY_DIR = os.path.abspath(argv[0])
OUTPUT_DIR = os.path.abspath(argv[1])
PREVIEW_DIR = os.path.abspath(argv[2]) if len(argv) > 2 and argv[2] else None
sys.dont_write_bytecode = True
sys.path.insert(0, LIBRARY_DIR)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import mesh_kit as mk  # noqa: E402
import upcycle_tools as tools  # noqa: E402


def export(obj, path):
    bpy.context.view_layer.update()
    for other in bpy.context.view_layer.objects:
        other.select_set(False)
    for o in [obj] + list(obj.children_recursive):
        o.select_set(True)
        if o.type == "MESH" and not any(m.type == "TRIANGULATE" for m in o.modifiers):
            tri = o.modifiers.new("triangulate", "TRIANGULATE")
            tri.quad_method = "BEAUTY"
            tri.ngon_method = "BEAUTY"
    bpy.context.view_layer.objects.active = obj
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"MESH", "EMPTY"}, global_scale=1.0, apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y", bake_space_transform=True,
        use_mesh_modifiers=True, mesh_smooth_type="FACE", add_leaf_bones=False, bake_anim=False)


def _render_setup():
    scene = bpy.context.scene
    engines = bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items.keys()
    scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = 1400, 900
    if scene.world is None:
        scene.world = bpy.data.worlds.new("w")
    scene.world.use_nodes = True
    bg = scene.world.node_tree.nodes.get("Background")
    bg.inputs["Color"].default_value = (0.55, 0.62, 0.70, 1)
    bg.inputs["Strength"].default_value = 1.0
    scene.view_settings.view_transform = "Standard"
    for name, energy, rot in (("sun", 4.0, (50, 0, -35)), ("fill", 1.2, (60, 0, 150))):
        data = bpy.data.lights.new(name, "SUN")
        data.energy = energy
        o = bpy.data.objects.new(name, data)
        o.rotation_euler = tuple(math.radians(a) for a in rot)
        scene.collection.objects.link(o)
    floor = bpy.data.meshes.new("pv_floor")
    s = 20
    floor.from_pydata([(-s, -s, 0), (s, -s, 0), (s, s, 0), (-s, s, 0)], [], [(0, 1, 2, 3)])
    fo = bpy.data.objects.new("pv_floor", floor)
    fm = bpy.data.materials.new("pv_floor_mat")
    fm.use_nodes = True
    fm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.42, 0.40, 0.37, 1)
    fo.data.materials.append(fm)
    scene.collection.objects.link(fo)


def _shot(name, loc, target, lens):
    scene = bpy.context.scene
    cam = bpy.data.objects.get("pv_cam")
    if cam is None:
        cam = bpy.data.objects.new("pv_cam", bpy.data.cameras.new("pv_cam"))
        scene.collection.objects.link(cam)
    cam.data.lens = lens
    cam.data.clip_start = 0.01
    cam.location = loc
    cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    scene.camera = cam
    scene.render.filepath = os.path.join(PREVIEW_DIR, name)
    bpy.ops.render.render(write_still=True)


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mk.reset()
    mk.SCENE_UNITS_PER_METRE = 1.0
    coll = bpy.data.collections.new("tools")
    bpy.context.scene.collection.children.link(coll)
    built = {name: build(coll) for name, build in tools.MODELS.items()}
    for name, obj in built.items():
        parts = [obj] + [c for c in obj.children_recursive if c.type == "MESH"]
        tris = sum(len(p.vertices) - 2 for o in parts for p in o.data.polygons)
        kids = ", ".join(c.name for c in obj.children)
        print(f"[tools] {name}: {tris} tris; children: {kids}")
        export(obj, os.path.join(OUTPUT_DIR, f"{name}.fbx"))
    if PREVIEW_DIR:
        os.makedirs(PREVIEW_DIR, exist_ok=True)
        _render_setup()
        picker, cart = built["litter_picker"], built["shopping_cart"]
        cart.location.x = 3.0
        picker.location = (0, 0, 0.3)
        for jaw in (c for c in picker.children if c.name.startswith("Jaw")):
            jaw.rotation_euler.z = math.radians(-22 if jaw.location.x < 0 else 22)
        _shot("picker_side.png", (0.55, -0.35, 0.55), (0, -0.42, 0.3), 40)
        _shot("picker_head.png", (0.12, -1.05, 0.42), (0, -0.88, 0.3), 50)
        _shot("picker_grip.png", (0.18, 0.12, 0.40), (0, -0.04, 0.29), 50)
        _shot("cart_front.png", (3.0 + 1.6, -1.7, 1.5), (3.0, 0, 0.55), 40)
        _shot("cart_top.png", (3.0 + 0.5, 1.4, 2.0), (3.0, -0.05, 0.6), 40)


main()
