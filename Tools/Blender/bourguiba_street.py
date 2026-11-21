"""Street furniture for the Avenue Habib Bourguiba map: the municipal bin (in parts, so Unity can show how full
it is) and the avenue's cast-iron street lamp.

  municipal_bin_body       the green four-wheel 1100 L container as an open-topped shell (walls, floor, rim,
                           handles, castors, pedal bar, white band and emblem)
  municipal_bin_lid        its domed lid; origin on the hinge at the back, closed pose (Unity swings it open)
  municipal_bin_contents   the rubbish inside: a heap of tied bags and loose litter sized to the interior; origin at
                           the bottom of the heap (Unity raises and grows it with the fill level)
  municipal_bin_overflow   bags piled above the rim, shown only while the bin overflows
  street_lamp              a double-globe cast-iron lamp post like the ones round Bab Bhar

Modelled in metres, front towards -Y, origin on the floor (except the lid and contents, see above).
"""

import math
import random

from mathutils import Matrix, Vector

import mesh_kit as mk

BIN_GREEN = (0.05, 0.42, 0.20)
BIN_GREEN_DARK = (0.03, 0.27, 0.13)
BIN_INSIDE = (0.04, 0.16, 0.09)
WHITE = (0.95, 0.95, 0.92)
RUBBER = (0.05, 0.05, 0.05)
STEEL = (0.55, 0.56, 0.58)
BAGS = ((0.035, 0.035, 0.04), (0.10, 0.30, 0.70), (0.88, 0.88, 0.86))
IRON = (0.07, 0.12, 0.10)
GLOBE = (0.98, 0.96, 0.88)

# Bin interior (inside the walls), shared with Unity's BinFillVisual through these numbers
FLOOR_Z = 0.24
RIM_Z = 1.2
INNER_BOTTOM = (1.08, 0.82)   # inside width/depth at the floor
INNER_TOP = (1.24, 0.94)      # inside width/depth at the rim
HINGE_Y = 0.55                # back edge (+Y) where the lid hinges
HINGE_Z = 1.24


def _jitter(bm, amount, rng):
    for v in bm.verts:
        v.co += Vector((rng.uniform(-amount, amount), rng.uniform(-amount, amount), rng.uniform(-amount, amount)))


def _bag(b, center, size, mat, rng):
    bm = b.ellipsoid(center, size, mat, seg=8, rings=6)
    _jitter(bm, min(size) * 0.16, rng)
    top = Vector(center) + Vector((0, 0, size[2] * 0.95))
    b.ellipsoid(top, (0.05, 0.05, 0.05), mat, seg=6, rings=4)
    for s in (-1, 1):
        b.ellipsoid(top + Vector((s * 0.05, 0, 0.05)), (0.045, 0.02, 0.07), mat, seg=6, rings=4, rot=(0, s * 35, 0))


