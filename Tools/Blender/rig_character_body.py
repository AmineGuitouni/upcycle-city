"""Adds a skeleton to the modular player bodies so they can walk, kneel and sit (intro cinematic).

Input:  Assets/UpcycleCity/Art/UpcycleProps/Character/body_male.fbx, body_female.fbx (static, from
        character_model.py via export_character_parts.py)
Output: Assets/UpcycleCity/Art/UpcycleProps/Character/Rigged/body_<sex>_rigged.fbx

The joints are the ones character_model.py builds the body around (metres, front towards -Y, feet on z = 0).
Bones are named for Unity's view of the character (it faces +Z there, so Blender -X is the character's right):
    Root > Hips > Spine > Chest > Neck > Head
    Hips > UpperLeg_L/R > LowerLeg_L/R > Foot_L/R > Toes_L/R
    Chest > Shoulder_L/R > UpperArm_L/R > LowerArm_L/R > Hand_L/R

Skinning is computed here rather than with Blender's bone heat (which fails on the body's 37 overlapping
primitives): limb pieces (trouser capsules, shoes, sleeves, gloves, hands, patches) are bound rigidly to their
nearest limb bone, so knees and elbows bend like ball joints with no stretching; the shirt, pelvis, coat and
scarf blend smoothly up the spine, the coat skirt follows the thighs part of the way, and the coat's shoulders
follow the upper arms a little. Material names are kept, so Unity's colour roles still apply.

Run headless:
    flatpak run org.blender.Blender -b --factory-startup --python Tools/Blender/rig_character_body.py -- \
        <character dir> <output dir> [preview.png]
"""

import math
import os
import sys

import bmesh
import bpy
from mathutils import Euler, Vector


def smoothstep(e0, e1, x):
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3.0 - 2.0 * t)


# ---------------------------------------------------------------- skeleton

def bone_table(female):
    hip_x = 0.092 if female else 0.086
    sh_x = 0.15 if female else 0.163
    hip_z, knee_z, ankle_z = 0.84, 0.47, 0.095
    bones = [
        # name, head, tail, parent
        ("Root", (0, 0, 0), (0, 0, 0.12), None),
        ("Hips", (0, 0, hip_z), (0, 0, 0.96), "Root"),
        ("Spine", (0, 0, 0.96), (0, 0, 1.14), "Hips"),
        ("Chest", (0, 0, 1.14), (0, 0, 1.38), "Spine"),
        ("Neck", (0, 0, 1.38), (0, 0, 1.52), "Chest"),
        ("Head", (0, 0, 1.52), (0, 0, 1.82), "Neck"),
    ]
    for bx, tag in ((-1, "R"), (1, "L")):
        hip = (bx * hip_x, 0.0, hip_z)
        knee = (bx * (hip_x + 0.006), -0.008, knee_z)
        ankle = (bx * (hip_x + 0.004), 0.0, ankle_z)
        ball = (bx * (hip_x + 0.004), -0.11, 0.03)
        toe = (bx * (hip_x + 0.004), -0.19, 0.025)
        shoulder = (bx * sh_x, 0.004, 1.335)
        elbow = (bx * (sh_x + 0.05), 0.012, 1.07)
        wrist = (bx * (sh_x + 0.066), -0.012, 0.83)
        hand_end = (bx * (sh_x + 0.072), -0.02, 0.68)
        bones += [
            (f"UpperLeg_{tag}", hip, knee, "Hips"),
            (f"LowerLeg_{tag}", knee, ankle, f"UpperLeg_{tag}"),
            (f"Foot_{tag}", ankle, ball, f"LowerLeg_{tag}"),
            (f"Toes_{tag}", ball, toe, f"Foot_{tag}"),
            (f"Shoulder_{tag}", (bx * 0.05, 0.0, 1.33), shoulder, "Chest"),
            (f"UpperArm_{tag}", shoulder, elbow, f"Shoulder_{tag}"),
            (f"LowerArm_{tag}", elbow, wrist, f"UpperArm_{tag}"),
            (f"Hand_{tag}", wrist, hand_end, f"LowerArm_{tag}"),
        ]
    return bones


def build_armature(body, female):
    data = bpy.data.armatures.new("Rig")
    rig = bpy.data.objects.new("Rig", data)
    bpy.context.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    table = bone_table(female)
    for name, head, tail, parent in table:
        eb = data.edit_bones.new(name)
        eb.head = Vector(head)
        eb.tail = Vector(tail)
        eb.roll = 0.0
        if parent:
            eb.parent = data.edit_bones[parent]
            eb.use_connect = False
    bpy.ops.object.mode_set(mode="OBJECT")
    return rig, table


