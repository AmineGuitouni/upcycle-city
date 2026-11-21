"""Avenue Habib Bourguiba landmarks for the Bourguiba Avenue map, built with the Upcycle City mesh toolkit.

clocktower           the Clock Tower of Place du 14 Janvier 2011: a square obelisk in two stages clad in
                     rust-red openwork panels with eight-point rosettes, white clock faces and a gold
                     pyramid cap, on a stepped stone plinth (about 37 m tall)
clocktower_fountain  the round fountain basin around it with its rings of jet nozzles (water is added in Unity)

Modelled in metres, front towards -Y, origin on the floor at the centre.
"""

import math

from mathutils import Matrix, Vector

import mesh_kit as mk

# ---------------------------------------------------------------- palette

RUST = (0.34, 0.10, 0.055)
RUST_DARK = (0.13, 0.045, 0.03)
STONE = (0.80, 0.76, 0.68)
STONE_DARK = (0.62, 0.58, 0.52)
GOLD = (0.86, 0.62, 0.20)
CLOCK_WHITE = (0.94, 0.93, 0.89)
CLOCK_RED = (0.58, 0.09, 0.06)
IRON = (0.07, 0.07, 0.08)
GLASS = (0.32, 0.46, 0.50)

# ---------------------------------------------------------------- tower proportions (metres)

PLINTH_H = 1.7
LOWER_TOP = 14.5          # top of the wide tapered stage
LOWER_BASE_W = 5.4
LOWER_TOP_W = 3.3
UPPER_BASE_W = 2.75
UPPER_TOP_W = 2.55
UPPER_TOP = 30.5
CLOCK_W = 3.0
CLOCK_TOP = 33.6
CAP_TOP = 37.4


def _star(radius, inner=0.5, points=8, rot=0.0):
    out = []
    for i in range(points * 2):
        r = radius if i % 2 == 0 else radius * inner
        a = rot + math.pi * i / points
        out.append((math.cos(a) * r, math.sin(a) * r))
    return out


def _circle(radius, seg=24):
    return [(math.cos(2 * math.pi * i / seg) * radius, math.sin(2 * math.pi * i / seg) * radius) for i in range(seg)]


def _face_frames(z0, z1, w0, w1):
    """For each of the four sides of a square frustum: a function (u, v) -> placement matrix on that face.

    u runs -1..1 across the face, v 0..1 up it; the matrix's local Z is the outward normal."""
    h0, h1, height = w0 / 2, w1 / 2, z1 - z0
    up = Vector((0.0, h0 - h1, height)).normalized()
    across = Vector((1.0, 0.0, 0.0))
    normal = across.cross(up).normalized()      # points -Y, tilted up by the taper
    frames = []
    for side in range(4):
        spin = Matrix.Rotation(side * math.pi / 2, 4, "Z")

        def frame(u, v, spin=spin):
            half = h0 + (h1 - h0) * v
            local = Vector((u * half, -half, z0 + height * v))
            basis = Matrix((across, up, normal)).transposed().to_4x4()
            return spin @ Matrix.Translation(local) @ basis
        frames.append(frame)
    return frames


def _lattice(b, z0, z1, w0, w1, cols, rows, rust, dark):
    """Openwork cladding: a dark core with a grid of rust rosettes and bars over every face."""
    mid = (z0 + z1) / 2
    b.box((0, 0, mid), (w0 - 0.06, w0 - 0.06, z1 - z0), dark, taper=((w1 - 0.06) / (w0 - 0.06),) * 2)
    for frame in _face_frames(z0, z1, w0, w1):
        for r in range(rows):
            v = (r + 0.5) / rows
            local_w = w0 + (w1 - w0) * v
            cell = local_w / cols
            for c in range(cols):
                u = -1 + (c + 0.5) * 2 / cols
                m = frame(u, v)
                b.prism(_star(cell * 0.46, 0.62, 8, math.pi / 8), 0.05, rust, m)
        # bars between the rosette rows, and the side frames
        for r in range(rows + 1):
            v = r / rows
            local_w = w0 + (w1 - w0) * v
            b.prism([(-local_w / 2, -0.035), (local_w / 2, -0.035), (local_w / 2, 0.035), (-local_w / 2, 0.035)],
                    0.04, rust, frame(0, v))