def build_bin_body(collection):
    b = mk.Builder("municipal_bin_body", scale=1.0)
    green, dark = b.mat("tn_bin_green", BIN_GREEN, 0.5), b.mat("tn_bin_green_dark", BIN_GREEN_DARK, 0.55)
    inside = b.mat("tn_bin_inside", BIN_INSIDE, 0.8)
    white, rubber, steel = b.mat("tn_bin_white", WHITE, 0.5), b.mat("tn_rubber", RUBBER, 0.9), b.mat("tn_steel", STEEL, 0.4, 0.7)
    t = 0.05
    h = RIM_Z - FLOOR_Z
    wb, db = INNER_BOTTOM
    wt, dt = INNER_TOP
    # floor, then four flared walls (outside green, inside dark so the fill reads from above)
    b.box((0, 0, FLOOR_Z - 0.03), (wb + 2 * t, db + 2 * t, 0.06), green)
    flare_x = math.degrees(math.atan2((wt - wb) / 2, h))
    flare_y = math.degrees(math.atan2((dt - db) / 2, h))
    zc = FLOOR_Z + h / 2
    for s in (-1, 1):
        x = s * ((wb + wt) / 4 + t / 2)
        b.box((x, 0, zc), (t, (db + dt) / 2 + 2 * t, h / math.cos(math.radians(flare_x))), green, rot=(0, s * flare_x, 0))
        b.box((x - s * t * 0.6, 0, zc), (0.01, (db + dt) / 2, h - 0.02), inside, rot=(0, s * flare_x, 0))
        y = s * ((db + dt) / 4 + t / 2)
        b.box((0, y, zc), ((wb + wt) / 2 + 2 * t, t, h / math.cos(math.radians(flare_y))), green, rot=(-s * flare_y, 0, 0))
        b.box((0, y - s * t * 0.6, zc), ((wb + wt) / 2, 0.01, h - 0.02), inside, rot=(-s * flare_y, 0, 0))
    # rim, handles, band and emblem on the front
    for s in (-1, 1):
        b.box((0, s * (dt / 2 + t), RIM_Z + 0.02), (wt + 2 * t + 0.1, 0.1, 0.08), dark)
        b.box((s * (wt / 2 + t), 0, RIM_Z + 0.02), (0.1, dt + 2 * t + 0.1, 0.08), dark)
        b.box((s * (wt / 2 + t + 0.06), 0, 1.0), (0.05, 0.5, 0.05), dark)
    front_y = -((db + dt) / 4 + t + 0.01)
    b.box((0, front_y - 0.01, 0.82), (wb + 0.14, 0.02, 0.16), white, rot=(flare_y, 0, 0))
    b.revolve([(0.0, 0.0), (0.12, 0.0), (0.12, 0.02), (0.0, 0.02)], white, seg=12,
              matrix=Matrix.Translation((0, front_y - 0.02, 0.55)) @ Matrix.Rotation(math.pi / 2, 4, "X"))
    # castors and the foot pedal that lifts the lid
    for sx in (-1, 1):
        for sy in (-1, 1):
            x, y = sx * 0.48, sy * 0.36
            b.box((x, y, 0.17), (0.08, 0.08, 0.06), steel)
            b.tube((x - 0.04, y, 0.08), (x + 0.04, y, 0.08), 0.075, rubber, seg=8)
    b.tube((-0.4, front_y - 0.08, 0.3), (0.4, front_y - 0.08, 0.3), 0.022, steel, seg=6)
    return b.finish(collection)


def build_bin_lid(collection):
    """Origin on the hinge (back top edge). The lid extends forwards (-Y) from it."""
    b = mk.Builder("municipal_bin_lid", scale=1.0)
    green, dark = b.mat("tn_bin_green", BIN_GREEN, 0.5), b.mat("tn_bin_green_dark", BIN_GREEN_DARK, 0.55)
    depth = 2 * HINGE_Y + 0.02
    b.box((0, -depth / 2, 0.04), (INNER_TOP[0] + 0.22, depth, 0.07), dark)
    b.box((0, -depth / 2 + 0.02, 0.12), (INNER_TOP[0] + 0.02, depth - 0.16, 0.1), green, taper=(0.86, 0.78))
    b.box((0, -depth + 0.04, 0.0), (0.5, 0.06, 0.08), dark)   # front grip
    return b.finish(collection)


