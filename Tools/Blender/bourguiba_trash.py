"""Tunisian street litter and bins for the Avenue Habib Bourguiba map.

Skips and piles (static; the municipal bin is in bourguiba_street.py, in parts):
  skip                     a flared steel skip (benne), faded green with rust
  skip_full                the same, heaped with bags, cardboard and a broken chair
  bag_pile                 a kerbside pile of tied bags

Litter the player can grab (one piece each, real size):
  bag_black_tied, bag_blue_tied, bottle_water, bottle_water_crushed, cup_espresso, cup_espresso_tan,
  glibettes_cone (a paper cone of seeds), glibettes_shells (a scatter of shells, decoration),
  kaskrout_paper (sandwich wrap with a harissa stain), tin_tuna, tin_harissa,
  chair_monobloc, chair_monobloc_broken

No real brands: labels are plain colour bands. Modelled in metres, origin on the floor, front towards -Y.
"""

import math
import random

from mathutils import Matrix, Vector

import mesh_kit as mk

BIN_GREEN = (0.11, 0.38, 0.21)
BIN_GREEN_DARK = (0.07, 0.25, 0.14)
SKIP_GREEN = (0.28, 0.42, 0.30)
RUST = (0.45, 0.20, 0.09)
RUBBER = (0.05, 0.05, 0.05)
STEEL = (0.55, 0.56, 0.58)
BAG_BLACK = (0.035, 0.035, 0.04)
BAG_BLUE = (0.10, 0.30, 0.70)
BOTTLE = (0.72, 0.86, 0.94)
LABEL_BLUE = (0.10, 0.36, 0.78)
CAP_BLUE = (0.06, 0.24, 0.64)
CUP_WHITE = (0.94, 0.93, 0.90)
CUP_TAN = (0.78, 0.62, 0.42)
COFFEE = (0.22, 0.12, 0.06)
PAPER = (0.92, 0.90, 0.84)
NEWSPRINT = (0.78, 0.77, 0.72)
HARISSA = (0.78, 0.18, 0.06)
TIN = (0.70, 0.71, 0.73)
LABEL_YELLOW = (0.95, 0.78, 0.12)
LABEL_RED = (0.80, 0.10, 0.07)
CHAIR_WHITE = (0.93, 0.93, 0.91)
CARDBOARD = (0.62, 0.45, 0.26)


def _jitter(bm, amount, rng, keep_floor=True):
    for v in bm.verts:
        d = Vector((rng.uniform(-amount, amount), rng.uniform(-amount, amount), rng.uniform(-amount, amount)))
        if keep_floor and v.co.z < 0.01:
            d.z = 0.0
        v.co += d


def _drop_to_floor(bm):
    low = min(v.co.z for v in bm.verts)
    for v in bm.verts:
        v.co.z -= low


# ---------------------------------------------------------------- bins and skips

def _bag(b, center, size, mat, rng, tie=True):
    bm = b.ellipsoid(center, size, mat, seg=10, rings=7)
    _jitter(bm, min(size) * 0.18, rng, keep_floor=False)
    if tie:
        top = Vector(center) + Vector((0, 0, size[2] * 0.95))
        b.ellipsoid(top, (0.05, 0.05, 0.05), mat, seg=6, rings=4)
        for s in (-1, 1):
            b.ellipsoid(top + Vector((s * 0.05, 0, 0.05)), (0.045, 0.02, 0.07), mat, seg=6, rings=4,
                        rot=(0, s * 35, 0))


