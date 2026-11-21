"""Export the Avenue Habib Bourguiba models (bourguiba_landmarks.py, this folder) to FBX for Unity.

Run from the Unity project root:

  /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup \
      --python Tools/Blender/export_bourguiba.py -- \
      <path to upcycle_city/blender> Assets/UpcycleCity/Art/UpcycleProps/Bourguiba [preview_dir] [groups]

Writes <output>/<Group>/<model>.fbx for the groups Landmarks (bourguiba_landmarks.py), Buildings
(bourguiba_buildings.py + bourguiba_interiors.py), Litter (bourguiba_trash.py), Street (bourguiba_street.py:
the municipal bin in parts, the street lamp) and Industry (bourguiba_industry.py: the factory yard);
groups is an optional comma list to build only some.
Models face -Y in Blender (+Z in Unity) with their origin on the floor; meshes are triangulated on export so
concave outlines (stars, arches) import cleanly. Shops export a ShopPoint empty. With a preview_dir it also
renders daylight previews for review.
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
GROUPS = argv[3].split(",") if len(argv) > 3 else None
sys.dont_write_bytecode = True
sys.path.insert(0, LIBRARY_DIR)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import mesh_kit as mk  # noqa: E402  (needs LIBRARY_DIR on sys.path first)
import bourguiba_buildings as buildings  # noqa: E402
import bourguiba_landmarks as landmarks  # noqa: E402
import bourguiba_industry as industry  # noqa: E402
import bourguiba_street as street_kit  # noqa: E402
import bourguiba_trash as trash  # noqa: E402

MODULES = {"Landmarks": landmarks, "Buildings": buildings, "Litter": trash, "Street": street_kit, "Industry": industry}


def build_all(coll):
    """Build the chosen groups; returns {group: {name: object}}."""
    out = {}
    for group, module in MODULES.items():
        if GROUPS and group not in GROUPS:
            continue
        out[group] = {name: build(coll) for name, build in module.MODELS.items()}
    return out


def export(obj, path):
    bpy.context.view_layer.update()   # temporary text objects removed while building can leave stale entries
    for other in bpy.context.view_layer.objects:
        if other is not None:
            other.select_set(False)
    obj.select_set(True)
    for child in obj.children:
        child.select_set(True)
    bpy.context.view_layer.objects.active = obj
    used = {p.material_index for p in obj.data.polygons}   # drop material slots no face uses
    mats = list(obj.data.materials)
    if len(used) < len(mats):
        keep = sorted(used)
        remap = {old: new for new, old in enumerate(keep)}
        indices = [remap[p.material_index] for p in obj.data.polygons]
        obj.data.materials.clear()
        for old in keep:
            obj.data.materials.append(mats[old])
        for p, i in zip(obj.data.polygons, indices):
            p.material_index = i
    if not any(m.type == "TRIANGULATE" for m in obj.modifiers):
        tri = obj.modifiers.new("triangulate", "TRIANGULATE")
        tri.quad_method = "BEAUTY"
        tri.ngon_method = "BEAUTY"
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"MESH", "EMPTY"}, global_scale=1.0, apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y", bake_space_transform=True,
        use_mesh_modifiers=True, mesh_smooth_type="FACE", add_leaf_bones=False, bake_anim=False)


# ---------------------------------------------------------------- previews

def _setup_render(width, height):
    scene = bpy.context.scene
    engines = bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items.keys()
    scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = width, height
    if scene.world is None:
        scene.world = bpy.data.worlds.new("w")
    scene.world.use_nodes = True
    bg = scene.world.node_tree.nodes.get("Background")
    bg.inputs["Color"].default_value = (0.42, 0.62, 0.92, 1)
    bg.inputs["Strength"].default_value = 0.9
    scene.view_settings.view_transform = "Standard"
    return scene


def _light(name, kind, energy, rot):
    data = bpy.data.lights.new(name, kind)
    data.energy = energy
    obj = bpy.data.objects.new(name, data)
    obj.rotation_euler = tuple(math.radians(a) for a in rot)
    bpy.context.scene.collection.objects.link(obj)


def _camera(loc, target, lens):
    cam = bpy.data.objects.get("pv_cam")
    if cam is None:
        cam = bpy.data.objects.new("pv_cam", bpy.data.cameras.new("pv_cam"))
        bpy.context.scene.collection.objects.link(cam)
    cam.data.lens = lens
    cam.data.clip_end = 1000
    cam.location = loc
    cam.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    bpy.context.scene.camera = cam


def _ground(size=200):
    floor = bpy.data.meshes.new("pv_floor")
    floor.from_pydata([(-size, -size, 0), (size, -size, 0), (size, size, 0), (-size, size, 0)], [], [(0, 1, 2, 3)])
    fo = bpy.data.objects.new("pv_floor", floor)
    fm = bpy.data.materials.new("pv_floor_mat")
    fm.use_nodes = True
    fm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.36, 0.35, 0.33, 1)
    fo.data.materials.append(fm)
    bpy.context.scene.collection.objects.link(fo)


def _shot(name, loc, target, lens, width=None, height=None):
    scene = bpy.context.scene
    if width:
        scene.render.resolution_x, scene.render.resolution_y = width, height
    _camera(loc, target, lens)
    scene.render.filepath = os.path.join(PREVIEW_DIR, name)
    bpy.ops.render.render(write_still=True)


def _hide_all_but(objs):
    keep = set(objs)
    for o in bpy.data.objects:
        if o.type == "MESH" and not o.name.startswith("pv_"):
            o.hide_render = o not in keep and o.parent not in keep


def _row(objs, gap=0.0):
    """Line models up side by side along X (facades on the same line); returns the total length."""
    x = 0.0
    for o in objs:
        w = o.get("bb_width") or (max(v.co.x for v in o.data.vertices) - min(v.co.x for v in o.data.vertices))
        o.location = (x + w / 2, 0, 0)
        x += w + gap
    return x


def _lineup(objs, name, gap, height):
    _hide_all_but(objs)
    x = 0.0
    for o in objs:
        xs = [v.co.x for v in o.data.vertices]
        o.location = (x - min(xs), 0, 0)
        x += max(xs) - min(xs) + gap
    x -= gap
    lens = 35
    dist = (x / 2 * 1.1) / math.tan(math.atan(18 / lens))
    _shot(name, (x / 2, -dist, height * 0.9 + dist * 0.18), (x / 2, 0, height * 0.4), lens, 2400, 1000)


def render_previews(built):
    _setup_render(1100, 1400)
    _ground()
    _light("sun", "SUN", 4.0, (50, 0, -35))
    _light("sky_fill", "SUN", 0.8, (60, 0, 150))

    if "Landmarks" in built:
        tower = built["Landmarks"]["clocktower"]
        _hide_all_but(built["Landmarks"].values())
        _shot("clocktower_front.png", (3.5, -26.0, 1.7), (0, 0, 17.0), 24)
        _shot("clocktower_top.png", (4.5, -11.0, 28.0), (0, 0, 32.4), 50)
        tower.hide_render = True
        built["Landmarks"]["clocktower_fountain"].hide_render = True

    if "Buildings" in built:
        objs = built["Buildings"]
        fillers = [o for n, o in objs.items() if n.startswith("bb_")]
        shops = [o for n, o in objs.items() if n.startswith("shop_")]
        _hide_all_but(fillers + shops)
        street = _row(fillers)
        shops_len = _row(shops)
        for o in shops:
            o.location.y = -30.0            # shops across the "street", facing the fillers
            o.location.x += 4.0
            o.rotation_euler.z = math.pi
            o.location.x = shops_len - o.location.x + 4.0
        _shot("buildings_street.png", (street / 2, -22.0, 1.7), (street / 2, 0, 9.0), 18, 2400, 1100)
        for o in shops:
            o.location.y, o.rotation_euler.z = 0.0, 0.0
            o.location.x = o.location.x
        for o in fillers:
            o.hide_render = True
        _row(shops, gap=1.0)
        _shot("shops_street.png", (shops_len / 2, -15.0, 1.7), (shops_len / 2, 0, 5.5), 20, 2400, 1100)
        for o in shops:
            _shot(f"{o.name}.png", (o.location.x + 3.0, o.location.y - 9.5, 1.7), (o.location.x, 0, 3.2), 24, 1400, 1000)
        bpy.context.view_layer.update()
        for o in shops:   # interiors, lit by point lights on the InteriorLight markers
            lamps = []
            for c in o.children:
                if c.name.startswith("InteriorLight"):
                    data = bpy.data.lights.new("pv_lamp", "POINT")
                    data.energy, data.shadow_soft_size = 70, 0.4
                    lamp = bpy.data.objects.new("pv_lamp", data)
                    lamp.location = c.matrix_world.translation
                    bpy.context.scene.collection.objects.link(lamp)
                    lamps.append(lamp)
            x, w = o.location.x, o["bb_width"]
            _shot(f"{o.name}_inside.png", (x - 0.3, 0.2, 1.7), (x + 0.4, 9.0, 1.1), 14, 1600, 1000)
            _shot(f"{o.name}_inside_back.png", (x + w / 2 - 2.6, 8.0, 2.4), (x - 1.5, 1.2, 0.7), 15, 1600, 1000)
            for lamp in lamps:
                bpy.data.objects.remove(lamp)
        for o in shops:
            o.hide_render = True

    if "Street" in built:
        objs = built["Street"]
        body, lid, contents, overflow = (objs[n] for n in ("municipal_bin_body", "municipal_bin_lid", "municipal_bin_contents",
                                                           "municipal_bin_overflow"))
        # three bins: nearly empty, half full, overflowing with the lid thrown open
        _hide_all_but([])
        shown = []
        for i, (fill, lid_open, over) in enumerate(((0.15, 0.0, False), (0.6, 0.0, False), (1.0, 80.0, True))):
            x = i * 2.2
            for src in (body, contents, lid) + ((overflow,) if over else ()):
                o = bpy.data.objects.new(f"pv_{src.name}_{i}", src.data)
                bpy.context.scene.collection.objects.link(o)
                o.location = (x, 0, 0)
                if src is lid:
                    o.location = (x, street_kit.HINGE_Y, street_kit.HINGE_Z)
                    o.rotation_euler = (math.radians(-lid_open), 0, 0)
                if src is contents:
                    heap = 0.55
                    top = street_kit.FLOOR_Z + fill * (street_kit.RIM_Z - street_kit.FLOOR_Z + 0.15)
                    o.location = (x, 0, max(street_kit.FLOOR_Z, top - heap))
                    o.scale = (1, 1, max(0.05, min(1.0, (top - street_kit.FLOOR_Z) / heap)))
                shown.append(o)
        lamp = objs["street_lamp"]
        lamp.hide_render = False
        lamp.location = (7.2, 0.5, 0)
        _shot("street_bins.png", (2.6, -5.2, 3.4), (2.6, 0, 0.8), 35, 2000, 1100)
        _shot("street_lamp.png", (7.2, -6.0, 2.6), (7.2, 0.5, 2.6), 35, 900, 1200)
        for o in shown:
            o.hide_render = True
        lamp.hide_render = True

    if "Industry" in built:
        _lineup(list(built["Industry"].values()), "industry.png", 4.0, 14.0)

    if "Litter" in built:
        objs = built["Litter"]
        big = [o for n, o in objs.items() if max(v.co.z for v in o.data.vertices) >= 0.3]
        small = [o for o in objs.values() if o not in big]
        for name, group, gap, height in (("litter_big.png", big, 0.6, 1.6), ("litter_small.png", small, 0.08, 0.25)):
            _hide_all_but(group)
            x = 0.0
            for o in group:
                xs = [v.co.x for v in o.data.vertices]
                o.location = (x - min(xs), 0, 0)
                x += max(xs) - min(xs) + gap
            x -= gap
            lens = 35
            dist = (x / 2 * 1.08) / math.tan(math.atan(18 / lens))
            _shot(name, (x / 2, -dist, height * 1.6 + dist * 0.25), (x / 2, 0, height * 0.35), lens, 2400, 900)


def _copy(src, loc, yaw_deg, coll):
    o = bpy.data.objects.new(f"pv_{src.name}", src.data)
    o.location = loc
    o.rotation_euler = (0, 0, math.radians(yaw_deg))
    coll.objects.link(o)
    return o


def _slab(name, x0, x1, y0, y1, z, colour, coll):
    me = bpy.data.meshes.new(name)
    me.from_pydata([(x0, y0, z), (x1, y0, z), (x1, y1, z), (x0, y1, z)], [], [(0, 1, 2, 3)])
    o = bpy.data.objects.new(name, me)
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    m.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (*colour, 1)
    m.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.9
    me.materials.append(m)
    coll.objects.link(o)


NATURE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Assets", "UpcycleCity", "Art",
                          "QuaterniusNature")


def _import_model(path, height):
    """Import an FBX as one hidden mesh, scaled to the given height with its base on the floor (preview only)."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=path)
    new = [o for o in bpy.data.objects if o not in before]
    meshes = [o for o in new if o.type == "MESH"]
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    bpy.ops.object.parent_clear(type="CLEAR_KEEP_TRANSFORM")
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    for o in new:
        if o != obj and o.name in bpy.data.objects:
            bpy.data.objects.remove(o)
    zs = [v.co.z for v in obj.data.vertices]
    k = height / (max(zs) - min(zs))
    low = min(zs)
    for v in obj.data.vertices:
        v.co.z -= low
        v.co *= k
    for mat in obj.data.materials:   # Quaternius FBX materials come in with alpha 0
        bsdf = mat.node_tree.nodes.get("Principled BSDF") if mat.node_tree else None
        if bsdf:
            bsdf.inputs["Alpha"].default_value = 1.0
        if hasattr(mat, "surface_render_method"):
            mat.surface_render_method = "DITHERED"
    obj.name = "pv_src_" + os.path.splitext(os.path.basename(path))[0]
    obj.hide_render = True
    return obj


