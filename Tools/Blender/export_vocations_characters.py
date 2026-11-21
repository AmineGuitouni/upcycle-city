"""Export the characters of "Vocations and Uniforms.blend" to FBX for Unity.

For every character it writes:
  Rigged/<Name>.fbx          the skinned mesh and its deform bones in T-pose, imported as Humanoid for Mixamo animations
  Rigged/<Name>_Mask.fbx     the same wearing a smog mask skinned to the head (adults only)
  <Name>_<Pose>.fbx          a static mesh posed as Stand, Walk or Sit, for placing in scenes
  <Name>_<Pose>_Mask.fbx     the same with a smog mask, for the polluted city's crowd (adults only)
and the shared palette texture. Characters are scaled to life size and face -Y in Blender, like the
Upcycle City props, so in Unity they face +Z with their origin on the floor under the hips.

  /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup "<path>/Vocations and Uniforms.blend" \
      --python Tools/Blender/export_vocations_characters.py -- Assets/UpcycleCity/Art/VocationsCharacters [preview.png]
"""

import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector, kdtree

argv = sys.argv[sys.argv.index("--") + 1:]
OUTPUT_DIR = os.path.abspath(argv[0])
PREVIEW = os.path.abspath(argv[1]) if len(argv) > 1 else None

# The pack's adults are about 1.05 units tall; this makes them 1.78 m
LIFE_SIZE = 1.7

# Mesh object in the .blend -> character name
CHARACTERS = {
    "BaseMaleMesh": "Male",
    "BaseFemaleMesh.001": "Female",
    "BaseMaleMesh.001": "Child_A",
    "BaseMaleMesh.003": "Child_B",
    "DocMaleMesh": "Doctor",
    "DocNurseMesh": "Nurse",
    "EngineerMaleMesh": "Engineer",
    "FiremanMesh": "Firefighter",
    "GarbagemanBaseMesh": "Garbageman",
    "MehchanicBaseMesh": "Mechanic",
    "PoliceMaleMesh": "Police",
    "TrafficPoliceMaleMesh": "TrafficPolice",
    "ScientistMaleMesh": "ScientistMale",
    "ScientistFemaleMesh.001": "ScientistFemale_A",
    "ScientistFemaleMesh.002": "ScientistFemale_B",
    "SoldierMaleMesh": "Soldier",
    "MaleStudent": "StudentMale",
    "FemaleStudent": "StudentFemale",
    "TeacherMale": "TeacherMale_A",
    "TeacherMale.001": "TeacherMale_B",
    "TeacherFemale": "TeacherFemale",
}
CHILDREN = {"Child_A", "Child_B"}

# Rotations about armature-space X through each bone's head, applied in order on top of the
# file's own standing pose. The characters face -Y, so a negative angle swings a limb forwards.
POSES = {
    "Stand": [],
    "Walk": [("thigh.L", -24), ("shin.L", 8), ("thigh.R", 18), ("shin.R", 28),
             ("upper_arm.L", 18), ("upper_arm.R", -20), ("forearm.L", -8), ("forearm.R", -16)],
    "Sit": [("thigh.L", -88), ("thigh.R", -88), ("shin.L", 86), ("shin.R", 86),
            ("upper_arm.L", -14), ("upper_arm.R", -14), ("forearm.L", -48), ("forearm.R", -48)],
}

PALETTE = "ImphenziaPalette02-Albedo.png"


def save_palette():
    # In a Textures folder, where Unity looks for the textures of models here and in Rigged/
    image = next(i for i in bpy.data.images if i.name.startswith("ImphenziaPalette"))
    path = os.path.join(OUTPUT_DIR, "Textures", PALETTE)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    image.save(filepath=path)
    image.filepath_raw = path  # FBX files reference it by file name only