def _skip_shell(b, paint, rust, steel):
    """Flared steel skip: floor, two sloping sides, two raked ends, lifting lugs and rust patches."""
    L0, L1, W0, W1, H = 3.0, 3.8, 1.6, 1.8, 1.3
    rake = math.degrees(math.atan2((L1 - L0) / 2, H))
    side = math.degrees(math.atan2((W1 - W0) / 2, H))
    b.box((0, 0, 0.1), (L0, W0, 0.08), paint)
    for sy in (-1, 1):
        b.box((0, sy * (W0 + W1) / 4, H / 2 + 0.1), ((L0 + L1) / 2, 0.06, H / math.cos(math.radians(side))), paint,
              rot=(sy * side, 0, 0))
        b.box((0, sy * (W1 / 2 + 0.03), H + 0.08), (L1 + 0.05, 0.1, 0.1), paint)     # top rail
        for k in range(-2, 3):   # ribs
            b.box((k * 0.75, sy * ((W0 + W1) / 4 + 0.04), H / 2 + 0.1), (0.08, 0.06, H), paint, rot=(sy * side, 0, 0))
    for sx in (-1, 1):
        b.box((sx * (L0 + L1) / 4, 0, H / 2 + 0.1), (0.06, W1, H / math.cos(math.radians(rake))), paint,
              rot=(0, -sx * rake, 0))
        for sy in (-1, 1):
            b.tube((sx * (L1 / 2 + 0.05), sy * 0.6, H * 0.7), (sx * (L1 / 2 + 0.2), sy * 0.6, H * 0.7), 0.05, steel, seg=8)
    for x, z, w, h in ((-1.2, 0.5, 0.5, 0.3), (0.6, 0.9, 0.35, 0.45), (1.4, 0.3, 0.6, 0.2)):   # rust patches
        b.box((x, -(W0 + W1) / 4 - 0.035, z), (w, 0.01, h), rust, rot=(-side, 0, 0))
    b.box((0, 0, 0.03), (L0 - 0.2, W0 - 0.2, 0.06), steel)    # skids


def build_skip(collection):
    b = mk.Builder("skip", scale=1.0)
    _skip_shell(b, b.mat("tn_skip_green", SKIP_GREEN, 0.7, 0.2), b.mat("tn_rust", RUST, 0.9), b.mat("tn_steel", STEEL, 0.4, 0.7))
    return b.finish(collection)


def build_skip_full(collection):
    rng = random.Random(11)
    b = mk.Builder("skip_full", scale=1.0)
    _skip_shell(b, b.mat("tn_skip_green", SKIP_GREEN, 0.7, 0.2), b.mat("tn_rust", RUST, 0.9), b.mat("tn_steel", STEEL, 0.4, 0.7))
    black, blue = b.mat("tn_bag_black", BAG_BLACK, 0.35), b.mat("tn_bag_blue", BAG_BLUE, 0.4)
    card = b.mat("tn_cardboard", CARDBOARD, 0.9)
    rubble = b.mat("tn_rubble", (0.55, 0.52, 0.48), 0.95)
    heap = b.ellipsoid((0, 0, 1.15), (1.7, 0.78, 0.45), rubble, seg=14, rings=7)
    _jitter(heap, 0.12, rng, keep_floor=False)
    for i in range(9):
        _bag(b, (rng.uniform(-1.4, 1.4), rng.uniform(-0.5, 0.5), 1.45 + rng.uniform(0, 0.2)), (0.3, 0.25, 0.22),
             (black, blue)[i % 2], rng)
    for i in range(4):
        b.box((rng.uniform(-1.2, 1.2), rng.uniform(-0.4, 0.4), 1.5), (0.6, 0.45, 0.3), card,
              rot=(rng.uniform(-30, 30), rng.uniform(-30, 30), rng.uniform(0, 90)))
    _monobloc(b, b.mat("tn_chair_white", CHAIR_WHITE, 0.45), broken=True,
              matrix=Matrix.Translation((0.9, 0.1, 1.55)) @ Matrix.Rotation(math.radians(70), 4, "Y"))
    return b.finish(collection)


