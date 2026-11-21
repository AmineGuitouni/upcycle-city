"""Export the procedural Upcycle City models to FBX for Unity.

Builds every model the Upcycle City Blender libraries register (litter, city props, landmarks, the
garage, machines, works and clip props) in metres, and writes one FBX per model, sorted into a
folder per group. Unity's PollutionModelPostprocessor gives them Pollution Lit materials on import.

Run from the Unity project root:

  /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup \
      --python Tools/Blender/export_upcycle_props.py -- \
      <path to upcycle_city/blender> Assets/UpcycleCity/Art/UpcycleProps [manifest.json]

Axes: models are modelled front towards -Y, origin on the floor. They are exported Y-up with the
transform baked, so in Unity every model has an identity rotation and its origin on the floor.
"""

import json
import os
import sys

import bpy

argv = sys.argv[sys.argv.index("--") + 1:]
LIBRARY_DIR = os.path.abspath(argv[0])
OUTPUT_DIR = os.path.abspath(argv[1])
MANIFEST = os.path.abspath(argv[2]) if len(argv) > 2 else None
sys.path.insert(0, LIBRARY_DIR)

import clip_library as clip  # noqa: E402  (needs LIBRARY_DIR on sys.path first)
import garage_library as garage  # noqa: E402
import machines_library as machines  # noqa: E402
import mesh_kit as mk  # noqa: E402
import props_library as props  # noqa: E402
import trash_library as trash  # noqa: E402
import works_library as works  # noqa: E402

# trash_library models in KayKit units (one road tile = 2 units = 8.8 m); everything else is
# built in metres once mk.SCENE_UNITS_PER_METRE is 1
KAYKIT_UNIT_IN_METRES = 4.4


def build_models():
    """Build every library model; returns {name: folder}."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mk.reset()
    mk.SCENE_UNITS_PER_METRE = 1.0
    folders = {}

    def collect(default_folder, build):
        before = set(trash.LIBRARY)
        build()
        for name in trash.LIBRARY:
            if name in before:
                continue
            kind = trash.LIBRARY[name]["kind"]
            if default_folder == "City" and kind == "person":
                folders[name] = "People"
            elif default_folder == "City" and kind == "landmark":
                folders[name] = "Landmarks"
            else:
                folders[name] = default_folder

    collect("Litter", trash.build_library)
    collect("City", lambda: props.build_props(include_landmarks=True))
    collect("Garage", garage.build)
    collect("Machines", machines.build)
    collect("Works", works.build)
    collect("Clip", clip.build)
    return folders


# ---------------------------------------------------------------- materials

def _colour_inputs(node, name):
    return [s for s in node.inputs if s.name == name and s.type == "RGBA"]


def representative_colour(socket, depth=0):
    """A flat colour for a Base Color socket, following procedural nodes (ramps, mixes, hue/sat)."""
    if not socket.is_linked:
        return tuple(socket.default_value)[:3] if socket.type == "RGBA" else None
    if depth > 8:
        return None
    node = socket.links[0].from_node
    if node.type == "RGB":
        return tuple(node.outputs[0].default_value)[:3]
    if node.type == "VALTORGB":
        elements = node.color_ramp.elements
        return tuple(sum(e.color[i] for e in elements) / len(elements) for i in range(3))
    if node.type in ("MIX", "MIX_RGB"):
        a = _colour_inputs(node, "A") or _colour_inputs(node, "Color1")
        b = _colour_inputs(node, "B") or _colour_inputs(node, "Color2")
        ca = representative_colour(a[0], depth + 1) if a else None
        cb = representative_colour(b[0], depth + 1) if b else None
        if ca and cb:
            if node.blend_type == "MULTIPLY":
                return tuple(ca[i] * (0.5 + 0.5 * cb[i]) for i in range(3))
            return tuple((ca[i] + cb[i]) / 2 for i in range(3))
        return ca or cb
    for candidate in node.inputs:
        if candidate.type == "RGBA":
            colour = representative_colour(candidate, depth + 1)
            if colour:
                return colour
    return None


def flatten_materials():
    """FBX only carries flat colours: replace procedural base colours with a representative one."""
    for mat in bpy.data.materials:
        if not mat.node_tree:
            continue
        bsdf = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None:
            continue
        base = bsdf.inputs["Base Color"]
        if not base.is_linked:
            continue
        colour = representative_colour(base) or tuple(mat.diffuse_color)[:3]
        for link in list(base.links):
            mat.node_tree.links.remove(link)
        base.default_value = (*colour, 1.0)


# ---------------------------------------------------------------- export

def export(name, folder):
    obj = trash.LIBRARY[name]["proto"]
    for other in bpy.context.view_layer.objects:
        other.select_set(False)
    obj.hide_set(False)
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj

    path = os.path.join(OUTPUT_DIR, folder, name + ".fbx")
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        object_types={"MESH"},
        global_scale=KAYKIT_UNIT_IN_METRES if folder == "Litter" else 1.0,
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
    scale = KAYKIT_UNIT_IN_METRES if folder == "Litter" else 1.0
    corners = [v.co for v in obj.data.vertices]
    return {
        "folder": folder,
        "kind": trash.LIBRARY[name]["kind"],
        "min": [min(c[i] for c in corners) * scale for i in range(3)],
        "max": [max(c[i] for c in corners) * scale for i in range(3)],
    }


def main():
    folders = build_models()
    collection = trash._COLLECTION
    collection.hide_viewport = False
    collection.hide_render = False
    flatten_materials()

    manifest = {}
    for name in sorted(folders):
        manifest[name] = export(name, folders[name])
    if MANIFEST:
        with open(MANIFEST, "w") as f:
            json.dump(manifest, f, indent=1)

    counts = {}
    for entry in manifest.values():
        counts[entry["folder"]] = counts.get(entry["folder"], 0) + 1
    print("EXPORTED", len(manifest), "models:", counts)


main()