def fill_missing_weights(mesh_obj):
    """Accessories modelled without skin weights (holsters, glasses, badges) take the weights of the
    nearest skinned vertex, so they move with the body instead of floating when animated."""
    vertices = mesh_obj.data.vertices
    weighted = [v for v in vertices if any(g.weight > 0 for g in v.groups)]
    missing = [v for v in vertices if not any(g.weight > 0 for g in v.groups)]
    if not missing or not weighted:
        return
    tree = kdtree.KDTree(len(weighted))
    for i, v in enumerate(weighted):
        tree.insert(v.co, i)
    tree.balance()
    for v in missing:
        _, nearest, _ = tree.find(v.co)
        for g in weighted[nearest].groups:
            mesh_obj.vertex_groups[g.group].add([v.index], g.weight, "REPLACE")


def pose(rig, standing, rotations):
    """Back to the file's standing pose, then each rotation in turn, parents before children."""
    for bone in rig.pose.bones:
        bone.matrix_basis = standing[bone.name]
    bpy.context.view_layer.update()
    for bone_name, degrees in rotations:
        bone = rig.pose.bones[bone_name]
        head = bone.head.copy()
        turn = Matrix.Translation(head) @ Matrix.Rotation(math.radians(degrees), 4, "X") @ Matrix.Translation(-head)
        bone.matrix = turn @ bone.matrix
        bpy.context.view_layer.update()


def mask_material():
    mat = bpy.data.materials.get("smog_mask_#E8E6E0")
    if mat is None:
        mat = bpy.data.materials.new("smog_mask_#E8E6E0")
        mat.use_nodes = True
        mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.8, 0.79, 0.75, 1.0)
    return mat


def add_mask(bm, rig, world_verts):
    """A surgical smog mask over the mouth and nose, fitted to the face of the posed mesh."""
    head = rig.pose.bones["head"]
    mouth_z = head.head.z + 0.3 * (head.tail.z - head.head.z)
    face = [v for v in world_verts if abs(v.z - mouth_z) < 0.02 and abs(v.x - head.head.x) < 0.03]
    front_y = min(v.y for v in face) if face else head.head.y - 0.06
    result = bmesh.ops.create_cube(bm, size=1.0)
    for v in result["verts"]:
        # Width, depth, height in pack units, bowed so the sides wrap round the cheeks
        x, y, z = v.co.x * 0.095, v.co.y * 0.022, v.co.z * 0.055
        y += 12.0 * x * x
        v.co = Vector((head.head.x + x, front_y + 0.004 + y, mouth_z + z))
    for f in bm.faces:
        if all(v in set(result["verts"]) for v in f.verts):
            f.material_index = 1


def masked_rigged_object(name, mesh_obj, rig):
    """A copy of the skinned mesh with the smog mask added in the rest pose, skinned to the head bone."""
    obj = mesh_obj.copy()
    obj.data = mesh_obj.data.copy()
    obj.name = name
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.update()

    depsgraph = bpy.context.evaluated_depsgraph_get()
    rest = bpy.data.meshes.new_from_object(mesh_obj.evaluated_get(depsgraph))
    rest.transform(mesh_obj.matrix_world)
    world_verts = [v.co.copy() for v in rest.vertices]
    bpy.data.meshes.remove(rest)

    bm = bmesh.new()
    bm.from_mesh(obj.data)
    before = set(bm.verts)
    to_local = obj.matrix_world.inverted()
    add_mask(bm, rig, world_verts)
    head_group = obj.vertex_groups["head"].index
    deform = bm.verts.layers.deform.verify()
    for v in bm.verts:
        if v not in before:
            v.co = to_local @ v.co
            v[deform][head_group] = 1.0
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.materials.append(mask_material())
    return obj


def posed_object(name, mesh_obj, rig, masked):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    mesh = bpy.data.meshes.new_from_object(mesh_obj.evaluated_get(depsgraph))
    mesh.transform(mesh_obj.matrix_world)
    bm = bmesh.new()
    bm.from_mesh(mesh)
    if masked:
        add_mask(bm, rig, [v.co.copy() for v in bm.verts])
    # Origin on the floor, under the hips
    pelvis = rig.pose.bones["pelvis"].head
    floor = min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co -= Vector((pelvis.x, pelvis.y, floor))
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.clear()
    mesh.materials.append(mesh_obj.material_slots[0].material)
    if masked:
        mesh.materials.append(mask_material())
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def export_fbx(objects, path, object_types, bake_space_transform=True):
    for other in bpy.context.view_layer.objects:
        other.select_set(False)
    for obj in objects:
        obj.hide_set(False)
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types=object_types, global_scale=LIFE_SIZE,
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
        bake_space_transform=bake_space_transform, use_mesh_modifiers=True, mesh_smooth_type="FACE",
        use_armature_deform_only=True, add_leaf_bones=False, bake_anim=False, path_mode="STRIP",
    )