def _corner_posts(b, z0, z1, w0, w1, mat, r=0.07):
    for sx in (-1, 1):
        for sy in (-1, 1):
            b.tube((sx * w0 / 2, sy * w0 / 2, z0), (sx * w1 / 2, sy * w1 / 2, z1), r, mat, seg=6)


def _finial(b, x, y, z, iron, height=0.7):
    b.ellipsoid((x, y, z + 0.12), (0.12, 0.12, 0.12), iron, seg=8, rings=6)
    b.tube((x, y, z + 0.2), (x, y, z + height), 0.035, iron, seg=6, r1=0.005)


def _clock_face(b, matrix, size, white, red, iron):
    """One clock face in local XY facing +Z: square white plate, red rim and rosettes, hour marks, hands."""
    half = size / 2
    b.prism([(-half, -half), (half, -half), (half, half), (-half, half)], 0.06, red, matrix)
    inner = half - 0.09
    b.prism([(-inner, -inner), (inner, -inner), (inner, inner), (-inner, inner)], 0.08, white, matrix)
    r = half * 0.86
    b.prism(_circle(r, 40), 0.1, red, matrix)
    b.prism(_circle(r - 0.1, 40), 0.12, white, matrix)
    for k in range(4):        # rosettes in the corners
        a = math.pi / 4 + k * math.pi / 2
        cm = matrix @ Matrix.Translation((math.cos(a) * half * 1.02, math.sin(a) * half * 1.02, 0.1))
        b.prism(_star(half * 0.14, 0.55, 8), 0.03, red, cm)
    for h in range(12):
        a = math.pi / 2 - h * math.pi / 6
        long_mark = h % 3 == 0
        length = 0.22 if long_mark else 0.13
        width = 0.07 if long_mark else 0.045
        d = r - 0.12 - length / 2
        m = matrix @ Matrix.Translation((math.cos(a) * d, math.sin(a) * d, 0.12)) @ Matrix.Rotation(a, 4, "Z")
        b.prism([(-length / 2, -width / 2), (length / 2, -width / 2), (length / 2, width / 2), (-length / 2, width / 2)],
                0.02, red, m)
    # hands at ten past ten
    for angle, length, width in ((math.radians(90 - 300), r * 0.5, 0.07), (math.radians(90 - 60), r * 0.78, 0.05)):
        m = matrix @ Matrix.Translation((0, 0, 0.15)) @ Matrix.Rotation(angle, 4, "Z")
        b.prism([(-0.08, -width / 2), (length, -width * 0.25), (length, width * 0.25), (-0.08, width / 2)], 0.03, iron, m)
    b.prism(_circle(0.08, 12), 0.05, iron, matrix @ Matrix.Translation((0, 0, 0.16)))


