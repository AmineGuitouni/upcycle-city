"""The player character: a stylised, down-on-their-luck city dweller, built from simple smooth shapes.

Proportions follow the character-creator reference: a big tall egg head (about two thirds of the shoulder
width), big bulging eyes with tiny pupils and heavy lids, no nose, a short thin neck, narrow shoulders and
long slim limbs. The clothes tell the story: an oversized patched coat over a T-shirt, a scarf, baggy
patched trousers with frayed hems, fingerless gloves and mismatched worn shoes.

Every part is built around one shared head, so bodies, faces, facial hair and hair styles stack in Unity
without offsets. Modelled in metres, front towards -Y, feet on z = 0 (mesh_kit conventions).
Material names carry a role prefix that Unity recolours: skin_ hair_ iris_ top_ bottom_.
"""

import math

import bmesh
from mathutils import Matrix, Vector

import mesh_kit as mk

# ---------------------------------------------------------------- style

# False: smooth stylised shapes (the chosen look). True: one fused, faceted low-poly mesh per part
# (export_character_parts.py then remeshes and decimates bodies, hair and beards).
LOW_POLY = False
SMOOTH = not LOW_POLY
HEAD_SEG, HEAD_RINGS = (18, 12) if LOW_POLY else (36, 24)      # the head's facet grid
SHELL_SEG, SHELL_RINGS = (18, 12) if LOW_POLY else (40, 28)    # hair shells follow the head
EYE_SEG, EYE_RINGS = (14, 10) if LOW_POLY else (24, 16)

# ---------------------------------------------------------------- palette (placeholders for recolourable roles)

SKIN = "#C98B62"
HAIR = "#4A3426"
COAT = "#6B5A3E"        # top_: the oversized coat, recoloured by "Outfit Colour"
TROUSERS = "#4A4C55"    # bottom_
SHIRT = "#B9B09A"       # a once-white T-shirt
SCARF = "#8E3B2F"
PATCH_A = "#7B8F5A"
PATCH_B = "#A8743C"
GLOVE = "#3A3F44"
SHOE_L = "#5A4632"
SHOE_R = "#6E6A64"
SOLE = "#2A2626"
HAT = "#4E5D4A"
IRIS = "#5B7A3A"
PUPIL = "#141212"
EYEWHITE = "#F2EFE6"
MOUTH = "#5E2C24"


def srgb(hex_color):
    h = hex_color.lstrip("#")
    c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    return tuple(v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in c)


def paint(b, hex_color, roughness, prefix):
    return b.mat(f"{prefix}_{hex_color}", srgb(hex_color), roughness)


# ---------------------------------------------------------------- the shared head

HEAD_CENTRE = Vector((0.0, -0.004, 1.615))
HEAD_R = Vector((0.138, 0.13, 0.185))
HEAD_TAPER = 0.34      # how much narrower the chin is than the cranium
TAPER_POWER = 1.6
K = HEAD_R.x / 0.118   # face and hair details were tuned on a 0.118 m wide head; they scale with it


def taper_at(z_rel, rz, taper=HEAD_TAPER):
    return 1.0 - taper * (max(0.0, -z_rel) / rz) ** TAPER_POWER if z_rel < 0 else 1.0


def egg(b, centre, radii, mat, taper=HEAD_TAPER, seg=28, rings=18, smooth=True):
    """Ellipsoid whose lower half narrows towards the chin, like the head."""
    bm = b.ellipsoid(centre, radii, mat, seg=seg, rings=rings, smooth=smooth)
    for v in bm.verts:
        rel = v.co - Vector(centre)
        f = taper_at(rel.z, radii[2], taper)
        v.co.x = centre[0] + rel.x * f
        v.co.y = centre[1] + rel.y * f
    return bm


def cut(bm, co, normal):
    """Slice bm with a plane and keep the side the normal points to: a clean, straight edge."""
    geom = list(bm.verts) + list(bm.edges) + list(bm.faces)
    bmesh.ops.bisect_plane(bm, geom=geom, dist=1e-6, plane_co=Vector(co), plane_no=-Vector(normal).normalized(),
                           clear_outer=True)


