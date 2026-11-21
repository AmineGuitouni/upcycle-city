"""The factory yard behind the avenue's east row, in the same chunky low-poly style as the Bab Bhar buildings.

  factory_hall       a sawtooth-roofed production hall: corrugated walls, glazed roof teeth, a big rolling door,
                     a high window band, rust streaks and an "USINE" sign (24 × 16 m, about 12 m high)
  factory_warehouse  a gable-roofed storage shed with an open bay and stacked pallets (18 × 12 m)
  smokestack         a tapered brick chimney with white and red bands near the top, an iron ladder and a crown
                     (about 26 m). Its smoke is a Unity particle system at SMOKESTACK_TOP.
  water_tower        an industrial tank on braced steel legs (about 15 m)

Modelled in metres, front towards -Y, origin on the ground at the middle of the front (centre for the round ones).
"""

import math
import random

from mathutils import Matrix

import bourguiba_buildings as bb
import mesh_kit as mk

WALL = (0.52, 0.58, 0.58)
WALL_RIB = (0.46, 0.52, 0.52)
ROOF = (0.46, 0.30, 0.22)
RUST = (0.45, 0.22, 0.10)
CONCRETE = (0.62, 0.60, 0.56)
DOOR = (0.58, 0.60, 0.62)
GLASS = (0.20, 0.28, 0.32)
DARK = (0.08, 0.08, 0.09)
BRICK = (0.55, 0.24, 0.16)
BRICK_DARK = (0.42, 0.18, 0.12)
WHITE = (0.94, 0.94, 0.92)
RED = (0.78, 0.12, 0.08)
STEEL = (0.36, 0.38, 0.40)
TANK = (0.28, 0.45, 0.42)
WOOD = (0.72, 0.56, 0.36)
SIGN = (0.18, 0.55, 0.34)

SMOKESTACK_TOP = 26.0
FRONT_UP = Matrix.Rotation(math.pi / 2, 4, "X")


def _corrugated(b, x0, x1, y, z0, z1, wall, rib, out=0.06, step=0.9):
    """A flat wall face from x0 to x1 at y (facing -Y) with vertical ribs."""
    b.box(((x0 + x1) / 2, y + 0.1, (z0 + z1) / 2), (x1 - x0, 0.2, z1 - z0), wall)
    n = int((x1 - x0) / step)
    for i in range(1, n):
        b.box((x0 + (x1 - x0) * i / n, y - out / 2, (z0 + z1) / 2), (0.12, out, z1 - z0), rib)


