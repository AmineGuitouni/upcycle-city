"""Walk-in ground floors for the Avenue Habib Bourguiba shops and the RÉCUP selling point.

Each shop's ground floor is a hollow room behind an open shopfront: tiled floor, a band of Tunisian wall
tiles, a ceiling with lamps, and a counter where the player buys or sells. The props stay in the chunky
flat-shaded style of the rest of the map, but each room is filled out:

  cafe       bar with espresso machine, back-bar shelves and menu board, drinks fridge, tables and
             chairs with glasses of tea and a shisha, a TV showing football, a ceiling fan, a Tunisian flag
  tools      (quincaillerie) counter and till, floor-to-ceiling shelving of paint tins and boxes, a pegboard of
             tools, a pipe rack and hose coils, a barrel of brooms, stacked buckets, a ladder, a wheelbarrow,
             sacks of cement
  grocery    (épicerie) counter with scale, till and sweet jars, wall shelves and a middle gondola of tins,
             bottles and boxes, a drinks fridge, a bread bin of baguettes, open sacks of semolina,
             crisps hanging on a strip, egg trays and water packs
  recycling  (RÉCUP buy-back point) concrete floor with safety lines, four sorting cages in the stream
             colours, stacked bales, a vertical baler, the weighing platform, a price board, an office desk
             with the cash box, pallets and a pallet jack, industrial lamps

dress(kind, f, width, xs, rng) builds a room and returns {"shop_point": (x, y), "lights": [(x, y, z), ...]}.
Shop builders use the same coordinates as bourguiba_buildings: facade on y = 0 facing -Y, room behind it.
"""

import math

from mathutils import Matrix, Vector

import bourguiba_buildings as bb
import mesh_kit as mk

T = Matrix.Translation


def _rz(deg):
    return Matrix.Rotation(math.radians(deg), 4, "Z")


class Room:
    def __init__(self, width):
        self.x0, self.x1 = -width / 2 + 0.35, width / 2 - 0.35
        self.y0, self.y1 = 0.25, bb.DEPTH - 0.35
        self.h = bb.GROUND_H - 0.3
        self.cy = (self.y0 + self.y1) / 2


class Wall:
    """Placement against one inside wall. u runs along the wall (centred, to the right when facing it),
    d out from the wall into the room, z up. Sizes are (along, out, up)."""

    def __init__(self, room, kind):
        self.r, self.kind = room, kind
        self.yaw = {"back": 0.0, "left": 90.0, "right": -90.0}[kind]
        self.length = room.x1 - room.x0 if kind == "back" else room.y1 - room.y0

    def p(self, u, d, z):
        r = self.r
        if self.kind == "back":
            return Vector((u, r.y1 - d, z))
        if self.kind == "left":
            return Vector((r.x0 + d, r.cy + u, z))
        return Vector((r.x1 - d, r.cy - u, z))

    def m(self, u, d, z):
        """Matrix whose local XY is the wall plane (X along, Y up) and local +Z points into the room."""
        return T(self.p(u, d, z)) @ _rz(self.yaw) @ bb.FRONT_UP

    def box(self, b, u, d, z, size, mat, tilt=0.0):
        b.box(self.p(u, d, z), size, mat, rot=(0, tilt, self.yaw))


# ---------------------------------------------------------------- small props

def _cyl(b, p, r, h, mat, seg=8, r1=None):
    r1 = r if r1 is None else r1
    b.revolve([(0.0, 0.0), (r, 0.0), (r1, h), (0.0, h)], mat, seg=seg, matrix=T(p))


def _bottle(b, p, h, mat, seg=6, r=None):
    r = r or h * 0.17
    b.revolve([(0.0, 0.0), (r, 0.0), (r, h * 0.6), (r * 0.42, h * 0.8), (r * 0.42, h), (0.0, h)], mat, seg=seg,
              matrix=T(p))


def _torus(b, matrix, major, minor, mat, seg=10, ring=5):
    prof = [(major + minor * math.cos(2 * math.pi * i / ring), minor * math.sin(2 * math.pi * i / ring))
            for i in range(ring + 1)]
    b.revolve(prof, mat, seg=seg, matrix=matrix, caps=False)


def _chair(b, x, y, yaw, wood):
    R = T((x, y, 0)) @ _rz(yaw)
    for sx in (-1, 1):
        for sy in (-1, 1):
            b.box(R @ Vector((sx * 0.18, sy * 0.18, 0.23)), (0.05, 0.05, 0.46), wood, rot=(0, 0, yaw))
        b.box(R @ Vector((sx * 0.18, 0.19, 0.72)), (0.05, 0.05, 0.5), wood, rot=(0, 0, yaw))
    b.box(R @ Vector((0, 0, 0.48)), (0.44, 0.44, 0.05), wood, rot=(0, 0, yaw))
    b.box(R @ Vector((0, 0.19, 0.86)), (0.44, 0.05, 0.2), wood, rot=(0, 0, yaw))


def _table(b, x, y, top, metal, r=0.4):
    _cyl(b, (x, y, 0.0), 0.24, 0.05, metal)
    _cyl(b, (x, y, 0.05), 0.04, 0.66, metal, seg=6)
    _cyl(b, (x, y, 0.71), r, 0.05, top, seg=10)


def _tea_glass(b, x, y, z, glass, tea):
    b.revolve([(0.0, 0.0), (0.028, 0.0), (0.034, 0.09), (0.0, 0.09)], glass, seg=6, matrix=T((x, y, z)))
    _cyl(b, (x, y, z + 0.005), 0.03, 0.06, tea, seg=6)


def _shisha(b, x, y, z, glass, brass, hose):
    b.revolve([(0.0, 0.0), (0.1, 0.0), (0.12, 0.09), (0.06, 0.2), (0.025, 0.22), (0.0, 0.22)], glass, seg=6,
              matrix=T((x, y, z)))
    b.tube((x, y, z + 0.22), (x, y, z + 0.62), 0.018, brass, seg=5)
    _cyl(b, (x, y, z + 0.5), 0.1, 0.02, brass)
    _cyl(b, (x, y, z + 0.62), 0.045, 0.06, brass, seg=6, r1=0.06)
    b.path([(x + 0.02, y, z + 0.35), (x + 0.22, y - 0.05, z + 0.3), (x + 0.3, y - 0.2, z + 0.1), (x + 0.32, y - 0.35, z + 0.02)],
           0.015, hose, seg=5)