class Head:
    """Places features on the egg head, in metres relative to its centre."""

    centre = HEAD_CENTRE
    r = HEAD_R

    def surface(self, x, z):
        f = taper_at(z, self.r.z)
        rx, ry = self.r.x * f, self.r.y * f
        k = 1 - (x / rx) ** 2 - (z / self.r.z) ** 2
        y = -ry * math.sqrt(max(k, 0.0))
        n = Vector((x / rx ** 2, y / ry ** 2, z / self.r.z ** 2)).normalized()
        return Vector((x, y, z)), n

    def on(self, x, z, out=0.0):
        s, n = self.surface(x, z)
        return self.centre + s + n * out

    def frame(self, b, x, z, out, radii, mat, seg=18, rings=12, smooth=SMOOTH):
        """Ellipsoid whose thin (y) axis follows the head normal at (x, z). Returns (bm, centre, up)."""
        s, n = self.surface(x, z)
        yax = -n
        zax = (Vector((0, 0, 1)) - yax * yax.z).normalized()
        xax = yax.cross(zax).normalized()
        rot = Matrix((xax, yax, zax)).transposed().to_4x4()
        centre = self.centre + s + n * out
        bm = b.ellipsoid(None, radii, mat, seg=seg, rings=rings, matrix=Matrix.Translation(centre) @ rot, smooth=smooth)
        return bm, centre, zax

    def shell(self, b, mat, grow, seg=SHELL_SEG, rings=SHELL_RINGS, smooth=SMOOTH, scale=(1.0, 1.0, 1.0)):
        radii = (self.r.x * scale[0] + grow, self.r.y * scale[1] + grow, self.r.z * scale[2] + grow)
        return egg(b, self.centre, radii, mat, seg=seg, rings=rings, smooth=smooth)

    def hair_shell(self, b, mat, grow, front, back, **kw):
        """Hair over the skull above a hairline that is `front` (in head radii) high at the forehead and
        `back` at the nape."""
        bm = self.shell(b, mat, grow, **kw)
        ry, rz = self.r.y, self.r.z
        p0 = self.centre + Vector((0, -ry, front * rz))
        p1 = self.centre + Vector((0, ry, back * rz))
        along = p1 - p0
        normal = Vector((0, -along.z, along.y)).normalized()
        if normal.z < 0:
            normal = -normal
        cut(bm, p0, normal)
        return bm


HEAD = Head()


# ---------------------------------------------------------------- body

COAT_SECTIONS = [(0.54, .232, .158), (0.72, .222, .152), (0.92, .208, .145), (1.12, .204, .138),
                 (1.28, .2, .13), (1.36, .186, .116), (1.415, .128, .086), (1.445, .08, .068)]
COAT_TOP, COAT_HEM = 1.445, 0.54


def coat_radii(z, sections):
    for (z0, rx0, ry0), (z1, rx1, ry1) in zip(sections, sections[1:]):
        if z0 <= z <= z1:
            t = (z - z0) / (z1 - z0)
            return rx0 + (rx1 - rx0) * t, ry0 + (ry1 - ry0) * t
    return sections[-1][1], sections[-1][2]


def on_coat(b, sections, angle_deg, z, size, mat, out=0.007, tilt=0.0):
    """A flat patch or pocket flap lying on the coat at an angle round the body (-90 is the front)."""
    rx, ry = coat_radii(z, sections)
    a = math.radians(angle_deg)
    n = Vector((math.cos(a) / rx, math.sin(a) / ry, 0)).normalized()
    p = Vector((rx * math.cos(a), ry * math.sin(a), z)) + n * out
    yaw = math.degrees(math.atan2(n.y, n.x)) + 90
    b.box(p, size, mat, bevel=min(size) * 0.4, rot=(0, tilt, yaw))


def _limb(b, p0, p1, r0, r1, mat, seg=14):
    b.capsule(p0, p1, r0, r1, mat, seg=seg, cap_steps=3)