def build_hall(collection):
    rng = random.Random(4)
    b = mk.Builder("factory_hall", scale=1.0)
    wall, rib = b.mat("ind_wall", WALL, 0.7), b.mat("ind_wall_rib", WALL_RIB, 0.7)
    roof, rust = b.mat("ind_roof", ROOF, 0.8), b.mat("ind_rust", RUST, 0.9)
    concrete, door = b.mat("ind_concrete", CONCRETE, 0.9), b.mat("ind_door", DOOR, 0.5, 0.4)
    glass, dark = b.mat("ind_glass", GLASS, 0.1, 0.3), b.mat("ind_dark", DARK, 0.8)
    sign, white = b.mat("ind_sign", SIGN, 0.6), b.mat("ind_white", WHITE, 0.5)
    W, D, H = 24.0, 16.0, 8.0

    b.box((0, D / 2, 0.3), (W + 0.4, D + 0.4, 0.6), concrete)                 # plinth
    b.box((0, D / 2, H / 2), (W, D, H), wall)                                  # body
    _corrugated(b, -W / 2, W / 2, 0.0, 0.6, H, wall, rib)                      # front ribs
    for s in (-1, 1):                                                          # side ribs
        for i in range(1, int(D / 0.9)):
            b.box((s * (W / 2 + 0.03), D * i / int(D / 0.9), H / 2 + 0.3), (0.06, 0.12, H - 0.6), rib)
    # rolling door, a small door, a high window band
    b.box((-5.0, -0.05, 2.8), (6.0, 0.1, 5.0), door)
    for k in range(10):
        b.box((-5.0, -0.12, 0.6 + k * 0.5), (6.0, 0.04, 0.05), dark)
    b.box((-5.0, -0.2, 5.5), (6.6, 0.4, 0.5), dark)
    b.box((2.5, -0.05, 1.3), (1.1, 0.1, 2.2), dark)
    b.box((7.0, -0.06, 6.6), (8.0, 0.12, 1.0), glass)
    for k in range(9):
        b.box((3.0 + k, -0.1, 6.6), (0.08, 0.06, 1.0), dark)
    # sawtooth roof: four teeth across the depth, glazing on their steep faces (facing north, +Y)
    teeth = 4
    for k in range(teeth):
        y0 = k * D / teeth
        b.box((0, y0 + D / teeth / 2, H + 1.6), (W + 0.3, D / teeth / math.cos(math.radians(38)), 0.18), roof,
              rot=(38, 0, 0))
        b.box((0, y0 + D / teeth - 0.05, H + 1.6), (W + 0.2, 0.1, 3.2), glass)
    b.box((0, -0.3, H + 0.2), (W + 0.6, 0.6, 0.4), roof)                      # front parapet
    # rust streaks under the roof and round the door
    for _ in range(7):
        x = rng.uniform(-W / 2 + 1, W / 2 - 1)
        b.box((x, -0.08, H - 1.2), (rng.uniform(0.4, 1.0), 0.03, rng.uniform(1.0, 2.6)), rust)
    # sign over the door
    b.box((-5.0, -0.25, 6.7), (5.2, 0.12, 1.1), sign)
    bb.text(b, "USINE", white, 0.8, Matrix.Translation((-5.0, -0.33, 6.65)) @ FRONT_UP, depth=0.06)
    # rooftop vents
    for x in (-8, 0, 8):
        b.revolve([(0.0, 0.0), (0.45, 0.0), (0.45, 1.2), (0.6, 1.25), (0.0, 1.6)], rib, seg=8,
                  matrix=Matrix.Translation((x, D * 0.6, H + 3.0)))
    return b.finish(collection)


def build_warehouse(collection):
    b = mk.Builder("factory_warehouse", scale=1.0)
    wall, rib = b.mat("ind_wall_tan", (0.66, 0.60, 0.50), 0.7), b.mat("ind_wall_tan_rib", (0.60, 0.54, 0.45), 0.7)
    roof, concrete = b.mat("ind_roof", ROOF, 0.8), b.mat("ind_concrete", CONCRETE, 0.9)
    dark, wood = b.mat("ind_dark", DARK, 0.8), b.mat("ind_pallet", WOOD, 0.85)
    W, D, H = 18.0, 12.0, 6.0
    b.box((0, D / 2, 0.15), (W + 0.4, D + 0.4, 0.3), concrete)
    # walls with an open bay in the middle of the front
    bay = 7.0
    for s in (-1, 1):
        x0, x1 = (-W / 2, -bay / 2) if s < 0 else (bay / 2, W / 2)
        _corrugated(b, x0, x1, 0.0, 0.3, H, wall, rib)
    b.box((0, 0.1, H - 0.6), (bay, 0.2, 1.2), wall)
    b.box((0, D - 0.1, H / 2), (W, 0.2, H), wall)
    for s in (-1, 1):
        b.box((s * (W / 2 - 0.1), D / 2, H / 2), (0.2, D, H), wall)
    b.box((0, D / 2, 0.31), (W - 0.4, D - 0.4, 0.02), dark)
    # gable roof
    pitch = math.degrees(math.atan2(2.4, W / 2))
    for s in (-1, 1):
        b.box((s * W / 4, D / 2, H + 1.2), (W / 2 / math.cos(math.radians(pitch)) + 0.4, D + 0.6, 0.2), roof,
              rot=(0, s * pitch, 0))
    for y in (0.0, D):
        b.prism([(-W / 2, 0), (W / 2, 0), (0, 2.4)], 0.2, wall, Matrix.Translation((0, y + (0.2 if y == 0 else 0), H)) @ FRONT_UP)
    # pallets stacked inside the bay
    for k in range(5):
        for s in range(2):
            x = -1.6 + s * 3.2
            z = 0.32 + k * 0.16
            b.box((x, 3.0, z + 0.06), (1.2, 1.0, 0.04), wood)
            for dx in (-0.5, 0.0, 0.5):
                b.box((x + dx, 3.0, z), (0.1, 1.0, 0.1), wood)
    return b.finish(collection)