def _shelving(b, wall, u0, u1, levels, depth, height, frame, fill, base=0.0, edge=None):
    """Shelving against a wall from u0 to u1, from z = base: uprights, shelves (with a price-tag strip in the
    edge colour), and fill(level, u0, u1, z, d) for the stock on each shelf."""
    bays = max(1, int((u1 - u0) / 1.2))
    for i in range(bays + 1):
        u = u0 + (u1 - u0) * i / bays
        wall.box(b, u, depth / 2, base + height / 2, (0.06, depth, height), frame)
    wall.box(b, (u0 + u1) / 2, 0.02, base + height / 2, (u1 - u0, 0.04, height), frame)          # back panel
    for k in range(levels):
        z = base + (0.15 + k * (height - 0.3) / (levels - 1) if levels > 1 else 0.15)
        wall.box(b, (u0 + u1) / 2, depth / 2, z, (u1 - u0, depth, 0.04), frame)
        if edge is not None:
            wall.box(b, (u0 + u1) / 2, depth + 0.01, z - 0.02, (u1 - u0, 0.02, 0.07), edge)
        if k < levels - 1:
            fill(k, u0 + 0.08, u1 - 0.08, z + 0.02, depth / 2)


def _ceiling_tubes(b, room, mat, rows=2, cols=2):
    lights = []
    for i in range(cols):
        for j in range(rows):
            x = room.x0 + (room.x1 - room.x0) * (i + 0.5) / cols
            y = room.y0 + 0.6 + (room.y1 - room.y0 - 0.6) * (j + 0.5) / rows
            b.box((x, y, room.h - 0.05), (1.3, 0.22, 0.08), mat)
            lights.append((x, y, room.h - 0.3))
    return lights


def _tunisian_flag(b, wall, u, d, z, red, white):
    m = wall.m(u, d, z)
    b.prism([(-0.6, -0.4), (0.6, -0.4), (0.6, 0.4), (-0.6, 0.4)], 0.03, red, m)
    b.prism([(math.cos(a) * 0.26, math.sin(a) * 0.26) for a in (2 * math.pi * i / 12 for i in range(12))], 0.04, white, m)
    outer = [(math.cos(math.radians(a)) * 0.19, math.sin(math.radians(a)) * 0.19) for a in range(50, 311, 26)]
    inner = [(0.07 + math.cos(math.radians(a)) * 0.15, math.sin(math.radians(a)) * 0.15) for a in range(300, 59, -30)]
    b.prism(outer + inner, 0.05, red, m)
    b.prism(bb._star(0.075, 0.42, 5, math.pi / 2), 0.05, red, m @ T((0.07, 0.0, 0.0)))


# ---------------------------------------------------------------- the shell

def _shell(f, width, floor_a, floor_b, paint, dado, accent, ceiling, checker=True):
    b, r = f.b, Room(width)
    b.box((0, bb.DEPTH / 2, 0.03), (width - 0.1, bb.DEPTH - 0.1, 0.06), floor_a)
    if checker:
        tile = 0.8
        nx, ny = int((r.x1 - r.x0) / tile), int((r.y1 + 0.2) / tile)
        for ix in range(nx):
            for iy in range(ny):
                if (ix + iy) % 2:
                    b.box((r.x0 + tile * (ix + 0.5), 0.05 + tile * (iy + 0.5), 0.065), (tile - 0.02, tile - 0.02, 0.012), floor_b)
    for sx in (-1, 1):
        b.box((sx * (width / 2 - 0.025), bb.DEPTH / 2, bb.GROUND_H / 2), (0.05, bb.DEPTH, bb.GROUND_H), f.wall)
        b.box((sx * (width / 2 - 0.2), bb.DEPTH / 2 + 0.1, bb.GROUND_H / 2), (0.3, bb.DEPTH - 0.2, bb.GROUND_H), paint)
    b.box((0, bb.DEPTH - 0.175, bb.GROUND_H / 2), (width, 0.35, bb.GROUND_H), paint)
    b.box((0, bb.DEPTH / 2, bb.GROUND_H - 0.15), (width - 0.1, bb.DEPTH - 0.1, 0.3), ceiling)
    # tiled dado with a row of diamonds and a border, on the three inside walls
    for kind in ("back", "left", "right"):
        w = Wall(r, kind)
        w.box(b, 0, 0.015, 0.6, (w.length, 0.03, 1.2), dado)
        w.box(b, 0, 0.025, 1.24, (w.length, 0.05, 0.1), accent)
        n = int(w.length / 0.5)
        for i in range(n):
            b.prism([(0, -0.1), (0.1, 0), (0, 0.1), (-0.1, 0)], 0.035, accent, w.m(-w.length / 2 + 0.25 + i * 0.5, 0.0, 0.6))
    return r


def _mats(f, prefix):
    b = f.b
    return dict(
        tile_a=b.mat("bb_int_floor_cream", (0.86, 0.82, 0.72), 0.7),
        tile_b=b.mat("bb_int_floor_terracotta", (0.66, 0.32, 0.20), 0.75),
        ceiling=b.mat("bb_int_ceiling", (0.93, 0.92, 0.88), 0.9),
        dado=b.mat("bb_int_tile_white", (0.92, 0.92, 0.9), 0.35),
        accent=b.mat("bb_int_tile_blue", (0.10, 0.32, 0.62), 0.35),
        lamp=b.mat("bb_int_lamp", (1.0, 0.97, 0.88), 0.3, emission=3.0),
        wood=b.mat("bb_int_wood", (0.45, 0.27, 0.14), 0.7),
        wood_dark=b.mat("bb_int_wood_dark", (0.26, 0.15, 0.08), 0.7),
        metal=b.mat("bb_int_metal", (0.32, 0.33, 0.35), 0.4, 0.7),
        chrome=b.mat("bb_int_chrome", (0.75, 0.76, 0.78), 0.2, 1.0),
        black=b.mat("bb_int_black", (0.05, 0.05, 0.06), 0.5),
        white=b.mat("bb_int_white", (0.95, 0.95, 0.93), 0.5),
        red=b.mat("bb_int_red", (0.80, 0.08, 0.07), 0.5),
        glass=b.mat("bb_int_glass", (0.70, 0.85, 0.85), 0.1, alpha=0.6),
        card=b.mat("bb_int_cardboard", (0.66, 0.48, 0.28), 0.9),
        prods=[b.mat(f"bb_int_prod_{i}", c, 0.55) for i, c in enumerate((
            (0.85, 0.18, 0.10), (0.95, 0.75, 0.10), (0.15, 0.42, 0.80), (0.92, 0.92, 0.88), (0.18, 0.58, 0.30),
            (0.95, 0.45, 0.10), (0.55, 0.20, 0.55)))],
    )