def build_body(female):
    b = mk.Builder("body_female" if female else "body_male", scale=1.0)
    m_skin = paint(b, SKIN, 0.55, "skin")
    m_coat = paint(b, COAT, 0.85, "top")
    m_trousers = paint(b, TROUSERS, 0.9, "bottom")
    m_shirt = paint(b, SHIRT, 0.85, "cloth")
    m_scarf = paint(b, SCARF, 0.9, "scarf")
    m_patch_a = paint(b, PATCH_A, 0.9, "patch")
    m_patch_b = paint(b, PATCH_B, 0.9, "patch")
    m_glove = paint(b, GLOVE, 0.9, "glove")
    m_shoe_l = paint(b, SHOE_L, 0.7, "shoe")
    m_shoe_r = paint(b, SHOE_R, 0.7, "shoe")
    m_sole = paint(b, SOLE, 0.8, "sole")

    hip_x = 0.092 if female else 0.086
    sh_x = 0.15 if female else 0.163
    hip_z, knee_z, ankle_z = 0.84, 0.47, 0.095

    # ---- legs: baggy trousers, a knee patch, frayed hems, mismatched shoes
    for side in (-1, 1):
        hip = Vector((side * hip_x, 0.0, hip_z))
        knee = Vector((side * (hip_x + 0.006), -0.008, knee_z))
        ankle = Vector((side * (hip_x + 0.004), 0.0, ankle_z))
        _limb(b, hip, knee, 0.076, 0.065, m_trousers)
        _limb(b, knee, ankle, 0.065, 0.06, m_trousers)
        b.loft([(ankle.z - 0.012, 0.068, 0.068, ankle.x, ankle.y), (ankle.z + 0.03, 0.064, 0.064, ankle.x, ankle.y)],
               m_trousers, seg=9, caps=False, sharp=0.0)  # frayed cuff
        shoe_mat = m_shoe_l if side < 0 else m_shoe_r
        x = side * (hip_x + 0.004)
        b.box((x, -0.05, 0.06), (0.12, 0.285, 0.105), shoe_mat, bevel=0.05)
        b.box((x, -0.05, 0.014), (0.112, 0.275, 0.026), m_sole, bevel=0.012)
    b.box((-(hip_x + 0.006), -0.07, knee_z + 0.02), (0.07, 0.012, 0.075), m_patch_b, bevel=0.006, rot=(0, 0, 8))

    # ---- pelvis and the T-shirt torso
    pelvis = (0.17, 0.104) if female else (0.162, 0.1)
    b.loft([(0.76, 0.0, 0.0), (0.775, pelvis[0] * 0.75, pelvis[1] * 0.75), (0.82, pelvis[0], pelvis[1]),
            (0.92, pelvis[0] * 0.98, pelvis[1] * 0.98)], m_trousers, seg=22, exponent=2.2)
    if female:
        torso = [(0.9, .158, .098), (1.04, .136, .09), (1.17, .144, .1), (1.25, .15, .108), (1.33, .15, .096),
                 (1.39, .114, .07), (1.41, .05, .045), (1.415, 0, 0)]
    else:
        torso = [(0.9, .152, .096), (1.04, .15, .096), (1.2, .158, .1), (1.32, .162, .098), (1.38, .134, .08),
                 (1.405, .06, .05), (1.415, 0, 0)]
    b.loft(torso, m_shirt, seg=24, exponent=2.2)

    # ---- the oversized coat: open at the front, hanging to mid-thigh, with real thickness
    coat = COAT_SECTIONS
    if female:
        coat = [(z, rx - (0.012 if 0.9 < z < 1.25 else 0.006), ry) for z, rx, ry in coat]
    bm = b.loft(coat, m_coat, seg=32, exponent=2.15, caps=False)
    # The opening: |x| < w(z) on the front, w growing from the collar to the hem; cut along both
    # edge planes first so the edges are straight, then drop what lies between them
    w_top, w_hem = 0.03, 0.115
    slope = (w_hem - w_top) / (COAT_TOP - COAT_HEM)
    for side in (-1, 1):
        cut_geom = list(bm.verts) + list(bm.edges) + list(bm.faces)
        bmesh.ops.bisect_plane(bm, geom=cut_geom, dist=1e-6, plane_co=Vector((side * w_top, 0, COAT_TOP)),
                               plane_no=Vector((side, 0, slope)).normalized())
    drop = []
    for f in bm.faces:
        c = f.calc_center_median()
        w = w_top + slope * (COAT_TOP - c.z)
        if c.y < 0 and abs(c.x) < w and c.z < COAT_TOP - 0.02:
            drop.append(f)
    bmesh.ops.delete(bm, geom=drop, context="FACES")
    bmesh.ops.solidify(bm, geom=list(bm.faces), thickness=0.012)
    on_coat(b, coat, -138, 0.74, (0.1, 0.012, 0.1), m_coat, out=0.009, tilt=3)    # patch pockets
    on_coat(b, coat, -42, 0.74, (0.1, 0.012, 0.1), m_coat, out=0.009, tilt=-2)
    on_coat(b, coat, -44, 0.6, (0.07, 0.012, 0.075), m_patch_a, out=0.016, tilt=-8)  # patches
    on_coat(b, coat, 160, 1.1, (0.08, 0.012, 0.09), m_patch_b, tilt=10)

    # ---- scarf: a loop round the neck and a tail down the front
    b.loft([(1.385, 0.1, 0.09), (1.41, 0.1, 0.09), (1.445, 0.086, 0.078), (1.465, 0.07, 0.064)], m_scarf, seg=18,
           caps=False)
    b.box((0.055, -0.098, 1.28), (0.07, 0.03, 0.22), m_scarf, bevel=0.012, rot=(-6, 0, -5))

    # ---- arms in coat sleeves, big mitten hands in fingerless gloves
    for side in (-1, 1):
        shoulder = Vector((side * sh_x, 0.004, 1.335))
        elbow = Vector((side * (sh_x + 0.05), 0.012, 1.07))
        wrist = Vector((side * (sh_x + 0.066), -0.012, 0.83))
        _limb(b, shoulder, elbow, 0.058, 0.053, m_coat)
        _limb(b, elbow, wrist, 0.053, 0.05, m_coat)
        b.loft([(-0.012, 0.057, 0.057), (0.024, 0.055, 0.055)], m_coat, seg=9, caps=False, sharp=0.0,
               matrix=Matrix.Translation(wrist) @ mk._orient(wrist - elbow))   # frayed cuff
        d = (wrist - elbow).normalized()
        orient = mk._orient(d)
        b.ellipsoid(None, (0.044, 0.032, 0.05), m_glove, matrix=Matrix.Translation(wrist + d * 0.045) @ orient,
                    seg=14, rings=8)
        b.ellipsoid(None, (0.042, 0.031, 0.056), m_skin, matrix=Matrix.Translation(wrist + d * 0.1) @ orient,
                    seg=14, rings=8)
        b.ellipsoid(None, (0.016, 0.018, 0.03), m_skin,   # thumb
                    matrix=Matrix.Translation(wrist + d * 0.075 + Vector((-side * 0.03, -0.02, 0))) @ orient,
                    seg=10, rings=6)
        if side > 0:  # elbow patch on the right sleeve
            b.box(elbow + Vector((0.004, 0.054, 0.0)), (0.06, 0.012, 0.07), m_patch_b, bevel=0.006)

    # ---- neck up into the head (the head itself is build_head: clean regular facets, not remeshed)
    _limb(b, (0, 0.0, 1.38), (0, 0.0, 1.52), 0.042, 0.04, m_skin)
    return b