def build_smokestack(collection):
    b = mk.Builder("smokestack", scale=1.0)
    brick, brick_dark = b.mat("ind_brick", BRICK, 0.85), b.mat("ind_brick_dark", BRICK_DARK, 0.85)
    white, red, steel = b.mat("ind_white", WHITE, 0.5), b.mat("ind_red", RED, 0.5), b.mat("ind_steel", STEEL, 0.5, 0.5)
    concrete = b.mat("ind_concrete", CONCRETE, 0.9)
    b.box((0, 0, 1.2), (4.0, 4.0, 2.4), concrete)
    r0, r1, top = 1.55, 1.0, SMOKESTACK_TOP
    b.revolve([(0.0, 2.4), (r0, 2.4), (r1, top - 3.0), (0.0, top - 3.0)], brick, seg=12)
    for k, z in enumerate((6.0, 11.0, 16.0)):   # darker brick courses
        r = r0 + (r1 - r0) * (z - 2.4) / (top - 5.4)
        b.revolve([(r + 0.04, z), (r + 0.04, z + 0.4)], brick_dark, seg=12, caps=False)
    # warning bands and the crown
    b.revolve([(0.0, top - 3.0), (r1, top - 3.0), (r1, top - 2.0), (0.0, top - 2.0)], white, seg=12)
    b.revolve([(0.0, top - 2.0), (r1, top - 2.0), (r1, top - 1.0), (0.0, top - 1.0)], red, seg=12)
    b.revolve([(0.0, top - 1.0), (r1, top - 1.0), (r1 + 0.25, top - 0.6), (r1 + 0.25, top), (r1 - 0.15, top), (r1 - 0.15, top - 0.4),
               (0.0, top - 0.4)], brick_dark, seg=12)
    # iron ladder up the front
    for s in (-0.25, 0.25):
        b.tube((s, -r0 - 0.15, 2.4), (s, -r1 - 0.15, top - 0.5), 0.03, steel, seg=4)
    for k in range(int((top - 3.0) / 0.8)):
        z = 2.8 + k * 0.8
        frac = (z - 2.4) / (top - 2.9)
        y = -(r0 + (r1 - r0) * frac) - 0.15
        b.box((0, y, z), (0.5, 0.04, 0.04), steel)
    return b.finish(collection)


def build_water_tower(collection):
    b = mk.Builder("water_tower", scale=1.0)
    steel, tank, roof = b.mat("ind_steel", STEEL, 0.5, 0.5), b.mat("ind_tank", TANK, 0.6, 0.2), b.mat("ind_roof", ROOF, 0.8)
    legs = [(-2.2, -2.2), (2.2, -2.2), (2.2, 2.2), (-2.2, 2.2)]
    for x, y in legs:
        b.tube((x * 1.15, y * 1.15, 0.0), (x, y, 10.0), 0.18, steel, seg=6)
        b.box((x * 1.15, y * 1.15, 0.2), (0.7, 0.7, 0.4), b.mat("ind_concrete", CONCRETE, 0.9))
    for z0, z1 in ((0.6, 4.6), (4.6, 8.6)):   # cross bracing
        for i in range(4):
            (xa, ya), (xb, yb) = legs[i], legs[(i + 1) % 4]
            b.tube((xa * 1.1, ya * 1.1, z0), (xb * 1.06, yb * 1.06, z1), 0.06, steel, seg=4)
            b.tube((xb * 1.1, yb * 1.1, z0), (xa * 1.06, ya * 1.06, z1), 0.06, steel, seg=4)
    b.revolve([(0.0, 10.0), (3.2, 10.0), (3.3, 10.3), (3.3, 13.4), (0.0, 13.4)], tank, seg=12)
    b.revolve([(3.45, 13.4), (2.0, 14.4), (0.3, 15.0), (0.0, 15.0)], roof, seg=12, caps=False)
    for k in range(4):   # tank hoops
        b.revolve([(3.34, 10.6 + k * 0.8), (3.34, 10.7 + k * 0.8)], steel, seg=12, caps=False)
    b.tube((0.0, -3.3, 0.0), (0.0, -3.3, 10.0), 0.12, steel, seg=6)            # inlet pipe
    return b.finish(collection)


MODELS = {
    "factory_hall": build_hall,
    "factory_warehouse": build_warehouse,
    "smokestack": build_smokestack,
    "water_tower": build_water_tower,
}