def build_bag_pile(collection):
    rng = random.Random(5)
    b = mk.Builder("bag_pile", scale=1.0)
    mats = (b.mat("tn_bag_black", BAG_BLACK, 0.35), b.mat("tn_bag_blue", BAG_BLUE, 0.4),
            b.mat("tn_bag_white", (0.88, 0.88, 0.86), 0.45))
    spots = [(-0.5, 0.0, 0.0), (0.0, 0.1, 0.0), (0.5, -0.05, 0.0), (-0.25, 0.35, 0.0), (0.3, 0.4, 0.0),
             (-0.2, 0.1, 0.38), (0.25, 0.15, 0.36), (0.75, 0.3, 0.0)]
    for i, (x, y, z) in enumerate(spots):
        size = (rng.uniform(0.24, 0.3), rng.uniform(0.2, 0.26), rng.uniform(0.2, 0.24))
        _bag(b, (x, y, z + size[2] * 0.85), size, mats[(0, 1, 0, 2, 0, 1, 0, 2)[i]], rng)
    return b.finish(collection)


# ---------------------------------------------------------------- grabbable litter

def build_bag_tied(name, colour):
    def build(collection):
        rng = random.Random(len(name))
        b = mk.Builder(name, scale=1.0)
        _bag(b, (0, 0, 0.19), (0.2, 0.17, 0.2), b.mat(f"tn_{name}", colour, 0.38), rng)
        return b.finish(collection)
    return build


def _bottle_profile(h=0.32, r=0.045):
    return [(0.0, 0.0), (r * 0.8, 0.0), (r, 0.012), (r, h * 0.62), (r * 0.92, h * 0.68), (r, h * 0.72),
            (r * 0.96, h * 0.78), (r * 0.5, h * 0.9), (r * 0.36, h * 0.94), (r * 0.36, h * 0.97)]


def build_bottle_water(name, crushed=False):
    def build(collection):
        rng = random.Random(3)
        b = mk.Builder(name, scale=1.0)
        plastic = b.mat("tn_bottle_plastic", BOTTLE, 0.1, alpha=0.7)
        label = b.mat("tn_label_blue", LABEL_BLUE, 0.5)
        white = b.mat("tn_label_white", (0.95, 0.95, 0.95), 0.5)
        cap = b.mat("tn_cap_blue", CAP_BLUE, 0.5)
        parts = [b.revolve(_bottle_profile(), plastic, seg=14),
                 b.revolve([(0.0462, 0.1), (0.0462, 0.17), (0.0, 0.17)], label, seg=14, caps=False),
                 b.revolve([(0.0465, 0.125), (0.0465, 0.14)], white, seg=14, caps=False),
                 b.revolve([(0.0, 0.31), (0.018, 0.31), (0.018, 0.335), (0.0, 0.335)], cap, seg=10)]
        if crushed:
            for bm in parts:
                for v in bm.verts:
                    v.co.x *= 0.55
                    v.co.z *= 0.78 + 0.1 * math.sin(v.co.z * 40)
                _jitter(bm, 0.004, rng, keep_floor=False)
        # lie it on its side: litter is dropped, not stood up
        for bm in parts:
            for v in bm.verts:
                v.co = Matrix.Rotation(math.pi / 2, 3, "Y") @ v.co
        for bm in parts:
            for v in bm.verts:
                v.co.z += 0.03 if crushed else 0.046
        return b.finish(collection)
    return build


def build_cup(name, colour):
    def build(collection):
        b = mk.Builder(name, scale=1.0)
        cup = b.mat(f"tn_{name}", colour, 0.55)
        b.revolve([(0.0, 0.0), (0.021, 0.0), (0.028, 0.062), (0.03, 0.065), (0.027, 0.065), (0.02, 0.006), (0.0, 0.006)],
                  cup, seg=14, caps=False)
        b.revolve([(0.0, 0.012), (0.021, 0.012)], b.mat("tn_coffee", COFFEE, 0.3), seg=12, caps=False)
        return b.finish(collection)
    return build