def build_head(name):
    """The low-poly egg head and ears (no nose: the reference face is eyes and a small mouth)."""
    b = mk.Builder(name, scale=1.0)
    m_skin = paint(b, SKIN, 0.55, "skin")
    egg(b, HEAD.centre, tuple(HEAD.r), m_skin, seg=HEAD_SEG, rings=HEAD_RINGS, smooth=SMOOTH)
    for side in (-1, 1):
        ez = -0.02 * K
        ear = HEAD.centre + Vector((side * HEAD.r.x * taper_at(ez, HEAD.r.z) * 0.95, 0.012, ez))
        b.ellipsoid(ear, (0.018 * K, 0.028 * K, 0.04 * K), m_skin, seg=7 if LOW_POLY else 10,
                    rings=5 if LOW_POLY else 6, smooth=SMOOTH)
    return b


# ---------------------------------------------------------------- faces (eyes, brows, mouth)

FACE_STYLES = ["tired", "wide", "dot", "happy", "squint"]
FACE_NAMES = {"tired": "Tired", "wide": "Wide", "dot": "Dot", "happy": "Happy", "squint": "Squint"}


def build_face(style):
    b = mk.Builder(f"face_{style}", scale=1.0)
    m_white = paint(b, EYEWHITE, 0.3, "eyewhite")
    m_iris = paint(b, IRIS, 0.3, "iris")
    m_pupil = paint(b, PUPIL, 0.25, "pupil")
    m_skin = paint(b, SKIN, 0.55, "skin")
    m_hair = paint(b, HAIR, 0.8, "hair")
    m_mouth = paint(b, MOUTH, 0.5, "mouth")
    h = HEAD

    ex, ez = 0.05 * K, 0.022 * K
    eye_r = (0.029 * K, 0.018 * K, 0.034 * K)
    for side in (-1, 1):
        x = side * ex
        if style in ("tired", "wide", "squint"):
            sink = -0.004 * K
            h.frame(b, x, ez, sink, eye_r, m_white, seg=EYE_SEG, rings=EYE_RINGS)
            front = eye_r[1] + sink
            gaze = -side * 0.003 * K  # pupils a touch towards the nose, like the reference
            look = -0.002 * K
            h.frame(b, x + gaze, ez + look, front - 0.0016, (0.0115 * K, 0.0035 * K, 0.0125 * K), m_iris, seg=16,
                    rings=8)
            h.frame(b, x + gaze, ez + look, front + 0.0004, (0.0058 * K, 0.0022 * K, 0.0062 * K), m_pupil, seg=12,
                    rings=6)
            h.frame(b, x + gaze + 0.004 * K, ez + look + 0.004 * K, front + 0.0018,
                    (0.0022 * K, 0.0012 * K, 0.0022 * K), m_white, seg=8, rings=4)
            # Lids: (which, edge) where edge is how far from the eye's middle the lid stops, in eye radii
            lids = {"tired": [("top", 0.18)], "squint": [("top", -0.02), ("bottom", 0.42)]}.get(style, [])
            for which, edge in lids:
                lid_r = (eye_r[0] + 0.0022, eye_r[1] + 0.0026, eye_r[2] + 0.0022)
                bm, c, up = h.frame(b, x, ez, sink, lid_r, m_skin, seg=EYE_SEG, rings=EYE_RINGS)
                sign = 1 if which == "top" else -1
                cut(bm, c + up * (sign * edge * lid_r[2]), up * sign)
        elif style == "dot":
            h.frame(b, x, ez, -0.002, (0.011 * K, 0.006 * K, 0.015 * K), m_pupil, seg=12, rings=8)
        elif style == "happy":
            pts = [h.on(x + dx * K, ez + dz * K, 0.0012) for dx, dz in
                   ((-0.022, -0.004), (-0.012, 0.008), (0.0, 0.012), (0.012, 0.008), (0.022, -0.004))]
            b.path(pts, 0.0035 * K, m_pupil, seg=8)

        # Brows: thin, the inner end lifted on tired eyes (a worn-out, kind look)
        lift = 0.01 * K if style == "tired" else 0.0
        bz = ez + eye_r[2] + 0.02 * K
        brow = [h.on(side * bx * K, bz + dz, 0.003) for bx, dz in ((0.024, lift), (0.05, 0.006 * K),
                                                                   (0.076, -0.004 * K))]
        b.path(brow, 0.0042 * K, m_hair, seg=8)

    # A small, slightly lopsided mouth, drawn proud of stubble
    mouth = [h.on(dx * K, (-0.082 + dz) * K, 0.0055) for dx, dz in ((-0.02, 0.0), (-0.006, -0.0025),
                                                                    (0.01, -0.002), (0.022, 0.003))]
    b.path(mouth, 0.0036 * K, m_mouth, seg=8)
    return b


