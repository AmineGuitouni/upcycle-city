"""Export the procedural manual crafting machines (upcycle_machines.py) to FBX for Unity.

Run from the Unity project root:

  /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup \
      --python Tools/Blender/export_machines.py -- \
      <path to blender-tests/upcycle_city/blender> \
      Assets/UpcycleCity/Art/UpcycleProps/Machines/Manual \
      [preview_dir]

Writes <output>/<machine>.fbx with each machine's animated pivots, markers and Upgrade_Lvl1 child.
When preview_dir is given, renders Level 0 and Level 1 comparison sheets for visual QA.
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
import upcycle_machines as machines  # noqa: E402


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
        filepath=path,
        use_selection=True,
        object_types={"MESH", "EMPTY"},
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


def _render_setup():
    scene = bpy.context.scene
    engines = bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items.keys()
    scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = 1400, 900
    if scene.world is None:
        scene.world = bpy.data.worlds.new("w")
    scene.world.use_nodes = True
    bg = scene.world.node_tree.nodes.get("Background")
    bg.inputs["Color"].default_value = (0.58, 0.68, 0.76, 1)
    bg.inputs["Strength"].default_value = 1.0
    scene.view_settings.view_transform = "Standard"
    for name, energy, rot in (("sun", 3.8, (52, 0, -35)), ("fill", 1.4, (60, 0, 145))):
        data = bpy.data.lights.new(name, "SUN")
        data.energy = energy
        o = bpy.data.objects.new(name, data)
        o.rotation_euler = tuple(math.radians(a) for a in rot)
        scene.collection.objects.link(o)
    floor = bpy.data.meshes.new("pv_floor")
    s = 40
    floor.from_pydata([(-s, -s, 0), (s, -s, 0), (s, s, 0), (-s, s, 0)], [], [(0, 1, 2, 3)])
    fo = bpy.data.objects.new("pv_floor", floor)
    fm = bpy.data.materials.new("pv_floor_mat")
    fm.use_nodes = True
    fm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.44, 0.43, 0.40, 1)
    fo.data.materials.append(fm)
    scene.collection.objects.link(fo)


def _shot(name, loc, target, lens=42):
    scene = bpy.context.scene
    cam = bpy.data.objects.get("pv_cam")
    if cam is None:
        cam = bpy.data.objects.new("pv_cam", bpy.data.cameras.new("pv_cam"))
        scene.collection.objects.link(cam)
    cam.data.lens = lens
    cam.data.clip_start = 0.05
    cam.location = loc
    cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    scene.camera = cam
    scene.render.filepath = os.path.join(PREVIEW_DIR, name)
    bpy.ops.render.render(write_still=True)


def _set_upgrade_visible(obj, visible):
    for c in obj.children_recursive:
        if c.name == "Upgrade_Lvl1" or c.name.startswith("Upgrade_Lvl1."):
            c.hide_render = not visible
            c.hide_viewport = not visible


def main():
    # Export each machine from its own clean scene so child pivot/marker names
    # (HopperZone, OutputPoint, SteamPoint, Upgrade_Lvl1, GlowMesh, CrankPivot, etc.)
    # are never auto-suffixed with .001/.002 by Blender.
    for name, build in machines.MODELS.items():
        bpy.ops.wm.read_factory_settings(use_empty=True)
        mk.reset()
        mk.SCENE_UNITS_PER_METRE = 1.0
        coll = bpy.data.collections.new("manual_machines")
        bpy.context.scene.collection.children.link(coll)
        obj = build(coll)
        parts = [obj] + [c for c in obj.children_recursive if c.type == "MESH"]
        tris = sum(len(p.vertices) - 2 for o in parts for p in o.data.polygons)
        kids = ", ".join(c.name for c in obj.children)
        print(f"[machines] {name}: {tris} tris; children: {kids}")
        export(obj, os.path.join(OUTPUT_DIR, f"{name}.fbx"))

    if PREVIEW_DIR:
        bpy.ops.wm.read_factory_settings(use_empty=True)
        mk.reset()
        mk.SCENE_UNITS_PER_METRE = 1.0
        coll = bpy.data.collections.new("manual_machines_preview")
        bpy.context.scene.collection.children.link(coll)
        built = {name: build(coll) for name, build in machines.MODELS.items()}

        os.makedirs(PREVIEW_DIR, exist_ok=True)
        _render_setup()
        # Arrange all 6 machines in a clean row for lineup renders (Level 0 vs Level 1)
        order = list(built.keys())
        spacing = 3.2
        for idx, name in enumerate(order):
            built[name].location = ((idx - (len(order) - 1) / 2.0) * spacing, 0.0, 0.0)

        for lvl, visible in ((0, False), (1, True)):
            for obj in built.values():
                _set_upgrade_visible(obj, visible)
            _shot(f"machines_lineup_lvl{lvl}.png", (0.0, -13.5, 4.8), (0.0, 0.0, 1.0), lens=36)

        # Close-up hero pair (Level 0 & Level 1) for each machine
        for idx, name in enumerate(order):
            cx = (idx - (len(order) - 1) / 2.0) * spacing
            for lvl, visible in ((0, False), (1, True)):
                _set_upgrade_visible(built[name], visible)
                _shot(f"{name}_lvl{lvl}.png", (cx + 2.3, -3.1, 2.0), (cx, 0.0, 0.95), lens=44)


if __name__ == "__main__":
    main()