def main():
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    save_palette()
    exported = []
    for mesh_name, name in CHARACTERS.items():
        mesh_obj = bpy.data.objects[mesh_name]
        rig = mesh_obj.parent
        fill_missing_weights(mesh_obj)
        rig.location = (0, 0, 0)
        rig.rotation_euler = (0, 0, 0)  # the file turns them to face its camera; face -Y like the props
        bpy.context.view_layer.update()
        standing = {bone.name: bone.matrix_basis.copy() for bone in rig.pose.bones}

        # Rigged: in the T-pose rest position with unbaked bone axes, so Unity's Humanoid avatar
        # (and Mixamo animations retargeted through it) is calibrated correctly
        rig.data.pose_position = "REST"
        export_fbx([rig, mesh_obj], os.path.join(OUTPUT_DIR, "Rigged", name + ".fbx"), {"ARMATURE", "MESH"},
                   bake_space_transform=False)
        if name not in CHILDREN:
            masked = masked_rigged_object(name + "_Mask", mesh_obj, rig)
            export_fbx([rig, masked], os.path.join(OUTPUT_DIR, "Rigged", name + "_Mask.fbx"), {"ARMATURE", "MESH"},
                       bake_space_transform=False)
            bpy.data.objects.remove(masked)
        rig.data.pose_position = "POSE"
        # The legs are driven by IK and the arms are rotation-limited, which would undo the poses below
        for bone in rig.pose.bones:
            for constraint in bone.constraints:
                constraint.mute = True
        for pose_name, rotations in POSES.items():
            pose(rig, standing, rotations)
            for masked in ((False,) if name in CHILDREN else (False, True)):
                full_name = f"{name}_{pose_name}" + ("_Mask" if masked else "")
                obj = posed_object(full_name, mesh_obj, rig, masked)
                export_fbx([obj], os.path.join(OUTPUT_DIR, full_name + ".fbx"), {"MESH"})
                exported.append((obj, pose_name, masked))
        pose(rig, standing, [])
    print("EXPORTED", len(exported), "posed meshes and", len(CHARACTERS), "rigged characters")

    if PREVIEW:
        render_preview(exported)


def render_preview(exported):
    """Contact sheet: one row per pose (and the masked walk), to check poses before using them."""
    rows = {}
    for obj, pose_name, masked in exported:
        if not masked or pose_name == "Walk":
            rows.setdefault(pose_name + (" masked" if masked else ""), []).append(obj)
    shown = {obj for row in rows.values() for obj in row}
    for obj in bpy.data.objects:
        obj.hide_render = obj not in shown
    for r, (key, row) in enumerate(sorted(rows.items())):
        for c, obj in enumerate(row):
            obj.location = (c * 0.6, 0, -r * 1.3)
            obj.rotation_euler = (0, 0, math.radians(-25))
    scene = bpy.context.scene
    cam = bpy.data.objects.new("PreviewCam", bpy.data.cameras.new("PreviewCam"))
    scene.collection.objects.link(cam)
    cam.data.type = "ORTHO"
    width = max(len(r) for r in rows.values()) * 0.6
    cam.data.ortho_scale = max(width, len(rows) * 1.3 * 16 / 9) + 0.4
    cam.location = (width / 2 - 0.3, -10, -(len(rows) - 1) * 1.3 / 2 + 0.5)
    cam.rotation_euler = (math.radians(90), 0, 0)
    scene.camera = cam
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "TEXTURE"
    scene.render.resolution_x, scene.render.resolution_y = 2400, 1350
    scene.render.filepath = PREVIEW
    bpy.ops.render.render(write_still=True)


main()