def build_glibettes_cone(collection):
    rng = random.Random(9)
    b = mk.Builder("glibettes_cone", scale=1.0)
    paper = b.mat("tn_newsprint", NEWSPRINT, 0.9)
    shell = b.mat("tn_seed_black", (0.06, 0.06, 0.06), 0.6)
    stripe = b.mat("tn_seed_white", (0.85, 0.84, 0.8), 0.6)
    m = Matrix.Translation((0, 0, 0.04)) @ Matrix.Rotation(math.radians(90), 4, "Y") @ Matrix.Translation((0, 0, -0.08))
    b.revolve([(0.0, 0.0), (0.045, 0.16), (0.043, 0.162), (0.0, 0.02)], paper, seg=12, matrix=m, caps=False)
    for i in range(10):
        b.ellipsoid((0.07 + rng.uniform(0, 0.08), rng.uniform(-0.05, 0.05), 0.006), (0.009, 0.005, 0.004),
                    (shell, stripe)[i % 3 == 0], seg=6, rings=3, rot=(0, 0, rng.uniform(0, 180)))
    return b.finish(collection)


def build_glibettes_shells(collection):
    rng = random.Random(4)
    b = mk.Builder("glibettes_shells", scale=1.0)
    shell = b.mat("tn_seed_black", (0.06, 0.06, 0.06), 0.6)
    stripe = b.mat("tn_seed_white", (0.85, 0.84, 0.8), 0.6)
    pumpkin = b.mat("tn_seed_pale", (0.86, 0.82, 0.62), 0.6)
    for i in range(60):
        r, a = 0.22 * math.sqrt(rng.random()), rng.uniform(0, 2 * math.pi)
        b.ellipsoid((r * math.cos(a), r * math.sin(a), 0.003), (0.009, 0.0045, 0.003), (shell, stripe, pumpkin)[i % 3],
                    seg=6, rings=3, rot=(0, 0, rng.uniform(0, 180)))
    return b.finish(collection)


def build_kaskrout_paper(collection):
    rng = random.Random(8)
    b = mk.Builder("kaskrout_paper", scale=1.0)
    paper = b.ellipsoid((0, 0, 0.045), (0.08, 0.06, 0.045), b.mat("tn_paper_wrap", PAPER, 0.85), seg=10, rings=7)
    _jitter(paper, 0.014, rng, keep_floor=False)
    stain = b.ellipsoid((0.035, -0.02, 0.05), (0.045, 0.04, 0.035), b.mat("tn_harissa", HARISSA, 0.5), seg=8, rings=5)
    _jitter(stain, 0.008, rng, keep_floor=False)
    for bm in (paper, stain):
        for v in bm.verts:
            v.co.z = max(v.co.z, 0.0)
    return b.finish(collection)


def _tin(b, r, h, body_mat, label, stripe, lid_open):
    b.revolve([(0.0, 0.0), (r, 0.0), (r, h), (r * 0.94, h), (r * 0.94, h - 0.004), (0.0, h - 0.004)], body_mat, seg=16)
    b.revolve([(r + 0.0008, h * 0.15), (r + 0.0008, h * 0.85)], label, seg=16, caps=False)
    if stripe:
        b.revolve([(r + 0.0012, h * 0.42), (r + 0.0012, h * 0.58)], stripe, seg=16, caps=False)
    if lid_open:   # lid peeled back on its hinge
        hinge = Matrix.Translation((0, r, h)) @ Matrix.Rotation(math.radians(-100), 4, "X") @ Matrix.Translation((0, -r, 0))
        b.revolve([(0.0, 0.0), (r * 0.95, 0.0), (r * 0.95, 0.002), (0.0, 0.002)], body_mat, seg=16, matrix=hinge)


def build_tin_tuna(collection):
    b = mk.Builder("tin_tuna", scale=1.0)
    _tin(b, 0.044, 0.034, b.mat("tn_tin", TIN, 0.3, 0.9), b.mat("tn_label_yellow", LABEL_YELLOW, 0.5),
         b.mat("tn_label_blue", LABEL_BLUE, 0.5), True)
    return b.finish(collection)