# ---------------------------------------------------------------- facial hair

BEARD_STYLES = ["none", "stubble", "beard", "moustache"]
BEARD_NAMES = {"none": "None", "stubble": "Stubble", "beard": "Scruffy Beard", "moustache": "Moustache"}


def _moustache(b, m):
    pts = [HEAD.on(dx * K, (-0.066 + dz) * K, 0.007 * K) for dx, dz in
           ((-0.038, -0.013), (-0.02, -0.001), (0.0, 0.002), (0.02, -0.001), (0.038, -0.013))]
    b.path(pts, 0.0095 * K, m, seg=8)


def build_beard(style):
    b = mk.Builder(f"beard_{style}", scale=1.0)
    # stubble_ is tinted in Unity between skin and hair colour, so it reads as shadow, not a mask
    m = paint(b, HAIR, 0.85, "stubble" if style == "stubble" else "hair")
    h = HEAD
    c, r = h.centre, h.r
    if style in ("stubble", "beard"):
        grow = 0.0035 if style == "stubble" else 0.016 * K
        bm = h.shell(b, m, grow, seg=44, rings=30)
        top = -0.46 if style == "stubble" else -0.38
        cut(bm, c + Vector((0, 0, top * r.z)), (0, 0, -1))           # below the cheekbones
        cut(bm, c + Vector((0, 0.25 * r.y, 0)), (0, -1, 0))          # not round the back of the neck
        if style == "beard":
            # clear the mouth: drop faces in a small oval round it
            m_c = h.on(0, -0.082 * K)
            drop = [f for f in bm.faces if ((f.calc_center_median() - m_c).x / (0.034 * K)) ** 2 +
                    ((f.calc_center_median() - m_c).z / (0.016 * K)) ** 2 < 1 and
                    f.calc_center_median().y < c.y]
            bmesh.ops.delete(bm, geom=drop, context="FACES")
            b.tube(c + Vector((0, -0.08 * K, -0.152 * K)), c + Vector((0.004, -0.088 * K, -0.205 * K)), 0.05 * K, m,
                   r1=0.008, seg=8)
    if style in ("beard", "moustache"):
        _moustache(b, m)
    return b