# ---------------------------------------------------------------- skinning

def seg_distance(p, a, b):
    ab = b - a
    t = max(0.0, min(1.0, (p - a).dot(ab) / max(ab.length_squared, 1e-9)))
    return (p - (a + ab * t)).length


def islands(bm):
    """Vertex index lists of each loose part."""
    seen = set()
    parts = []
    for v in bm.verts:
        if v.index in seen:
            continue
        part = []
        stack = [v]
        while stack:
            x = stack.pop()
            if x.index in seen:
                continue
            seen.add(x.index)
            part.append(x.index)
            for e in x.link_edges:
                stack.append(e.other_vert(x))
        parts.append(part)
    return parts


def chain_weights(co, chain, segs, power=6.0):
    """Inverse-distance weights over a limb chain: a vertex mid-bone belongs to that bone, one at a joint is
    shared about evenly, which bends the tube smoothly."""
    raw = {}
    for name in chain:
        d = max(seg_distance(co, *segs[name]), 1e-4)
        raw[name] = 1.0 / d ** power
    total = sum(raw.values())
    w = {k: v / total for k, v in raw.items()}
    # Drop negligible influences (Unity keeps 4 per vertex) and renormalise
    w = dict(sorted(w.items(), key=lambda kv: -kv[1])[:3])
    w = {k: v for k, v in w.items() if v > 0.02}
    total = sum(w.values())
    return {k: v / total for k, v in w.items()}


def torso_weights(co):
    """Smooth spine blend for the shirt, pelvis, coat and scarf, with skirt and shoulder sharing."""
    z = co.z
    w = {}

    def add(bone, value):
        if value > 1e-4:
            w[bone] = w.get(bone, 0.0) + value

    # Up the spine
    hips = 1.0 - smoothstep(0.9, 1.02, z)
    chest = smoothstep(1.1, 1.22, z)
    neck = smoothstep(1.4, 1.48, z)
    spine = max(0.0, 1.0 - hips - chest)
    chest *= 1.0 - neck
    add("Hips", hips)
    add("Spine", spine)
    add("Chest", chest)
    add("Neck", neck)

    side = "R" if co.x < 0 else "L"
    # The coat skirt and the trouser seat follow the thigh on their side part of the way
    if z < 0.88:
        share = smoothstep(0.88, 0.58, z) * 0.62 * smoothstep(0.0, 0.11, abs(co.x))
        for k in list(w):
            w[k] *= 1.0 - share
        add(f"UpperLeg_{side}", share)
    # The coat's shoulders lift a little with the arms
    if z > 1.18 and abs(co.x) > 0.12:
        share = smoothstep(0.12, 0.2, abs(co.x)) * smoothstep(1.18, 1.3, z) * 0.45
        for k in list(w):
            w[k] *= 1.0 - share
        add(f"UpperArm_{side}", share)

    total = sum(w.values())
    return {k: v / total for k, v in w.items()}