def build_bin_contents(collection):
    """A heap of bags and loose litter as tall as HEAP_H, filling the interior; origin at its bottom."""
    rng = random.Random(21)
    b = mk.Builder("municipal_bin_contents", scale=1.0)
    mats = [b.mat(f"tn_bag_{n}", c, 0.4) for n, c in zip(("black", "blue", "white"), BAGS)]
    card = b.mat("tn_cardboard", (0.62, 0.45, 0.26), 0.9)
    bottle = b.mat("tn_bottle_plastic", (0.72, 0.86, 0.94), 0.15)
    w, d = INNER_BOTTOM[0] - 0.06, INNER_BOTTOM[1] - 0.06
    b.box((0, 0, 0.1), (w, d, 0.2), mats[0])   # packed bottom layer
    spots = [(-0.3, -0.18), (0.05, -0.2), (0.35, -0.15), (-0.25, 0.18), (0.1, 0.2), (0.38, 0.17)]
    for i, (x, y) in enumerate(spots):
        _bag(b, (x, y, 0.3), (0.22, 0.2, 0.17), mats[i % 3], rng)
    b.box((-0.05, 0.02, 0.38), (0.4, 0.3, 0.06), card, rot=(8, -12, 25))
    b.tube((0.22, -0.05, 0.36), (0.42, 0.05, 0.4), 0.045, bottle, seg=6)
    return b.finish(collection)


def build_bin_overflow(collection):
    """Bags mounded above the rim (origin on the floor like the body)."""
    rng = random.Random(33)
    b = mk.Builder("municipal_bin_overflow", scale=1.0)
    mats = [b.mat(f"tn_bag_{n}", c, 0.4) for n, c in zip(("black", "blue", "white"), BAGS)]
    for i, (x, y, z) in enumerate(((-0.3, -0.1, 1.32), (0.15, 0.05, 1.36), (0.42, -0.2, 1.3), (-0.05, -0.32, 1.42),
                                   (0.0, 0.2, 1.55))):
        _bag(b, (x, y, z), (0.26, 0.22, 0.2), mats[i % 3], rng)
    _bag(b, (0.2, -0.72, 1.05), (0.2, 0.16, 0.24), mats[1], rng)   # one hanging over the front
    return b.finish(collection)


def build_street_lamp(collection):
    """Cast-iron post with a fluted shaft, scrolled crossbar and two white globes (about 4.6 m)."""
    b = mk.Builder("street_lamp", scale=1.0)
    iron = b.mat("tn_lamp_iron", IRON, 0.45, 0.4)
    globe = b.mat("tn_lamp_globe", GLOBE, 0.3, emission=1.2)
    b.revolve([(0.0, 0.0), (0.32, 0.0), (0.32, 0.12), (0.24, 0.2), (0.24, 0.55), (0.16, 0.7), (0.0, 0.7)], iron, seg=8)
    b.revolve([(0.0, 0.7), (0.1, 0.7), (0.075, 3.6), (0.12, 3.7), (0.12, 3.8), (0.0, 3.8)], iron, seg=8)
    for z in (1.4, 2.6):
        b.revolve([(0.0, z), (0.12, z), (0.12, z + 0.08), (0.0, z + 0.08)], iron, seg=8)
    for s in (-1, 1):   # arms curling out to the globes
        b.path([(0, 0, 3.72), (s * 0.35, 0, 3.78), (s * 0.62, 0, 3.95), (s * 0.7, 0, 4.15)], 0.03, iron, seg=5)
        b.path([(0, 0, 3.4), (s * 0.25, 0, 3.5), (s * 0.4, 0, 3.72)], 0.02, iron, seg=5)
        b.revolve([(0.0, 4.15), (0.07, 4.15), (0.07, 4.22), (0.0, 4.22)], iron, seg=8, matrix=Matrix.Translation((s * 0.7, 0, 0)))
        b.ellipsoid((s * 0.7, 0, 4.42), (0.2, 0.2, 0.22), globe, seg=10, rings=7)
        b.revolve([(0.0, 4.6), (0.06, 4.6), (0.0, 4.72)], iron, seg=6, matrix=Matrix.Translation((s * 0.7, 0, 0)))
    b.revolve([(0.0, 3.8), (0.05, 3.8), (0.0, 4.15)], iron, seg=6)
    return b.finish(collection)


MODELS = {
    "municipal_bin_body": build_bin_body,
    "municipal_bin_lid": build_bin_lid,
    "municipal_bin_contents": build_bin_contents,
    "municipal_bin_overflow": build_bin_overflow,
    "street_lamp": build_street_lamp,
}