# ---------------------------------------------------------------- hair

HAIR_STYLES = ["none", "buzz", "messy", "long", "bun", "ponytail", "curly", "afro", "beanie"]
HAIR_NAMES = {"none": "None", "buzz": "Buzz Cut", "messy": "Messy", "long": "Long", "bun": "Bun",
              "ponytail": "Ponytail", "curly": "Curly", "afro": "Afro", "beanie": "Beanie"}


def _locks(b, m, specs):
    """Soft, low-poly locks: (angle round the head, height in head radii, length, droop) each."""
    h = HEAD
    for az, el, length, droop in specs:
        a, e = math.radians(az), math.radians(el)
        d = Vector((math.cos(a) * math.cos(e), math.sin(a) * math.cos(e), math.sin(e)))
        root = h.centre + Vector((d.x * h.r.x, d.y * h.r.y, d.z * h.r.z)) * 1.03
        out = (d * 0.55 + Vector((0, 0, -droop))).normalized()
        b.tube(root, root + out * length * K, 0.026 * K, m, r1=0.006 * K, seg=5 if LOW_POLY else 7, smooth=SMOOTH)


def _curls(b, m, centre, radii, count, size, sink=0.86):
    golden = math.pi * (3 - math.sqrt(5))
    for i in range(count):
        z = 1 - (i + 0.5) / count * 1.25
        r = math.sqrt(max(0.0, 1 - z * z))
        a = golden * i
        d = Vector((math.cos(a) * r, math.sin(a) * r, z))
        if d.y < -0.35 and d.z < 0.55:
            continue
        p = centre + Vector((d.x * radii[0], d.y * radii[1], d.z * radii[2])) * sink
        b.ellipsoid(p, (size,) * 3, m, seg=10, rings=6)