def render_square(built):
    """The clock tower in place: its square at the end of the avenue, shops and buildings along both sides."""
    for o in bpy.data.objects:
        if o.type == "MESH":
            o.hide_render = True
    coll = bpy.data.collections.new("preview_square")
    bpy.context.scene.collection.children.link(coll)
    L, B, T = built["Landmarks"], built["Buildings"], built["Litter"]
    tower_y = 60.0
    _copy(L["clocktower"], (0, tower_y, 0), 0, coll)
    _copy(L["clocktower_fountain"], (0, tower_y, 0), 0, coll)

    # ground: square paving, avenue asphalt, pavements, the central tree-lined promenade
    _slab("pv_asphalt", -60, 60, -80, 110, 0.0, (0.16, 0.16, 0.17), coll)
    _slab("pv_square", -14, 14, tower_y - 14, tower_y + 14, 0.02, (0.62, 0.6, 0.55), coll)
    for sx in (-1, 1):
        _slab(f"pv_pavement_{sx}", sx * 17, sx * 23, -80, 44, 0.15, (0.58, 0.56, 0.52), coll)
    _slab("pv_promenade", -4, 4, -80, 38, 0.15, (0.66, 0.62, 0.55), coll)
    # the project's own Quaternius trees: common trees along the promenade, palms round the square
    trees = [_import_model(os.path.join(NATURE_DIR, f"CommonTree_{i}.fbx"), 7.5) for i in (1, 2, 3, 4, 5)]
    palms = [_import_model(os.path.join(NATURE_DIR, f"PalmTree_{i}.fbx"), 9.0) for i in (1, 2, 3)]
    for i in range(10):
        for k, sx in enumerate((-1, 1)):
            y = -76 + i * 11.0
            _copy(trees[(i * 2 + k) % len(trees)], (sx * 2.6, y, 0.15), (i * 73 + k * 151) % 360, coll)
    for i, (x, y) in enumerate(((-12, tower_y - 12), (12, tower_y - 12), (-12, tower_y + 12), (12, tower_y + 12),
                                (-12, tower_y), (12, tower_y))):
        _copy(palms[i % len(palms)], (x, y, 0.02), i * 60, coll)

    # buildings: each side of the avenue facing the road, the square closed by more facades behind the tower
    order = ["shop_cafe", "bb_building_a", "bb_building_gate", "shop_recycling", "bb_building_b", "shop_grocery",
             "bb_building_c", "bb_building_d", "shop_tools", "bb_building_e"]
    for side, yaw in ((-1, 90), (1, -90)):
        y = 44.0
        for k in range(len(order)):
            src = B[order[(k + (0 if side < 0 else 5)) % len(order)]]
            w = src["bb_width"]
            y -= w / 2
            _copy(src, (side * 23, y, 0.15), yaw, coll)
            y -= w / 2
            if y < -80:
                break
    x = -40.0
    for name in ("bb_building_e", "bb_building_c", "bb_building_b", "bb_building_a", "bb_building_d"):
        w = B[name]["bb_width"]
        _copy(B[name], (x + w / 2, tower_y + 24, 0), 0, coll)      # facades face down the avenue
        x += w

    # street furniture: municipal bins at the kerb, a skip, litter on the pavements
    for i, y in enumerate((30, 8, -18, -44)):
        side = -1 if i % 2 else 1
        _copy(T["municipal_bin_overflow" if i % 2 else "municipal_bin"], (side * 18.2, y, 0.15), 90 * side, coll)
        _copy(T["bag_pile"], (side * 18.4, y - 2.2, 0.15), 30, coll)
    _copy(T["skip_full"], (19.5, -30, 0.15), 90, coll)
    for i, name in enumerate(("bottle_water", "cup_espresso", "kaskrout_paper", "tin_tuna", "chair_monobloc_broken",
                              "bottle_water_crushed", "cup_espresso_tan", "glibettes_shells")):
        _copy(T[name], ((-1) ** i * (18 + (i % 3)), 20 - i * 6.5, 0.15), i * 47, coll)

    _setup_render(2400, 1300)
    _shot("square_avenue.png", (9.5, -30.0, 1.8), (0, tower_y, 15.0), 24, 2400, 1300)
    _shot("square_close.png", (11.0, 22.0, 1.8), (0, tower_y, 17.0), 20, 2400, 1300)
    _shot("square_aerial.png", (0.0, -62.0, 62.0), (0, 40, 0), 30, 2400, 1300)


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mk.reset()
    mk.SCENE_UNITS_PER_METRE = 1.0
    coll = bpy.data.collections.new("bourguiba")
    bpy.context.scene.collection.children.link(coll)
    built = build_all(coll)
    for group, objects in built.items():
        for name, obj in objects.items():
            tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
            print(f"[bourguiba] {group}/{name}: {tris} tris, {len(obj.data.materials)} materials")
            export(obj, os.path.join(OUTPUT_DIR, group, f"{name}.fbx"))
    if PREVIEW_DIR:
        os.makedirs(PREVIEW_DIR, exist_ok=True)
        render_previews(built)
        if all(g in built for g in MODULES):
            render_square(built)


main()