def build_clocktower(collection):
    b = mk.Builder("clocktower", scale=1.0)
    rust = b.mat("bb_rust_lattice", RUST, 0.55, 0.35)
    dark = b.mat("bb_rust_core", RUST_DARK, 0.7, 0.2)
    stone = b.mat("bb_stone", STONE, 0.75)
    stone_dark = b.mat("bb_stone_dark", STONE_DARK, 0.8)
    gold = b.mat("bb_gold", GOLD, 0.3, 0.85)
    white = b.mat("bb_clock_white", CLOCK_WHITE, 0.4)
    red = b.mat("bb_clock_red", CLOCK_RED, 0.45)
    iron = b.mat("bb_iron", IRON, 0.45, 0.6)
    glass = b.mat("bb_glass", GLASS, 0.1, 0.2)

    # stepped stone plinth with the glass plaque on the front
    b.box((0, 0, 0.25), (8.2, 8.2, 0.5), stone_dark, bevel=0.03)
    b.box((0, 0, 0.5 + (PLINTH_H - 0.5) / 2), (7.2, 7.2, PLINTH_H - 0.5), stone, bevel=0.03, taper=(0.86, 0.86))
    for side in range(4):
        spin = Matrix.Rotation(side * math.pi / 2, 4, "Z")
        p = spin @ Vector((0, -3.42, 1.05))
        b.box(p, (2.6 if side % 2 == 0 else 2.6, 0.08, 0.42), glass, rot=(math.degrees(-0.22), 0, side * 90))

    # wide lower stage, joint, slender upper stage
    _lattice(b, PLINTH_H, LOWER_TOP, LOWER_BASE_W, LOWER_TOP_W, 6, 16, rust, dark)
    _corner_posts(b, PLINTH_H, LOWER_TOP, LOWER_BASE_W, LOWER_TOP_W, rust)
    b.box((0, 0, LOWER_TOP + 0.1), (LOWER_TOP_W + 0.2, LOWER_TOP_W + 0.2, 0.2), rust, bevel=0.02)
    for sx in (-1, 1):
        for sy in (-1, 1):
            _finial(b, sx * (LOWER_TOP_W / 2 + 0.05), sy * (LOWER_TOP_W / 2 + 0.05), LOWER_TOP + 0.2, iron)
    _lattice(b, LOWER_TOP + 0.2, UPPER_TOP, UPPER_BASE_W, UPPER_TOP_W, 4, 19, rust, dark)
    _corner_posts(b, LOWER_TOP + 0.2, UPPER_TOP, UPPER_BASE_W, UPPER_TOP_W, rust, r=0.06)

    # clock box
    cz = (UPPER_TOP + CLOCK_TOP) / 2
    b.box((0, 0, UPPER_TOP + 0.12), (CLOCK_W + 0.15, CLOCK_W + 0.15, 0.24), rust, bevel=0.02)
    b.box((0, 0, cz), (CLOCK_W, CLOCK_W, CLOCK_TOP - UPPER_TOP), dark)
    b.box((0, 0, CLOCK_TOP - 0.1), (CLOCK_W + 0.15, CLOCK_W + 0.15, 0.2), rust, bevel=0.02)
    for side in range(4):
        spin = Matrix.Rotation(side * math.pi / 2, 4, "Z")
        # local +Z faces outwards (-Y), local +Y up
        m = spin @ Matrix.Translation((0, -CLOCK_W / 2, cz)) @ Matrix.Rotation(math.pi / 2, 4, "X")
        _clock_face(b, m, CLOCK_W - 0.18, white, red, iron)
    for sx in (-1, 1):
        for sy in (-1, 1):
            _finial(b, sx * (CLOCK_W / 2 + 0.02), sy * (CLOCK_W / 2 + 0.02), CLOCK_TOP, iron, height=0.45)

    # gold pyramid cap and spike
    cap_h = CAP_TOP - CLOCK_TOP - 0.6
    b.box((0, 0, CLOCK_TOP + cap_h / 2), (CLOCK_W - 0.1, CLOCK_W - 0.1, cap_h), gold, taper=(0.04, 0.04))
    b.ellipsoid((0, 0, CLOCK_TOP + cap_h + 0.05), (0.1, 0.1, 0.1), gold, seg=8, rings=6)
    b.tube((0, 0, CLOCK_TOP + cap_h), (0, 0, CAP_TOP), 0.03, gold, seg=6, r1=0.004)
    return b.finish(collection)


def build_fountain(collection, radius=10.0):
    """Basin rim, floor and jet nozzles. The water surface and the jets are added in Unity, so they can rise
    and spray when the square is clean and run dry when it is polluted (FountainWaterResponder)."""
    b = mk.Builder("clocktower_fountain", scale=1.0)
    stone = b.mat("bb_stone", STONE, 0.75)
    stone_dark = b.mat("bb_stone_dark", STONE_DARK, 0.8)
    iron = b.mat("bb_iron", IRON, 0.45, 0.6)
    rim = [(radius - 0.5, 0.0), (radius + 0.25, 0.0), (radius + 0.25, 0.55), (radius - 0.5, 0.55), (radius - 0.5, 0.0)]
    b.revolve(rim, stone, seg=48, caps=False)
    b.prism(_circle(radius - 0.45, 48), 0.08, stone_dark, Matrix.Identity(4))
    for ring, count in ((radius - 1.4, 32), (radius - 3.2, 20)):
        for i in range(count):
            a = 2 * math.pi * (i + (0.5 if ring < radius - 2 else 0)) / count
            x, y = math.cos(a) * ring, math.sin(a) * ring
            b.tube((x, y, 0.08), (x, y, 0.4), 0.07, iron, seg=6)
    return b.finish(collection)


MODELS = {
    "clocktower": build_clocktower,
    "clocktower_fountain": build_fountain,
}