def build_hair(style):
    b = mk.Builder(f"hair_{style}", scale=1.0)
    m = paint(b, HAIR, 0.8, "hair")
    h = HEAD
    c = h.centre

    if style == "buzz":
        h.hair_shell(b, m, 0.005, 0.42, -0.25)
    elif style == "messy":
        h.hair_shell(b, m, 0.013, 0.36, -0.3)
        # a soft, uneven fringe over the forehead, locks over the ears, a cowlick at the crown
        for dx, dz, tilt, w in ((-0.06, 0.1, 20, 0.05), (-0.015, 0.115, -8, 0.052), (0.035, 0.105, -24, 0.048),
                                (0.075, 0.085, -34, 0.036)):
            p = h.on(dx * K, dz * K, 0.012)
            b.ellipsoid(p, (w * K, 0.02 * K, 0.026 * K), m, seg=12, rings=8, rot=(-25, 0, tilt))
        _locks(b, m, [(-20, 10, 0.045, 1.2), (200, 10, 0.045, 1.2), (60, 20, 0.05, 1.0), (120, 20, 0.05, 1.0),
                      (90, 0, 0.05, 1.2)])
    elif style == "long":
        h.hair_shell(b, m, 0.012, 0.4, -0.15)
        # straggly hair down to the shoulders, behind the ears
        z = c.z
        b.loft([(z - 0.02 * K, 0.124 * K, 0.07 * K, 0, c.y + 0.058 * K),
                (z - 0.12 * K, 0.13 * K, 0.068 * K, 0, c.y + 0.064 * K),
                (z - 0.24 * K, 0.122 * K, 0.06 * K, 0, c.y + 0.074 * K),
                (z - 0.27 * K, 0.1 * K, 0.045 * K, 0, c.y + 0.08 * K)], m, seg=13, sharp=0.0)
    elif style == "bun":
        h.hair_shell(b, m, 0.01, 0.42, -0.25)
        b.ellipsoid(c + Vector((0, 0.07, 0.15)) * K, (0.055 * K, 0.055 * K, 0.05 * K), m, seg=14, rings=8)
    elif style == "ponytail":
        h.hair_shell(b, m, 0.01, 0.42, -0.25)
        b.capsule(c + Vector((0, 0.1, 0.06)) * K, c + Vector((0, 0.15, -0.16)) * K, 0.042 * K, 0.02 * K, m)
    elif style == "curly":
        h.hair_shell(b, m, 0.012, 0.3, -0.25, scale=(1.05, 1.05, 1.0))
        _curls(b, m, c + Vector((0, 0.025, 0.035)) * K, tuple(v * K for v in (0.13, 0.13, 0.15)), 46, 0.03 * K)
    elif style == "afro":
        cc, r = c + Vector((0, 0.04, 0.07)) * K, tuple(v * K for v in (0.165, 0.16, 0.17))
        b.ellipsoid(cc, tuple(v * 0.93 for v in r), m, seg=28, rings=16)
        _curls(b, m, cc, r, 60, 0.036 * K)
    elif style == "beanie":
        m_hat = paint(b, HAT, 0.95, "hat")
        h.hair_shell(b, m_hat, 0.022, 0.25, 0.05)
        # folded brim, tipped back a little so the hat sits slouchy on the head
        b.loft([(0.0, h.r.x + 0.03, h.r.y + 0.03), (0.05 * K, h.r.x + 0.027, h.r.y + 0.027)], m_hat, seg=SHELL_SEG,
               caps=False, sharp=0.0 if LOW_POLY else None, matrix=Matrix.Translation(c) @ Matrix.Rotation(math.radians(-8), 4, "X"))
        # hair poking out at the sides and back
        _locks(b, m, [(az, -4, 0.035, 1.3) for az in (-20, 0, 20, 160, 180, 200, 60, 90, 120)])
    return b


# ---------------------------------------------------------------- first-person arms

FP_POSES = ["open", "grip"]


def _frame_from_fingers(f, up_hint, right):
    """Hand axes from the finger direction: f along the hand, u out of the back of the hand, w towards the
    thumb. A right hand has w = u x f, a left hand the mirror."""
    f = f.normalized()
    u = (up_hint - f * up_hint.dot(f)).normalized()
    w = u.cross(f) if right else f.cross(u)
    return f, u, w.normalized()


def _frame_from_thumb(w, up_hint, right):
    """Hand axes from the thumb direction, e.g. along a held handle (see _frame_from_fingers)."""
    w = w.normalized()
    u = (up_hint - w * up_hint.dot(w)).normalized()
    f = w.cross(u) if right else u.cross(w)
    return f.normalized(), u, w