def _counter(b, x, y, length, m, along="x", top=None):
    size = (length, 0.7, 1.0) if along == "x" else (0.7, length, 1.0)
    b.box((x, y, 0.5), size, m["wood"])
    b.box((x, y, 1.03), (size[0] + 0.08, size[1] + 0.08, 0.06), top or m["wood_dark"])


def _till(b, x, y, m, yaw=0.0):
    b.box((x, y, 1.12), (0.42, 0.36, 0.12), m["black"], rot=(0, 0, yaw))
    b.box((x, y + 0.05, 1.22), (0.38, 0.2, 0.1), m["black"], rot=(-20, 0, yaw))
    b.box((x, y + 0.18, 1.35), (0.26, 0.04, 0.14), m["prods"][4], rot=(0, 0, yaw))


def _fridge(b, wall, u, m, prods, height=2.0):
    """Drinks fridge with an open glazed front: a hollow white cabinet with shelves of bottles inside."""
    w, d = 0.9, 0.75
    wall.box(b, u, 0.03, height / 2, (w, 0.06, height), m["black"])                         # back
    for s in (-1, 1):
        wall.box(b, u + s * (w / 2 - 0.03), d / 2, height / 2, (0.06, d, height), m["white"])
    wall.box(b, u, d / 2, 0.08, (w, d, 0.16), m["white"])
    wall.box(b, u, d / 2, height - 0.15, (w, d, 0.3), m["white"])
    wall.box(b, u, d + 0.02, height - 0.15, (w, 0.04, 0.24), m["red"])                      # lit header
    for s in (-1, 1):   # door frame
        wall.box(b, u + s * (w / 2 - 0.02), d + 0.01, height / 2, (0.04, 0.03, height - 0.3), m["chrome"])
    for k in range(4):
        z = 0.16 + k * 0.4
        wall.box(b, u, d / 2, z, (w - 0.12, d - 0.1, 0.03), m["metal"])
        for i in range(4):
            for j in range(2):
                _bottle(b, wall.p(u - 0.28 + i * 0.19, 0.25 + j * 0.25, z + 0.02), 0.3, prods[(k + i) % len(prods)])


# ---------------------------------------------------------------- the cafe

def _cafe(f, width, xs, rng):
    b, m = f.b, _mats(f, "cafe")
    r = _shell(f, width, m["tile_a"], m["tile_b"], b.mat("bb_int_paint_cafe", (0.90, 0.84, 0.66), 0.85),
               m["dado"], m["accent"], m["ceiling"])
    red = b.mat("bb_awning_red", (0.70, 0.10, 0.08), 0.8)
    awning_white = b.mat("bb_awning_white", (0.94, 0.93, 0.90), 0.8)
    for i, x in enumerate(xs):
        bb._awning(f, x, bb.OPEN_W + 0.2, (red, awning_white), reach=1.8, drop=0.7)
        if i in (0, len(xs) - 1):   # window bays: a low wall and a frame; the middle bays are the doors
            f.front(x, 0.45, bb.OPEN_W, 0.9, 0.2, f.trim)
            f.front(x, 0.95, bb.OPEN_W + 0.1, 0.1, 0.3, m["wood_dark"])
    bb._sign(f, "CAFÉ", 0.0, min(width - 1.5, 5.5), b.mat("bb_sign_cafe", (0.12, 0.10, 0.09), 0.6),
             b.mat("bb_sign_gold", (0.90, 0.70, 0.28), 0.4, 0.6), size=0.55)

    back = Wall(r, "back")
    # bar along the back wall: counter, espresso machine, till, cups, bottles on the back bar, a mirror
    bar_y = r.y1 - 1.9
    _counter(b, -0.8, bar_y, 5.0, m)
    for i in range(6):   # tiled bar front
        b.prism([(0, -0.12), (0.12, 0), (0, 0.12), (-0.12, 0)], 0.03, m["accent"], T((-3.0 + i * 0.85, bar_y - 0.36, 0.55)) @ bb.FRONT_UP)
    ex = 0.6
    b.box((ex, bar_y + 0.1, 1.3), (0.8, 0.5, 0.48), m["chrome"])
    b.box((ex, bar_y + 0.1, 1.58), (0.84, 0.54, 0.06), m["black"])
    for gx in (-0.2, 0.2):
        b.box((ex + gx, bar_y - 0.2, 1.36), (0.12, 0.12, 0.1), m["black"])
        b.box((ex + gx, bar_y - 0.32, 1.32), (0.05, 0.22, 0.04), m["black"])
    b.box((ex, bar_y - 0.2, 1.08), (0.7, 0.2, 0.04), m["metal"])
    for i in range(6):
        _cyl(b, (ex - 0.3 + i * 0.12, bar_y + 0.15, 1.61), 0.035, 0.06, m["white"], seg=6)
    _till(b, -2.2, bar_y, m, yaw=180)
    teapot = b.mat("bb_int_brass", (0.78, 0.60, 0.25), 0.3, 0.8)
    for i in range(2):   # brass teapots
        tx = -0.4 + i * 0.4
        b.revolve([(0.0, 0.0), (0.09, 0.0), (0.11, 0.08), (0.08, 0.16), (0.03, 0.2), (0.03, 0.24), (0.0, 0.26)], teapot, seg=8,
                  matrix=T((tx, bar_y + 0.05, 1.06)))
        b.tube((tx - 0.08, bar_y + 0.05, 1.12), (tx - 0.2, bar_y + 0.05, 1.22), 0.015, teapot, seg=5)
    for i in range(5):
        _tea_glass(b, -1.4 + i * 0.16, bar_y - 0.15, 1.06, m["glass"], b.mat("bb_int_tea", (0.55, 0.22, 0.08), 0.3))
    _shelving(b, back, -4.0, 1.4, 3, 0.35, 1.2, m["wood_dark"],
              lambda k, u0, u1, z, d: [_bottle(b, back.p(u0 + 0.12 + i * 0.26, d, z), 0.34, m["prods"][(i + k) % 7])
                                       for i in range(int((u1 - u0) / 0.26))], base=1.2)
    back.box(b, -1.3, 0.04, 3.05, (5.4, 0.05, 0.6), m["glass"])                       # mirror strip
    below = b.mat("bb_int_wood_mid", (0.36, 0.21, 0.10), 0.7)
    back.box(b, -1.3, 0.3, 0.5, (5.4, 0.6, 1.0), below)                               # back-bar cupboards
    for i in range(6):
        back.box(b, -3.75 + i * 0.9, 0.61, 0.5, (0.04, 0.02, 0.8), m["wood_dark"])
    back.box(b, 2.5, 0.04, 2.6, (1.8, 0.06, 1.1), m["black"])                         # menu board
    bb.text(b, "MENU", m["white"], 0.18, back.m(2.5, 0.08, 2.95), depth=0.02)
    for i in range(5):
        back.box(b, 2.35, 0.08, 2.75 - i * 0.16, (1.1, 0.02, 0.05), m["white"])
        back.box(b, 3.2, 0.08, 2.75 - i * 0.16, (0.2, 0.02, 0.05), b.mat("bb_sign_gold", (0.90, 0.70, 0.28), 0.4, 0.6))
    _fridge(b, back, (r.x1 - r.x0) / 2 - 0.6, m, m["prods"])

    # tables, chairs, glasses of tea, a shisha
    hose = b.mat("bb_int_hose", (0.12, 0.30, 0.20), 0.6)
    brass = b.mat("bb_int_brass", (0.78, 0.60, 0.25), 0.3, 0.8)
    spots = [(-4.0, 2.2), (-1.4, 2.4), (1.4, 2.2), (4.0, 2.4), (-4.2, 4.8), (3.8, 5.0)]
    for i, (x, y) in enumerate(spots):
        _table(b, x, y, m["white"] if i % 2 else m["wood"], m["metal"])
        for k in range(3):
            a = k * 120 + i * 25
            _chair(b, x + math.sin(math.radians(a)) * 0.75, y - math.cos(math.radians(a)) * 0.75, a, m["wood_dark"])
        _tea_glass(b, x + 0.12, y, 0.76, m["glass"], b.mat("bb_int_tea", (0.55, 0.22, 0.08), 0.3))
        _tea_glass(b, x - 0.14, y + 0.1, 0.76, m["glass"], b.mat("bb_int_tea", (0.55, 0.22, 0.08), 0.3))
        _cyl(b, (x - 0.05, y - 0.18, 0.76), 0.07, 0.025, m["metal"], seg=6)                  # ashtray
        b.box((x + 0.2, y + 0.18, 0.82), (0.1, 0.05, 0.11), m["white"])                       # napkins
        _cyl(b, (x + 0.02, y + 0.2, 0.76), 0.045, 0.08, m["glass"], seg=6)                    # sugar
        if i in (1, 4):
            _shisha(b, x + 0.05, y - 0.12, 0.76, b.mat("bb_int_shisha_glass", (0.2, 0.5, 0.55), 0.15, alpha=0.8), brass, hose)

    # walls: TV with the match on, the flag, a framed picture of the clock tower
    left, right = Wall(r, "left"), Wall(r, "right")
    left.box(b, 1.5, 0.15, 3.1, (1.4, 0.1, 0.8), m["black"])
    b.prism([(-0.62, -0.34), (0.62, -0.34), (0.62, 0.34), (-0.62, 0.34)], 0.02,
            b.mat("bb_int_tv_pitch", (0.15, 0.6, 0.2), 0.4, emission=1.5), left.m(1.5, 0.2, 3.1))
    b.prism([(-0.01, -0.34), (0.01, -0.34), (0.01, 0.34), (-0.01, 0.34)], 0.03, m["white"], left.m(1.5, 0.2, 3.1))
    left.box(b, 1.5, 0.08, 2.6, (0.3, 0.16, 0.3), m["black"])
    _tunisian_flag(b, right, 1.0, 0.03, 2.6, m["red"], m["white"])
    right.box(b, -2.2, 0.03, 2.4, (0.9, 0.05, 1.2), m["wood_dark"])
    right.box(b, -2.2, 0.06, 2.4, (0.74, 0.03, 1.04), b.mat("bb_int_picture_sky", (0.55, 0.75, 0.92), 0.6))
    right.box(b, -2.2, 0.08, 2.35, (0.12, 0.03, 0.8), b.mat("bb_rust_lattice", (0.34, 0.10, 0.055), 0.6))

    # ceiling fan and lamps
    cx, cy = 0.0, 3.2
    _cyl(b, (cx, cy, r.h - 0.5), 0.02, 0.5, m["metal"], seg=4)
    _cyl(b, (cx, cy, r.h - 0.62), 0.14, 0.14, m["metal"])
    for k in range(4):
        a = k * 90 + 20
        b.box((cx + math.cos(math.radians(a)) * 0.55, cy + math.sin(math.radians(a)) * 0.55, r.h - 0.56),
              (0.9, 0.16, 0.02), m["wood"], rot=(0, 0, a))
    lights = _ceiling_tubes(b, r, m["lamp"], rows=2, cols=3)
    return {"shop_point": (-0.8, bar_y - 1.0), "lights": lights}