def build_tin_harissa(collection):
    b = mk.Builder("tin_harissa", scale=1.0)
    _tin(b, 0.036, 0.075, b.mat("tn_tin", TIN, 0.3, 0.9), b.mat("tn_label_red", LABEL_RED, 0.5),
         b.mat("tn_label_yellow", LABEL_YELLOW, 0.5), True)
    return b.finish(collection)


def _monobloc(b, mat, broken=False, matrix=None):
    """The white plastic garden chair of every Tunis café: splayed legs, slotted back, arms."""
    start = len(b.parts)
    b.box((0, 0, 0.44), (0.46, 0.44, 0.035), mat, bevel=0.015, segments=2)
    legs = [(-1, -1), (1, -1), (-1, 1), (1, 1)]
    for i, (sx, sy) in enumerate(legs):
        if broken and i == 1:
            b.tube((sx * 0.2, sy * 0.19, 0.43), (sx * 0.23, sy * 0.22, 0.3), 0.022, mat, seg=6, r1=0.03)   # snapped stump
            continue
        b.tube((sx * 0.2, sy * 0.19, 0.43), (sx * 0.27, sy * 0.25, 0.0), 0.022, mat, seg=6, r1=0.03)
    tilt = 14 if not broken else 32
    for k in range(-2, 3):   # back slats between two rails
        if broken and k == 1:
            continue
        b.box((k * 0.085, 0.24, 0.66), (0.055, 0.025, 0.38), mat, rot=(-tilt, 0, 0))
    b.box((0, 0.29, 0.86), (0.48, 0.035, 0.08), mat, bevel=0.012, segments=2, rot=(-tilt, 0, 0))
    b.box((0, 0.21, 0.48), (0.46, 0.03, 0.05), mat, rot=(-tilt, 0, 0))
    for sx in (-1, 1):
        b.box((sx * 0.25, 0.02, 0.62), (0.045, 0.42, 0.03), mat)
        b.tube((sx * 0.25, -0.18, 0.62), (sx * 0.23, -0.19, 0.44), 0.016, mat, seg=5)
    if matrix is not None:
        for bm, _, _ in b.parts[start:]:
            for v in bm.verts:
                v.co = matrix @ v.co


def build_chair(name, broken):
    def build(collection):
        b = mk.Builder(name, scale=1.0)
        mat = b.mat("tn_chair_white" if not broken else "tn_chair_grubby", CHAIR_WHITE if not broken else (0.82, 0.8, 0.74), 0.45)
        if broken:   # knocked over onto its side, missing a leg
            _monobloc(b, mat, True, Matrix.Translation((0, 0, 0.3)) @ Matrix.Rotation(math.radians(90), 4, "Y"))
            low = min(v.co.z for bm, _, _ in b.parts for v in bm.verts)
            for bm, _, _ in b.parts:
                for v in bm.verts:
                    v.co.z -= low
        else:
            _monobloc(b, mat)
        return b.finish(collection)
    return build


MODELS = {
    "skip": build_skip,
    "skip_full": build_skip_full,
    "bag_pile": build_bag_pile,
    "bag_black_tied": build_bag_tied("bag_black_tied", BAG_BLACK),
    "bag_blue_tied": build_bag_tied("bag_blue_tied", BAG_BLUE),
    "bottle_water": build_bottle_water("bottle_water"),
    "bottle_water_crushed": build_bottle_water("bottle_water_crushed", crushed=True),
    "cup_espresso": build_cup("cup_espresso", CUP_WHITE),
    "cup_espresso_tan": build_cup("cup_espresso_tan", CUP_TAN),
    "glibettes_cone": build_glibettes_cone,
    "glibettes_shells": build_glibettes_shells,
    "kaskrout_paper": build_kaskrout_paper,
    "tin_tuna": build_tin_tuna,
    "tin_harissa": build_tin_harissa,
    "chair_monobloc": build_chair("chair_monobloc", False),
    "chair_monobloc_broken": build_chair("chair_monobloc_broken", True),
}