def build_fp_arm(side, pose):
    """A first-person forearm and hand in camera space: origin at the eye, looking down -Y (Unity +Z),
    +Z up. side is "right" or "left" (the player's), pose is "open" or "grip".

    The hand has a fingerless glove over the palm, bare fingers with two joints and a thumb.
    "open" is a relaxed hand turned so its back faces the eye. "grip" is a pistol grip, like holding a
    litter picker: the fist hangs from the wrist and the handle runs through it, forward and down along
    the thumb. Empties mark the handle: ToolSocket (middle of the fist), ToolSocketForward (along the
    handle) and ToolSocketUp, so Unity can line any tool up with the fist.
    """
    b = mk.Builder(f"fp_arm_{side}_{pose}", scale=1.0)
    m_skin = paint(b, SKIN, 0.55, "skin")
    m_coat = paint(b, COAT, 0.85, "top")
    m_glove = paint(b, GLOVE, 0.9, "glove")
    right = side == "right"
    sx = -1.0 if right else 1.0   # Blender +X is the player's left once in Unity
    grip = pose == "grip"

    # the forearm sits where it reads best in first person: in from the bottom corner, under the hand
    elbow = Vector((sx * 0.27, -0.1, -0.4))
    wrist = Vector((sx * 0.185, -0.47, -0.235))
    if grip:
        # Handshake grip round a vertical handle, like a litter picker's pistol grip: the back of the hand
        # faces out, the thumb is on top, and the shaft leaves the top of the fist, forward and a little down
        f, u, w = _frame_from_fingers(Vector((-sx * 0.25, -1.0, 0.05)), Vector((sx * 1.0, 0.0, 0.35)), right)
    else:
        # Fingers point forward, in and a little up, and the back of the hand turns towards the eye:
        # fingers pointing straight away from the camera would stack on top of each other in perspective
        f, u, w = _frame_from_fingers(Vector((-sx * 0.55, -0.78, 0.22)), Vector((sx * 0.25, 0.55, 0.85)), right)

    _limb(b, elbow, wrist, 0.05, 0.043, m_coat, seg=20)
    b.loft([(-0.012, 0.0475, 0.0475), (0.018, 0.0465, 0.0465)], m_coat, seg=20, caps=False,
           matrix=Matrix.Translation(wrist) @ mk._orient(wrist - elbow))    # rolled cuff

    def at(a, along, c):
        return wrist + w * a + f * along + u * c

    # wrist and palm in the fingerless glove
    _limb(b, at(0, -0.01, 0), at(0, 0.03, 0), 0.034, 0.036, m_glove, seg=14)
    palm_m = Matrix((w, f, u)).transposed().to_4x4()
    b.ellipsoid(None, (0.046, 0.06, 0.021), m_glove, matrix=Matrix.Translation(at(0.0, 0.07, 0.0)) @ palm_m,
                seg=18, rings=12)

    # fingers: index to little finger, two segments each, bent towards the palm (-u)
    fingers = [(0.03, 1.0), (0.01, 1.07), (-0.01, 1.0), (-0.029, 0.82)]
    bends = (math.radians(80), math.radians(100)) if grip else (math.radians(12), math.radians(16))
    for a, length in fingers:
        root = at(a, 0.112, 0.004)
        splay = (a / 0.03) * (0.02 if grip else 0.12)
        d1 = (f * math.cos(bends[0]) - u * math.sin(bends[0]) + w * splay).normalized()
        j1 = root + d1 * 0.037 * length
        a2 = bends[0] + bends[1]
        d2 = (f * math.cos(a2) - u * math.sin(a2) + w * splay * 0.5).normalized()
        b.capsule(root, j1, 0.0118, 0.011, m_skin, seg=10, cap_steps=3)
        b.capsule(j1, j1 + d2 * 0.03 * length, 0.011, 0.0098, m_skin, seg=10, cap_steps=3)

    # thumb: out to the side when open, wrapped over the curled fingers when gripping
    t_root = at(0.04, 0.035, -0.006)
    if grip:
        t1 = t_root + (f * 0.55 - u * 0.75 + w * 0.2).normalized() * 0.036
        t2 = t1 + (f * 0.35 - u * 0.3 - w * 0.9).normalized() * 0.03
    else:
        t1 = t_root + (f * 0.75 + w * 0.55 - u * 0.25).normalized() * 0.03
        t2 = t1 + (f * 0.9 + w * 0.25 - u * 0.2).normalized() * 0.025
    b.capsule(t_root, t1, 0.0145, 0.0125, m_glove, seg=10, cap_steps=3)
    b.capsule(t1, t2, 0.0122, 0.0105, m_skin, seg=10, cap_steps=3)

    if grip:
        socket = at(0.035, 0.085, -0.022)                       # top of the fist, over the thumb
        down = math.radians(18)
        tool_dir = (f * math.cos(down) - w * math.sin(down)).normalized()
        return b, socket, socket + tool_dir * 0.1, socket + w * 0.1
    socket = wrist + f * 0.11 - u * 0.03
    return b, socket, socket + f * 0.1, socket + u * 0.1