# ---------------------------------------------------------------- the hardware shop

def _tools(f, width, xs, rng):
    b, m = f.b, _mats(f, "tools")
    r = _shell(f, width, b.mat("bb_int_floor_grey", (0.6, 0.58, 0.55), 0.8), m["tile_b"],
               b.mat("bb_int_paint_tools", (0.82, 0.86, 0.80), 0.85), m["dado"], b.mat("bb_int_tile_green", (0.12, 0.45, 0.30), 0.35),
               m["ceiling"], checker=False)
    bb._sign(f, "QUINCAILLERIE", 0.0, min(width - 1.0, 7.5), b.mat("bb_sign_tools", (0.95, 0.76, 0.11), 0.6),
             b.mat("bb_sign_dark", (0.08, 0.08, 0.09), 0.6), size=0.44)
    for x in xs:
        bb._rolling_shutter(f, x, 1.0)
    colours = [b.mat(f"bb_basin_{n}", c, 0.5) for n, c in
               (("red", (0.75, 0.12, 0.10)), ("blue", (0.10, 0.35, 0.75)), ("green", (0.15, 0.55, 0.25)), ("yellow", (0.95, 0.75, 0.10)))]
    steel = b.mat("bb_tool_steel", (0.62, 0.63, 0.65), 0.35, 0.8)
    handle = b.mat("bb_tool_handle", (0.55, 0.32, 0.14), 0.7)
    for x in (xs[0], xs[-1]):   # basins and buckets out on the pavement either side of the door
        for k in range(4):
            b.revolve([(0.22, 0.0), (0.3, 0.16), (0.0, 0.16)], colours[k % 4], seg=6, matrix=T((x - 0.5, -0.7, k * 0.12)))
        b.revolve([(0.0, 0.0), (0.14, 0.0), (0.18, 0.36), (0.0, 0.36)], colours[2], seg=6, matrix=T((x + 0.5, -0.6, 0.0)))

    back, left, right = Wall(r, "back"), Wall(r, "right"), Wall(r, "left")

    # floor-to-ceiling shelving on the back wall: paint tins, boxes, rope coils
    def stock(k, u0, u1, z, d):
        u = u0 + 0.15
        i = 0
        while u < u1 - 0.15:
            kind = (i + k) % 3
            if kind == 0:
                _cyl(b, back.p(u, d, z), 0.12, 0.24, steel)
                _cyl(b, back.p(u, d, z + 0.24), 0.12, 0.02, colours[(i + k) % 4])
                u += 0.3
            elif kind == 1:
                back.box(b, u + 0.1, d, z + 0.17, (0.42, 0.4, 0.34), m["card"])
                u += 0.5
            else:
                _torus(b, T(back.p(u + 0.05, d, z + 0.05)), 0.14, 0.05, b.mat("bb_int_rope", (0.85, 0.72, 0.42), 0.8), seg=8)
                u += 0.36
            i += 1
    _shelving(b, back, -(r.x1 - r.x0) / 2 + 0.2, (r.x1 - r.x0) / 2 - 0.2, 5, 0.5, 3.6, m["metal"], stock)

    # pegboard of tools on one side wall
    peg = b.mat("bb_pegboard", (0.62, 0.50, 0.34), 0.8)
    right.box(b, 0.5, 0.03, 2.1, (5.5, 0.05, 1.8), peg)
    for col in range(9):
        for row in range(3):
            u, z = -2.0 + col * 0.6, 2.65 - row * 0.55
            kind = (col + row) % 4
            if kind == 0:   # hammer
                right.box(b, u, 0.08, z - 0.05, (0.05, 0.04, 0.34), handle)
                right.box(b, u, 0.08, z + 0.13, (0.2, 0.05, 0.07), steel)
            elif kind == 1:   # spanner
                right.box(b, u, 0.08, z, (0.04, 0.03, 0.36), steel, tilt=15)
                right.box(b, u + 0.05, 0.08, z + 0.17, (0.1, 0.03, 0.08), steel, tilt=15)
            elif kind == 2:   # saw
                b.prism([(-0.22, -0.04), (0.18, -0.04), (0.18, 0.06), (-0.22, 0.0)], 0.02, steel, right.m(u, 0.06, z))
                right.box(b, u + 0.22, 0.08, z, (0.1, 0.04, 0.12), handle)
            else:   # screwdriver pair
                for s in (-0.06, 0.06):
                    right.box(b, u + s, 0.08, z + 0.08, (0.04, 0.04, 0.14), colours[col % 4])
                    right.box(b, u + s, 0.08, z - 0.08, (0.015, 0.015, 0.2), steel)

    # pipe rack and hose coils on the other side wall
    pipe = b.mat("bb_int_pipe_grey", (0.78, 0.78, 0.76), 0.5)
    for u in (-2.6, -1.0, 0.6):
        left.box(b, u, 0.25, 1.6, (0.08, 0.5, 0.08), m["metal"])
        left.box(b, u, 0.25, 2.2, (0.08, 0.5, 0.08), m["metal"])
    for k in range(6):
        p0, p1 = left.p(-3.2, 0.12 + (k % 3) * 0.12, 1.66 + (k // 3) * 0.6), left.p(1.2, 0.12 + (k % 3) * 0.12, 1.66 + (k // 3) * 0.6)
        b.tube(p0, p1, 0.04, pipe if k % 2 else m["chrome"], seg=6)
    hose = b.mat("bb_int_hose_green", (0.15, 0.55, 0.25), 0.6)
    for i, u in enumerate((2.0, 3.0)):
        _torus(b, left.m(u, 0.12, 2.4), 0.3, 0.06, hose if i == 0 else colours[0], seg=10)

    # counter near the door with the till, and stock on the floor
    cx, cy = r.x1 - 1.6, 2.4
    _counter(b, cx, cy, 2.6, m, along="y")
    _till(b, cx, cy - 0.5, m, yaw=90)
    for i in range(4):   # paint tin pyramid
        for j in range(4 - i):
            _cyl(b, (-1.2 + j * 0.3 + i * 0.15, 5.6, 0.06 + i * 0.25), 0.12, 0.24, colours[(i + j) % 4])
    gx, gy = 0.6, 6.4   # middle display stand, stocked both sides
    b.box((gx, gy, 0.75), (0.08, 2.6, 1.5), m["metal"])
    for side in (-1, 1):
        for k in range(3):
            z = 0.12 + k * 0.5
            b.box((gx + side * 0.25, gy, z), (0.45, 2.6, 0.04), m["metal"])
            b.box((gx + side * 0.48, gy, z - 0.02), (0.02, 2.6, 0.07), colours[3])
            for j in range(6):
                p = (gx + side * 0.25, gy - 1.05 + j * 0.42, z + 0.02)
                if (j + k) % 2:
                    _cyl(b, p, 0.1, 0.2, steel, seg=8)
                    _cyl(b, (p[0], p[1], p[2] + 0.2), 0.1, 0.02, colours[(j + k) % 4], seg=8)
                else:
                    b.box((p[0], p[1], p[2] + 0.12), (0.3, 0.3, 0.24), m["card"])
    for k in range(3):   # spools of chain and rope on a floor rack
        b.tube((-3.6, 4.2 + k * 0.5, 0.6), (-2.6, 4.2 + k * 0.5, 0.6), 0.03, m["metal"], seg=5)
        _torus(b, T((-3.1, 4.2 + k * 0.5, 0.6)) @ Matrix.Rotation(math.pi / 2, 4, "Y"), 0.18, 0.09,
               (b.mat("bb_int_rope", (0.85, 0.72, 0.42), 0.8), steel, colours[0])[k], seg=8)
    for s in (-3.6, -2.6):
        b.box((s, 4.7, 0.35), (0.06, 1.3, 0.7), m["metal"])
    _cyl(b, (r.x0 + 0.8, 1.6, 0.06), 0.3, 0.8, b.mat("bb_int_barrel", (0.15, 0.35, 0.65), 0.6))
    for k in range(5):   # brooms and mops in the barrel
        a = k * 72
        tip = Vector((r.x0 + 0.8 + math.cos(math.radians(a)) * 0.25, 1.6 + math.sin(math.radians(a)) * 0.25, 2.0))
        b.tube((r.x0 + 0.8, 1.6, 0.4), tip, 0.025, handle, seg=5)
        b.box(tip + Vector((0, 0, 0.06)), (0.3, 0.08, 0.12), colours[k % 4])
    for i in range(3):   # bucket stacks
        for k in range(4):
            b.revolve([(0.0, 0.0), (0.14, 0.0), (0.18, 0.34), (0.0, 0.34)], colours[i], seg=6,
                      matrix=T((r.x0 + 0.7 + i * 0.5, 3.4, 0.06 + k * 0.1)))
    # ladder against the back wall, cement sacks, a wheelbarrow
    foot, top = Vector((2.2, r.y1 - 1.45, 0.06)), Vector((2.2, r.y1 - 0.6, 3.2))
    for s in (-0.25, 0.25):
        b.tube(foot + Vector((s, 0, 0)), top + Vector((s, 0, 0)), 0.035, m["wood"], seg=5)
    for k in range(1, 9):
        p = foot.lerp(top, k / 9)
        b.box(p, (0.5, 0.05, 0.05), m["wood"])
    cement = b.mat("bb_int_cement_sack", (0.85, 0.83, 0.78), 0.9)
    for k in range(5):
        b.box((-3.0 + (k % 3) * 0.7, 6.8 + (k // 3) * 0.05, 0.16 + (k // 3) * 0.22), (0.62, 0.42, 0.2), cement, rot=(0, 0, (k * 7) % 11))
    wb = b.mat("bb_int_wheelbarrow", (0.15, 0.40, 0.25), 0.5, 0.4)
    b.box((-0.8, 3.0, 0.55), (0.7, 1.0, 0.3), wb, taper=(1.3, 1.25))
    b.tube((-0.85, 2.4, 0.2), (-0.75, 2.4, 0.2), 0.18, m["black"], seg=8)
    for s in (-0.28, 0.28):
        b.tube((-0.8 + s, 2.6, 0.5), (-0.8 + s * 1.2, 3.9, 0.7), 0.025, m["metal"], seg=5)
    lights = _ceiling_tubes(b, r, m["lamp"], rows=2, cols=2)
    return {"shop_point": (cx - 0.9, cy), "lights": lights}


# ---------------------------------------------------------------- the grocery

def _grocery(f, width, xs, rng):
    b, m = f.b, _mats(f, "grocery")
    r = _shell(f, width, m["tile_a"], b.mat("bb_int_floor_green", (0.25, 0.50, 0.35), 0.7),
               b.mat("bb_int_paint_grocery", (0.95, 0.93, 0.86), 0.85), m["dado"], m["accent"], m["ceiling"])
    bb._sign(f, "ÉPICERIE", 0.0, min(width - 1.5, 6.0), b.mat("bb_sign_grocery", (0.10, 0.40, 0.22), 0.6),
             b.mat("bb_sign_white", (0.96, 0.96, 0.94), 0.5), size=0.5)
    green = b.mat("bb_awning_green", (0.10, 0.45, 0.25), 0.8)
    awning_white = b.mat("bb_awning_white", (0.94, 0.93, 0.90), 0.8)
    for x in xs:
        bb._awning(f, x, bb.OPEN_W + 0.2, (green, awning_white))
        bb._rolling_shutter(f, x, 1.0)
    crate = b.mat("bb_crate_wood", (0.62, 0.45, 0.25), 0.8)
    produce = [b.mat(n, c, 0.55) for n, c in (("bb_oranges", (0.95, 0.5, 0.06)), ("bb_tomatoes", (0.8, 0.12, 0.08)),
                                              ("bb_peppers", (0.2, 0.55, 0.15)), ("bb_lemons", (0.95, 0.85, 0.2)))]
    sx = xs[0]   # produce stand on the pavement
    b.box((sx, -0.8, 0.4), (2.0, 0.9, 0.8), crate)
    for k in range(4):
        cx = sx - 0.75 + k * 0.5
        b.box((cx, -0.8, 0.88), (0.46, 0.8, 0.16), crate)
        b.ellipsoid((cx, -0.8, 0.95), (0.2, 0.36, 0.12), produce[k], seg=6, rings=3, smooth=False)

    back, left, right = Wall(r, "back"), Wall(r, "left"), Wall(r, "right")
    oil = b.mat("bb_int_oil", (0.95, 0.80, 0.20), 0.2)
    water = b.mat("bb_water_pack", (0.55, 0.75, 0.9), 0.15, alpha=0.8)

    def groceries(wall):
        def fill(k, u0, u1, z, d):
            u, i = u0 + 0.12, 0
            while u < u1 - 0.12:
                kind = (i + k * 2) % 4
                if kind == 0:   # tins
                    for s in range(3):
                        _cyl(b, wall.p(u + s * 0.13, d, z), 0.055, 0.13, m["prods"][(k + s) % 7], seg=6)
                    u += 0.45
                elif kind == 1:   # bottles of oil
                    for s in range(3):
                        _bottle(b, wall.p(u + s * 0.12, d, z), 0.32, oil)
                    u += 0.42
                elif kind == 2:   # boxes
                    wall.box(b, u + 0.12, d, z + 0.16, (0.24, 0.2, 0.32), m["prods"][(i + k) % 7])
                    wall.box(b, u + 0.38, d, z + 0.12, (0.22, 0.2, 0.24), m["prods"][(i + k + 3) % 7])
                    u += 0.55
                else:   # jugs of detergent
                    wall.box(b, u + 0.1, d, z + 0.15, (0.18, 0.16, 0.3), m["prods"][2])
                    _cyl(b, wall.p(u + 0.1, d, z + 0.3), 0.03, 0.05, m["red"], seg=6)
                    u += 0.3
                i += 1
        return fill

    tags = b.mat("bb_int_price_tags", (0.95, 0.80, 0.15), 0.5)
    _shelving(b, back, -(r.x1 - r.x0) / 2 + 0.2, (r.x1 - r.x0) / 2 - 1.3, 5, 0.45, 2.8, m["white"], groceries(back), edge=tags)
    _shelving(b, left, -2.8, 2.4, 5, 0.45, 2.4, m["white"], groceries(left), edge=tags)
    _fridge(b, back, (r.x1 - r.x0) / 2 - 0.6, m, [water, m["prods"][0], m["prods"][5]])
    # middle gondola, stocked both sides
    gx, gy = 0.4, 5.0
    b.box((gx, gy, 0.8), (0.1, 3.2, 1.6), m["white"])
    for side in (-1, 1):
        for k in range(3):
            z = 0.2 + k * 0.5
            b.box((gx + side * 0.25, gy, z), (0.45, 3.2, 0.04), m["white"])
            for j in range(8):
                p = (gx + side * 0.25, gy - 1.4 + j * 0.4, z + 0.02)
                if (j + k) % 2:
                    _cyl(b, p, 0.06, 0.14, m["prods"][(j + k + side) % 7], seg=6)
                else:
                    b.box((p[0], p[1], p[2] + 0.14), (0.2, 0.26, 0.28), m["prods"][(j * 3 + k) % 7])
    # counter by the door: scale, till, sweet jars, eggs; bread bin and semolina sacks in front
    cx, cy = r.x1 - 1.5, 2.6
    _counter(b, cx, cy, 2.8, m, along="y")
    _till(b, cx, cy + 0.6, m, yaw=90)
    b.box((cx, cy - 0.4, 1.1), (0.36, 0.3, 0.08), m["white"])
    b.box((cx, cy - 0.4, 1.16), (0.32, 0.26, 0.02), m["chrome"])
    b.box((cx + 0.18, cy - 0.4, 1.2), (0.04, 0.18, 0.1), b.mat("bb_int_display", (0.2, 0.9, 0.4), 0.3, emission=1.5))
    for i in range(3):
        _cyl(b, (cx - 0.15, cy - 1.1 + i * 0.22, 1.06), 0.09, 0.24, m["glass"])
        _cyl(b, (cx - 0.15, cy - 1.1 + i * 0.22, 1.08), 0.08, 0.14, m["prods"][(i * 2) % 7])
    egg = b.mat("bb_int_egg", (0.92, 0.84, 0.70), 0.7)
    for k in range(4):
        b.box((cx, cy + 1.15, 1.08 + k * 0.07), (0.32, 0.32, 0.06), egg)
    wicker = b.mat("bb_int_wicker", (0.70, 0.52, 0.28), 0.9)
    bread = b.mat("bb_int_baguette", (0.86, 0.62, 0.30), 0.8)
    bx, by = r.x1 - 3.2, 1.4
    b.box((bx, by, 0.45), (1.0, 0.6, 0.9), wicker, taper=(1.15, 1.15))
    for k in range(8):
        a = (k - 3.5) * 9
        b.tube((bx - 0.35 + k * 0.1, by, 0.6), (bx - 0.35 + k * 0.1 + math.sin(math.radians(a)) * 0.5, by + 0.05, 1.4), 0.045,
               bread, seg=6)
    sack = b.mat("bb_woven_sack", (0.82, 0.78, 0.66), 0.9)
    grains = [b.mat(n, c, 0.9) for n, c in (("bb_int_semolina", (0.93, 0.80, 0.45)), ("bb_int_couscous", (0.88, 0.72, 0.40)),
                                            ("bb_int_lentils", (0.55, 0.30, 0.15)))]
    for i in range(3):
        x = r.x0 + 0.6 + i * 0.75
        b.box((x, 1.3, 0.35), (0.6, 0.6, 0.7), sack, taper=(1.1, 1.1))
        b.box((x, 1.3, 0.72), (0.58, 0.58, 0.06), grains[i])
    b.box((r.x0 + 0.6, 1.3, 0.8), (0.12, 0.22, 0.08), m["chrome"])                     # scoop
    # crisps hanging on a strip by the counter
    b.tube((cx - 0.9, cy - 1.4, r.h), (cx - 0.9, cy - 1.4, r.h - 1.6), 0.01, m["metal"], seg=4)
    for k in range(6):
        b.box((cx - 0.9, cy - 1.4, r.h - 0.25 - k * 0.24), (0.22, 0.05, 0.2), m["prods"][k % 7])
    for k in range(3):   # water packs
        b.box((r.x1 - 3.2, 3.2, 0.2 + k * 0.34), (0.5, 0.34, 0.33), water)
    lights = _ceiling_tubes(b, r, m["lamp"], rows=2, cols=2)
    return {"shop_point": (cx - 0.9, cy), "lights": lights}


# ---------------------------------------------------------------- the RÉCUP selling point

STREAMS = (("PAPIER", (0.18, 0.44, 0.71)), ("PLASTIQUE", (0.95, 0.71, 0.11)), ("VERRE", (0.18, 0.55, 0.34)),
           ("CANETTES", (0.78, 0.27, 0.23)))


def _recycling(f, width, xs, rng):
    b, m = f.b, _mats(f, "recup")
    concrete = b.mat("bb_int_concrete", (0.55, 0.55, 0.53), 0.95)
    r = _shell(f, width, concrete, concrete, b.mat("bb_int_paint_depot", (0.80, 0.84, 0.82), 0.9),
               b.mat("bb_int_wall_green", (0.18, 0.45, 0.30), 0.7), b.mat("bb_int_safety_yellow", (0.95, 0.76, 0.11), 0.6),
               m["ceiling"], checker=False)
    green = b.mat("bb_company_green", (0.18, 0.55, 0.34), 0.6)
    white = b.mat("bb_sign_white", (0.96, 0.96, 0.94), 0.5)
    yellow = b.mat("bb_int_safety_yellow", (0.95, 0.76, 0.11), 0.6)
    bb._sign(f, "RÉCUP", -0.8, min(width - 1.5, 6.0), green, white, size=0.6)
    mk.recycle_icon(b, white, T((min(width - 1.5, 6.0) / 2 - 1.5, -0.37, (bb.OPEN_H + bb.GROUND_H - 0.05) / 2 + 0.07)) @ bb.FRONT_UP,
                    radius=0.26, width=0.09, depth=0.04)
    for x in xs:
        bb._rolling_shutter(f, x, 1.0)
    sack = b.mat("bb_woven_sack", (0.82, 0.78, 0.66), 0.9)
    band = b.mat("bb_sack_band", (0.16, 0.38, 0.70), 0.8)
    for x in (xs[0] - 0.9, xs[-1] + 0.9):   # sacks waiting outside, by the end piers
        b.box((x, -0.75, 0.5), (0.8, 0.8, 1.0), sack, taper=(0.85, 0.85))
        b.box((x, -0.75, 0.62), (0.84, 0.84, 0.14), band)
    streams = [b.mat(f"bb_bale_{n.lower()}", c, 0.7) for n, c in STREAMS]

    # safety lines on the floor round the weighing area
    sx, sy = -1.0, 2.5
    for dx, dy, w, d in ((0, -1.3, 2.68, 0.08), (0, 1.3, 2.68, 0.08), (-1.3, 0, 0.08, 2.6), (1.3, 0, 0.08, 2.6)):
        b.box((sx + dx, sy + dy, 0.07), (w, d, 0.01), yellow)
    # the weighing platform (the selling point) with its display post
    b.box((sx, sy, 0.1), (1.6, 1.6, 0.16), m["metal"])
    b.box((sx, sy, 0.19), (1.5, 1.5, 0.02), b.mat("bb_scale_plate", (0.45, 0.47, 0.5), 0.4, 0.7))
    b.box((sx + 1.0, sy + 0.6, 0.7), (0.1, 0.1, 1.3), m["metal"])
    b.box((sx + 1.0, sy + 0.55, 1.45), (0.5, 0.12, 0.34), m["black"])
    b.box((sx + 1.0, sy + 0.48, 1.47), (0.4, 0.02, 0.2), b.mat("bb_int_display", (0.2, 0.9, 0.4), 0.3, emission=1.5))

    # four sorting cages along the back wall, each with its stream colour, a label and a heap inside
    back = Wall(r, "back")
    cage_w = (r.x1 - r.x0 - 1.0) / 4
    for i, (label, _) in enumerate(STREAMS):
        u = -(r.x1 - r.x0) / 2 + 0.5 + cage_w * (i + 0.5)
        back.box(b, u, 0.9, 0.55, (cage_w - 0.4, 1.4, 1.0), streams[i])                 # heap
        for s in (-1, 1):
            back.box(b, u + s * (cage_w / 2 - 0.1), 1.6, 0.9, (0.08, 0.08, 1.8), m["metal"])
            back.box(b, u + s * (cage_w / 2 - 0.1), 0.9, 1.78, (0.08, 1.4, 0.06), m["metal"])
        back.box(b, u, 1.6, 1.78, (cage_w - 0.2, 0.06, 0.06), m["metal"])
        back.box(b, u, 1.6, 0.06, (cage_w - 0.2, 0.06, 0.12), m["metal"])
        for k in range(7):   # mesh bars on the front
            back.box(b, u - cage_w / 2 + 0.25 + k * (cage_w - 0.5) / 6, 1.6, 0.9, (0.03, 0.03, 1.7), m["metal"])
        back.box(b, u, 1.66, 2.1, (cage_w - 0.6, 0.05, 0.4), streams[i])
        bb.text(b, label, white, 0.22, back.m(u, 1.7, 2.08), depth=0.02)

    # bales stacked on the left, a vertical baler on the right
    left, right = Wall(r, "left"), Wall(r, "right")
    for k in range(6):
        u, z = -1.6 + (k % 3) * 1.25, 0.55 + (k // 3) * 1.1
        left.box(b, u, 0.6, z, (1.15, 1.0, 1.05), streams[(k * 3) % 4])
        for s in (-0.3, 0.3):
            left.box(b, u + s, 0.6, z, (0.05, 1.02, 1.07), m["black"])
    bx = -1.6
    right.box(b, bx, 0.8, 1.6, (1.6, 1.2, 3.2), green)
    right.box(b, bx, 1.41, 0.9, (1.2, 0.02, 1.0), m["black"])                          # loading mouth
    right.box(b, bx, 1.42, 2.1, (1.3, 0.04, 0.7), b.mat("bb_scale_plate", (0.45, 0.47, 0.5), 0.4, 0.7))
    _cyl(b, right.p(bx, 0.8, 3.2), 0.18, 0.9, m["chrome"])
    right.box(b, bx + 1.0, 0.3, 1.4, (0.36, 0.2, 0.5), yellow)
    for k, col in enumerate((m["red"], green)):
        b.revolve([(0.0, 0.0), (0.05, 0.0), (0.05, 0.04), (0.0, 0.04)], col, seg=6,
                  matrix=right.m(bx + 1.0, 0.4, 1.5 - k * 0.15))

    # price board and the office desk with the cash box
    right.box(b, 1.6, 0.03, 2.3, (1.6, 0.05, 1.2), m["white"])
    bb.text(b, "PRIX", m["black"], 0.2, right.m(1.6, 0.07, 2.72), depth=0.02)
    for i in range(4):
        right.box(b, 1.05, 0.07, 2.4 - i * 0.24, (0.16, 0.03, 0.16), streams[i])
        right.box(b, 1.75, 0.07, 2.4 - i * 0.24, (0.9, 0.03, 0.06), m["black"])
    dx, dy = r.x1 - 1.4, 5.2
    b.box((dx, dy, 0.75), (1.6, 0.8, 0.06), m["wood"])
    for sx2 in (-0.7, 0.7):
        b.box((dx + sx2, dy, 0.37), (0.06, 0.7, 0.74), m["metal"])
    b.box((dx - 0.3, dy, 0.82), (0.36, 0.26, 0.08), b.mat("bb_int_cashbox", (0.65, 0.12, 0.10), 0.5, 0.4))
    b.box((dx + 0.3, dy + 0.05, 0.8), (0.4, 0.3, 0.04), b.mat("bb_int_ledger", (0.20, 0.30, 0.55), 0.7))
    _chair(b, dx, dy + 0.75, 180, m["metal"])
    # pallets, a pallet jack, a hi-vis vest on a hook
    pallet = b.mat("bb_int_pallet", (0.72, 0.56, 0.36), 0.85)
    for k in range(4):
        z = 0.07 + k * 0.15
        for s in range(5):
            b.box((1.8, 6.6 - 0.45 + s * 0.22, z + 0.06), (1.2, 0.12, 0.02), pallet)
        for s in (-0.5, 0.0, 0.5):
            b.box((1.8 + s, 6.6, z), (0.1, 1.0, 0.1), pallet)
    for s in (-0.18, 0.18):
        b.box((0.2 + s, 5.6, 0.06), (0.16, 1.2, 0.08), m["red"])
    b.box((0.2, 4.95, 0.3), (0.5, 0.15, 0.5), m["red"])
    b.tube((0.2, 4.9, 0.5), (0.2, 4.5, 1.3), 0.025, m["black"], seg=5)
    vest = b.mat("bb_int_vest", (1.0, 0.48, 0.10), 0.6)
    b.prism([(-0.25, -0.35), (0.25, -0.35), (0.25, 0.25), (0.08, 0.35), (-0.08, 0.35), (-0.25, 0.25)], 0.04, vest,
            right.m(3.2, 0.06, 1.9))
    right.box(b, 3.2, 0.1, 1.75, (0.5, 0.02, 0.07), yellow)

    # industrial lamps
    shade = b.mat("bb_int_lamp_shade", (0.15, 0.40, 0.28), 0.5, 0.3)
    lights = []
    for x in (-3.0, 0.0, 3.0):
        for y in (2.5, 6.0):
            b.tube((x, y, r.h), (x, y, r.h - 0.8), 0.01, m["black"], seg=4)
            b.revolve([(0.08, 0.0), (0.4, -0.3), (0.38, -0.32), (0.0, -0.05)], shade, seg=8, matrix=T((x, y, r.h - 0.8)))
            _cyl(b, (x, y, r.h - 1.12), 0.08, 0.06, m["lamp"], seg=6)
            lights.append((x, y, r.h - 1.3))
    return {"shop_point": (sx, sy), "lights": lights}


ROOMS = {"cafe": _cafe, "tools": _tools, "grocery": _grocery, "recycling": _recycling}


def dress(kind, f, width, xs, rng):
    return ROOMS[kind](f, width, xs, rng)