def skin(body, rig, table):
    me = body.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.verts.ensure_lookup_table()
    segs = {name: (Vector(h), Vector(t)) for name, h, t, _ in table}
    for name, *_ in table:
        if name != "Root":
            body.vertex_groups.new(name=name)

    limb_bones = {
        "leg": ["UpperLeg_{}", "LowerLeg_{}", "Foot_{}", "Toes_{}"],
        "arm": ["UpperArm_{}", "LowerArm_{}", "Hand_{}"],
    }
    rigid, smooth, tubes = 0, 0, 0
    for part in islands(bm):
        pts = [bm.verts[i].co for i in part]
        c = sum(pts, Vector()) / len(pts)
        zmin = min(p.z for p in pts)
        zmax = max(p.z for p in pts)
        side = "R" if c.x < 0 else "L"
        kind = None
        # Arms hang outside the hips (|x| > 0.15), so test them first: the hands reach below the hip line
        arm_names = [n.format(side) for n in limb_bones["arm"]]
        near_arm = min(seg_distance(c, *segs[n]) for n in arm_names) < 0.075  # not the coat's pockets
        if abs(c.x) > 0.15 and 0.6 < c.z < 1.37 and zmax - zmin < 0.45 and near_arm:
            kind = "arm"
        elif abs(c.x) > 0.05 and zmax < 0.9 and zmin < 0.8:
            kind = "leg"

        names = [n.format(side) for n in limb_bones[kind]] if kind else []
        nearest = min(names, key=lambda n: seg_distance(c, *segs[n])) if kind else None
        is_end = nearest is not None and (nearest.startswith(("Foot", "Toes", "Hand")) or (kind == "leg" and zmax < 0.2))
        if kind and not is_end:
            # Trouser legs, sleeves and the patches on them: blend across the joints so knees and elbows
            # bend as one continuous tube instead of two capsules crossing
            chain = ["Hips"] + [n.format(side) for n in limb_bones["leg"][:3]] if kind == "leg" \
                else [f"Shoulder_{side}"] + [n.format(side) for n in limb_bones["arm"]]
            for i in part:
                for bone, value in chain_weights(bm.verts[i].co, chain, segs).items():
                    body.vertex_groups[bone].add([i], value, "REPLACE")
            tubes += 1
        elif kind:
            # Rigid: shoes, soles, gloves and hands go to the end bone nearest their centre
            body.vertex_groups[nearest].add(part, 1.0, "REPLACE")
            rigid += 1
        else:
            for i in part:
                for bone, value in torso_weights(bm.verts[i].co).items():
                    body.vertex_groups[bone].add([i], value, "REPLACE")
            smooth += 1
    bm.free()
    print(f"[rig] {body.name}: {tubes} blended limb tubes, {rigid} rigid limb pieces, {smooth} smooth torso pieces")

    body.parent = rig
    mod = body.modifiers.new("Armature", "ARMATURE")
    mod.object = rig


# ---------------------------------------------------------------- pipeline

def import_body(path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path)
    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    for o in bpy.data.objects:
        o.select_set(o in meshes)
    bpy.context.view_layer.objects.active = meshes[0]
    # Bake the FBX axis conversion so the data is Z-up in metres, like character_model.py
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    body = meshes[0]
    for o in [o for o in bpy.data.objects if o.type != "MESH"]:
        bpy.data.objects.remove(o)
    return body


def export(rig, body, path):
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    body.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        object_types={"ARMATURE", "MESH"},
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        bake_space_transform=False,
        use_mesh_modifiers=False,
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
        primary_bone_axis="Y",
        secondary_bone_axis="X",
        armature_nodetype="NULL",
        bake_anim=False,
    )
    print(f"[rig] exported {path}")


def preview(rig, path):
    """Renders a test pose (stride, bent knee, raised arm) to check the weights."""
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="POSE")
    pb = rig.pose.bones
    for b in pb:
        b.rotation_mode = "XYZ"
    pb["UpperLeg_R"].rotation_euler = Euler((math.radians(-40), 0, 0))
    pb["LowerLeg_R"].rotation_euler = Euler((math.radians(100), 0, 0))
    pb["UpperLeg_L"].rotation_euler = Euler((math.radians(25), 0, 0))
    pb["UpperArm_L"].rotation_euler = Euler((math.radians(-70), 0, 0))
    pb["LowerArm_L"].rotation_euler = Euler((math.radians(-50), 0, 0))
    pb["Spine"].rotation_euler = Euler((math.radians(12), 0, 0))
    bpy.ops.object.mode_set(mode="OBJECT")

    scene = bpy.context.scene
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam"))
    scene.collection.objects.link(cam)
    cam.location = (3.2, -0.9, 1.0)
    cam.rotation_euler = (Vector((0, 0, 0.85)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    scene.camera = cam
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.rotation_euler = (math.radians(50), 0, math.radians(30))
    scene.collection.objects.link(sun)
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.color_type = "MATERIAL"
    scene.render.resolution_x, scene.render.resolution_y = 700, 800
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print(f"[rig] preview {path}")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    src = argv[0] if argv else "Assets/UpcycleCity/Art/UpcycleProps/Character"
    out = argv[1] if len(argv) > 1 else os.path.join(src, "Rigged")
    preview_path = argv[2] if len(argv) > 2 else None
    os.makedirs(out, exist_ok=True)
    for sex in ("male", "female"):
        body = import_body(os.path.join(src, f"body_{sex}.fbx"))
        body.name = f"body_{sex}"
        rig, table = build_armature(body, sex == "female")
        skin(body, rig, table)
        export(rig, body, os.path.join(out, f"body_{sex}_rigged.fbx"))
        if preview_path and sex == "male":
            preview(rig, preview_path)


if __name__ == "__main__":
    main()
