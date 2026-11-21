"""Bab Bhar style buildings and shops for the Avenue Habib Bourguiba map.

Colonial-era Tunis facades: white or pastel walls, tall French windows with blue or green shutters,
wrought-iron balconies, string courses and heavy cornices, crowned by balustrades, pediments, corner domes
or (for the sandstone accent building) the crenellations and horseshoe arches of the Bab Bhar gate.
Rooftops carry the satellite dishes and water tanks of real Tunis roofs; facades carry AC units.

Every building is a filler (bb_building_*) or a shop (shop_*). A shop's ground floor is an open-fronted
walk-in room furnished by bourguiba_interiors.py, with a ShopPoint empty at the counter (or weighing platform)
where the Unity scene builder puts the shop or selling point, and InteriorLight_* empties for its lamps.

Modelled in metres. The facade faces -Y; the origin is on the ground at the middle of the facade, so
buildings line up along a pavement edge. Bodies run back 11 m.
"""

import math
import random

import bpy
from mathutils import Matrix

import mesh_kit as mk

BAY = 3.1            # one window column
EDGE = 0.7           # corner pier
GROUND_H = 4.6       # ground floor (shops) height
FLOOR_H = 3.4        # upper floor height
OPEN_W = BAY - 0.55  # ground floor opening width
OPEN_H = 3.3
DEPTH = 11.0

PALETTES = {
    "white_blue":   dict(wall=(0.90, 0.89, 0.85), trim=(0.97, 0.96, 0.93), shutter=(0.08, 0.33, 0.62), iron=(0.05, 0.14, 0.28)),
    "white_green":  dict(wall=(0.88, 0.88, 0.84), trim=(0.97, 0.96, 0.93), shutter=(0.10, 0.40, 0.28), iron=(0.04, 0.12, 0.08)),
    "cream_blue":   dict(wall=(0.88, 0.81, 0.66), trim=(0.95, 0.92, 0.84), shutter=(0.14, 0.40, 0.66), iron=(0.06, 0.08, 0.12)),
    "yellow_blue":  dict(wall=(0.93, 0.83, 0.55), trim=(0.97, 0.95, 0.88), shutter=(0.08, 0.33, 0.62), iron=(0.06, 0.07, 0.09)),
    "sand_teal":    dict(wall=(0.84, 0.72, 0.53), trim=(0.94, 0.88, 0.74), shutter=(0.18, 0.56, 0.54), iron=(0.05, 0.10, 0.10)),
    "stone":        dict(wall=(0.74, 0.60, 0.41), trim=(0.86, 0.75, 0.56), shutter=(0.36, 0.21, 0.11), iron=(0.07, 0.06, 0.06)),
}
BASE_STONE = (0.66, 0.62, 0.55)
GLASS = (0.10, 0.14, 0.17)
VOID = (0.045, 0.045, 0.05)
SHUTTER_METAL = (0.55, 0.56, 0.57)
WHITE_PLASTIC = (0.90, 0.90, 0.88)

FRONT_UP = Matrix.Rotation(math.pi / 2, 4, "X")   # local XY -> world XZ, local +Z -> world -Y (out of the facade)


class Facade:
    """A Builder plus the palette materials of one building."""

    def __init__(self, name, palette):
        p = PALETTES[palette]
        self.b = b = mk.Builder(name, scale=1.0)
        self.wall = b.mat(f"bb_{palette}_wall", p["wall"], 0.85)
        self.trim = b.mat(f"bb_{palette}_trim", p["trim"], 0.75)
        self.shutter = b.mat(f"bb_{palette}_shutter", p["shutter"], 0.6)
        self.iron = b.mat(f"bb_{palette}_iron", p["iron"], 0.45, 0.5)
        self.base = b.mat("bb_base_stone", BASE_STONE, 0.85)
        self.glass = b.mat("bb_window_glass", GLASS, 0.12, 0.3)
        self.void = b.mat("bb_interior_dark", VOID, 0.95)
        self.metal = b.mat("bb_rolling_shutter", SHUTTER_METAL, 0.5, 0.6)
        self.plastic = b.mat("bb_white_plastic", WHITE_PLASTIC, 0.5)

    def front(self, x, z, w, h, out, mat, y0=0.0):
        """A box on the facade: w wide, h tall (z is its centre), standing out by `out` from y0."""
        self.b.box((x, y0 - out / 2, z), (w, out, h), mat)

    def outline(self, points, out, mat, x=0.0, z=0.0, y0=0.0):
        """A flat shape drawn in facade coordinates (x, z), standing out by `out`."""
        m = Matrix.Translation((x, y0, z)) @ FRONT_UP
        self.b.prism(points, out, mat, m)


# ---------------------------------------------------------------- shapes

def _arch_outline(w, h, horseshoe=1.0, seg=14):
    """Opening w wide and h tall with a round (1.0) or horseshoe (>1) arch, base at 0."""
    r = w / 2 * horseshoe
    phi = math.acos(min(1.0, (w / 2) / r))
    cz = h - r
    jamb = cz - r * math.sin(phi)
    pts = [(-w / 2, 0.0), (w / 2, 0.0), (w / 2, jamb)]
    for i in range(seg + 1):
        a = -phi + (math.pi + 2 * phi) * i / seg
        pts.append((r * math.cos(a), cz + r * math.sin(a)))
    pts.append((-w / 2, jamb))
    return pts, cz, r, phi


def _voussoirs(f, x, y0, w, h, horseshoe, mats, out=0.12, depth=0.3, count=13, z0=0.0):
    """Alternating stones round an arch, like the Bab Bhar gate."""
    _, cz, r, phi = _arch_outline(w, h, horseshoe)
    span = math.pi + 2 * phi
    for i in range(count):
        a = -phi + span * (i + 0.5) / count
        rr = r + depth / 2
        length = span / count * rr * 0.92
        f.b.box((x + rr * math.cos(a), y0 - out / 2, z0 + cz + rr * math.sin(a)), (depth, out, length),
                mats[i % len(mats)], rot=(0, -math.degrees(a) + 90, 0))


def _star(radius, inner=0.5, points=8, rot=0.0):
    return [(math.cos(rot + math.pi * i / points) * (radius if i % 2 == 0 else radius * inner),
             math.sin(rot + math.pi * i / points) * (radius if i % 2 == 0 else radius * inner))
            for i in range(points * 2)]


def _rect(w, h, cx=0.0, cz=0.0):
    return [(cx - w / 2, cz - h / 2), (cx + w / 2, cz - h / 2), (cx + w / 2, cz + h / 2), (cx - w / 2, cz + h / 2)]


# ---------------------------------------------------------------- upper floors
#
# Low-poly, in the spirit of the Quaternius nature models: chunky flat-shaded blocks, thick frames,
# one slab per shutter leaf, solid bars instead of fine ironwork, no dentils, louvres or mullions.

def _window(f, x, z0, rng, style, balcony):
    """A tall French window from z0: a thick frame round a dark pane, two slab shutters, a chunky hood."""
    w, h, fw = 1.2, 2.3 if balcony else 2.0, 0.2
    sill = z0 + (0.12 if balcony else 0.45)
    if style == "arch":
        pts, _, _, _ = _arch_outline(w, h, 1.0, seg=6)
        f.outline(pts, 0.02, f.glass, x=x, z=sill)
        _voussoirs(f, x, 0.0, w, h, 1.0, (f.trim,), out=0.12, depth=0.22, count=5, z0=sill)
    else:
        f.front(x, sill + h / 2, w, h, 0.02, f.glass)
        f.front(x, sill + h + fw / 2, w + 2 * fw, fw, 0.12, f.trim)
        f.front(x, sill + h + fw + 0.12, w + 0.7, 0.24, 0.26, f.trim)                 # hood
    for sx in (-1, 1):
        f.front(x + sx * (w / 2 + fw / 2), sill + h / 2, fw, h, 0.12, f.trim)
    if not balcony:
        f.front(x, sill - 0.1, w + 0.6, 0.2, 0.24, f.trim)

    state = rng.choices(("open", "closed", "half"), (5, 2, 2))[0]
    leaf_w, leaf_h = w / 2, h - 0.08
    for i, sx in enumerate((-1, 1)):
        closed = state == "closed" or (state == "half" and i == 0)
        lx = x + sx * (w / 4) if closed else x + sx * (w / 2 + fw + leaf_w / 2)
        f.front(lx, sill + leaf_h / 2, leaf_w - 0.03, leaf_h, 0.1 if closed else 0.08, f.shutter)
    return sill, w


def _railing(f, x, z, w, depth, iron, y_front):
    """A balcony railing as a few solid bars: a thick top rail, a bottom rail and widely spaced posts."""
    b, top = f.b, 0.95
    b.box((x, y_front + 0.04, z + top), (w, 0.08, 0.08), iron)
    b.box((x, y_front + 0.04, z + 0.14), (w, 0.06, 0.06), iron)
    for sx in (-1, 1):
        b.box((x + sx * (w / 2 - 0.04), y_front + depth / 2, z + top), (0.08, depth, 0.08), iron)
        b.box((x + sx * (w / 2 - 0.04), y_front + 0.04, z + top / 2), (0.08, 0.08, top), iron)
    n = max(2, int(w / 0.32))
    for i in range(1, n):
        b.box((x - w / 2 + w * i / n, y_front + 0.04, z + top / 2), (0.05, 0.05, top - 0.1), iron)


def _balcony(f, x, z, w, rng):
    depth = 0.8
    f.b.box((x, -depth / 2, z - 0.11), (w, depth, 0.22), f.trim)
    f.b.box((x, -0.25, z - 0.32), (w - 0.4, 0.5, 0.2), f.trim, taper=(1.0, 1.6))      # one chunky corbel
    _railing(f, x, z, w - 0.06, depth - 0.06, f.iron, -depth + 0.03)


def _ac_unit(f, x, z):
    f.b.box((x, -0.2, z), (0.8, 0.36, 0.55), f.plastic)
    f.front(x + 0.12, z, 0.38, 0.38, 0.4, f.void)


# ---------------------------------------------------------------- ground floor

def _ground(f, width, bays, kind, rng, xs, shop=None):
    edges = [-width / 2 + EDGE + BAY * i for i in range(bays + 1)]
    f.front(0, 0.2, width + 0.06, 0.4, 0.26, f.base)                              # plinth
    f.front(0, (OPEN_H + 0.15 + GROUND_H - 0.2) / 2, width, GROUND_H - 0.2 - OPEN_H - 0.15, 0.24, f.trim)   # fascia
    f.front(0, GROUND_H - 0.1, width + 0.1, 0.3, 0.34, f.trim)                    # string course
    for i, ex in enumerate(edges):
        pw = EDGE if i in (0, bays) else 0.6
        px = ex - EDGE / 2 if i == 0 else ex + EDGE / 2 if i == bays else ex
        f.front(px, OPEN_H / 2 + 0.15, pw, OPEN_H + 0.3, 0.2, f.trim if kind != "arch" else f.wall)

    for x in xs:
        if kind == "arch":
            pts, _, _, _ = _arch_outline(OPEN_W - 0.3, OPEN_H, 1.12, seg=8)
            f.outline(pts, 0.02, f.void, x=x)
            _voussoirs(f, x, 0.0, OPEN_W - 0.3, OPEN_H, 1.12, (f.trim, f.base), out=0.2, depth=0.4, count=7)
        elif kind == "open":   # shop fronts: nothing in the opening, the shop dresses it
            continue
        elif kind == "glass":
            f.front(x, OPEN_H / 2, OPEN_W, OPEN_H, 0.02, f.glass)
            f.front(x, OPEN_H / 2, 0.12, OPEN_H, 0.08, f.iron)
            f.front(x, 0.4, OPEN_W, 0.8, 0.08, f.trim)                             # stall riser
        else:   # rolling metal shutters, some pulled half up over a dark shop (shops dress their own)
            f.front(x, OPEN_H / 2, OPEN_W, OPEN_H, 0.02, f.void)
            if not shop:
                _rolling_shutter(f, x, rng.choices((0.0, 0.45, 0.85), (4, 3, 2))[0])


def _rolling_shutter(f, x, raised):
    bottom = OPEN_H * raised
    height = OPEN_H - bottom
    if height > 0.05:
        f.front(x, bottom + height / 2, OPEN_W - 0.04, height, 0.08, f.metal)
        f.front(x, bottom + 0.06, OPEN_W - 0.04, 0.12, 0.12, f.metal)              # bottom bar
    f.front(x, OPEN_H + 0.1, OPEN_W + 0.1, 0.3, 0.18, f.metal)                    # shutter box


def _awning(f, x, w, colours, z=OPEN_H + 0.05, reach=1.3, drop=0.55):
    """Striped fabric awning: four wide stripes sloping out from the facade, with a deep valance."""
    b = f.b
    stripes = 4
    length = math.hypot(reach, drop)
    tilt = math.degrees(math.atan2(drop, reach))
    for i in range(stripes):
        sx = x - w / 2 + w * (i + 0.5) / stripes
        b.box((sx, -reach / 2, z - drop / 2), (w / stripes, length, 0.08), colours[i % 2], rot=(-tilt, 0, 0))
        b.box((sx, -reach - 0.02, z - drop - 0.14), (w / stripes, 0.06, 0.3), colours[i % 2])


def text(b, body, mat, size, matrix, depth=0.02):
    """Solid lettering like mesh_kit's Builder.text, but with coarse curves to keep signs low-poly."""
    curve = bpy.data.curves.new("bb_text", "FONT")
    curve.body = body
    curve.font = mk._font(mk.FONT_ROUNDED)
    curve.size = size
    curve.extrude = depth / 2
    curve.align_x = curve.align_y = "CENTER"
    curve.resolution_u = 2
    obj = bpy.data.objects.new("bb_text", curve)
    bpy.context.scene.collection.objects.link(obj)
    mesh = bpy.data.meshes.new_from_object(obj.evaluated_get(bpy.context.evaluated_depsgraph_get()))
    bpy.data.objects.remove(obj)
    bpy.data.curves.remove(curve)
    bm = b._part(mat, sharp=None)
    bm.from_mesh(mesh)
    bpy.data.meshes.remove(mesh)
    for e in bm.edges:
        e.smooth = False
    import bmesh
    bmesh.ops.transform(bm, matrix=matrix, verts=list(bm.verts))
    return bm


def _sign(f, text_body, x, w, board, letters, z=None, size=0.5):
    z = (OPEN_H + 0.15 + GROUND_H - 0.2) / 2 + 0.07 if z is None else z
    f.front(x, z, w, 0.7, 0.12, board, y0=-0.24)
    text(f.b, text_body, letters, size, Matrix.Translation((x, -0.36, z - 0.02)) @ FRONT_UP, depth=0.06)


# ---------------------------------------------------------------- crowns and rooftops

def _crown(f, width, height, kind, rng):
    b = f.b
    cornice_out = 0.55
    b.box((0, (DEPTH - cornice_out) / 2, height + 0.2), (width + 0.8, DEPTH + cornice_out, 0.4), f.trim)
    b.box((0, (DEPTH - 0.3) / 2, height - 0.12), (width + 0.4, DEPTH + 0.3, 0.24), f.trim)
    top = height + 0.4
    if kind == "balustrade":
        b.box((0, DEPTH / 2, top + 0.12), (width, DEPTH, 0.24), f.trim)
        n = max(3, int(width / 1.6))
        for i in range(n + 1):   # chunky posts with a solid rail
            b.box((-width / 2 + 0.2 + (width - 0.4) * i / n, -0.05, top + 0.6), (0.36, 0.36, 0.8), f.trim)
        for i in range(n):
            b.box((-width / 2 + 0.2 + (width - 0.4) * (i + 0.5) / n, -0.05, top + 0.55), ((width - 0.4) / n - 0.36, 0.16, 0.5),
                  f.wall)
        b.box((0, -0.05, top + 1.05), (width, 0.44, 0.14), f.trim)
        return top + 1.1
    if kind == "crenel":
        b.box((0, DEPTH / 2, top + 0.45), (width, DEPTH, 0.9), f.wall)
        n = int(width / 1.1)
        for i in range(n):
            mx = -width / 2 + width * (i + 0.5) / n
            b.box((mx, 0.15, top + 1.2), (0.6, 0.36, 0.6), f.wall)
            b.box((mx, 0.15, top + 1.6), (0.3, 0.36, 0.2), f.wall)
        f.front(0, top + 0.55, width + 0.06, 0.18, 0.12, f.trim)
        return top + 0.9
    # cornice with a solid parapet; "pediment" adds a curved pediment and a corner dome
    b.box((0, DEPTH / 2, top + 0.45), (width, DEPTH, 0.9), f.wall)
    f.front(0, top + 0.85, width + 0.1, 0.16, 0.16, f.trim)
    if kind == "pediment":
        pw = min(width * 0.45, 6.0)
        rad, lift = pw / 2 / math.cos(math.radians(15)), math.sin(math.radians(15))
        pts = [(rad * math.cos(a), rad * (math.sin(a) - lift)) for a in (math.radians(15 + 150 * i / 6) for i in range(7))]
        f.outline(pts, 0.5, f.wall, z=top + 0.9, y0=0.25)
        f.outline(_star(0.45, 0.5, 4, math.pi / 4), 0.08, f.shutter, z=top + 1.5, y0=-0.25)
        dx = width / 2 - 1.6
        b.revolve([(1.25, 0.0), (1.25, 1.3), (1.4, 1.3), (1.4, 1.5)], f.trim, seg=8, matrix=Matrix.Translation((dx, 1.6, top + 0.9)))
        b.revolve([(1.3, 0.0), (1.15, 0.8), (0.65, 1.55), (0.0, 1.9)], f.shutter, seg=8,
                  matrix=Matrix.Translation((dx, 1.6, top + 2.4)))
        b.revolve([(0.08, 0.0), (0.0, 0.7)], f.iron, seg=4, matrix=Matrix.Translation((dx, 1.6, top + 4.25)))
    return top + 0.9


def _rooftop(f, width, roof_z, rng):
    b = f.b
    dish = b.mat("bb_dish_white", (0.86, 0.86, 0.84), 0.8)
    tank = b.mat("bb_water_tank", (0.16, 0.38, 0.62), 0.8)
    for _ in range(rng.randint(1, 2)):
        x, y = rng.uniform(-width / 2 + 1, width / 2 - 1), rng.uniform(2.5, DEPTH - 1.5)
        b.box((x, y, roof_z + 0.5), (0.08, 0.08, 1.0), f.iron)
        m = Matrix.Translation((x, y - 0.1, roof_z + 1.0)) @ Matrix.Rotation(math.radians(-62), 4, "X") \
            @ Matrix.Rotation(math.radians(rng.uniform(-40, 40)), 4, "Y")
        b.revolve([(0.0, 0.1), (0.3, 0.14), (0.5, 0.24), (0.42, 0.24), (0.0, 0.16)], dish, seg=6, matrix=m)
    if rng.random() < 0.8:
        x, y = rng.uniform(-width / 2 + 1.2, width / 2 - 1.2), rng.uniform(4, DEPTH - 2)
        b.box((x, y, roof_z + 0.25), (1.4, 1.0, 0.5), f.iron)
        b.revolve([(0.6, 0.0), (0.6, 1.2), (0.3, 1.45), (0.0, 1.45)], tank, seg=8, matrix=Matrix.Translation((x, y, roof_z + 0.5)))


# ---------------------------------------------------------------- whole buildings

def build_building(collection, name, bays, floors, palette, seed, ground="shutters", crown="cornice",
                   balconies="bay", windows="rect", shop=None):
    rng = random.Random(seed)
    f = Facade(name, palette)
    b = f.b
    width = bays * BAY + 2 * EDGE
    height = GROUND_H + floors * FLOOR_H
    xs = [-width / 2 + EDGE + BAY * (i + 0.5) for i in range(bays)]

    if shop:   # the ground floor is a walk-in room (bourguiba_interiors), the body starts above it
        b.box((0, DEPTH / 2, (GROUND_H + height) / 2), (width, DEPTH, height - GROUND_H), f.wall)
    else:
        b.box((0, DEPTH / 2, height / 2), (width, DEPTH, height), f.wall)
    _ground(f, width, bays, ground, rng, xs, shop)
    for sx in (-1, 1):   # corner pilasters
        f.front(sx * (width / 2 - EDGE / 2), (GROUND_H + height) / 2, EDGE - 0.1, height - GROUND_H, 0.08, f.trim)

    for fl in range(floors):
        z0 = GROUND_H + fl * FLOOR_H
        if fl > 0:
            f.front(0, z0 + 0.05, width - 0.02, 0.16, 0.1, f.trim)
        long_balcony = balconies == "continuous" and fl == 0
        for i, x in enumerate(xs):
            has_balcony = long_balcony or (balconies in ("bay", "continuous") and (fl % 2 == 0 or rng.random() < 0.35))
            sill, w = _window(f, x, z0, rng, windows, has_balcony)
            if has_balcony and not long_balcony:
                _balcony(f, x, sill, w + 0.6, rng)
            elif not has_balcony and balconies == "juliet":
                _railing(f, x, sill, w + 0.1, 0.12, f.iron, -0.16)
            if rng.random() < 0.18 and i < len(xs) - 1:
                _ac_unit(f, x + BAY / 2, z0 + 0.9)
        if long_balcony:
            _balcony(f, 0, z0 + 0.12, width - 0.6, rng)

    roof_z = _crown(f, width, height, crown, rng)
    _rooftop(f, width, roof_z, rng)
    room = None
    if shop:
        import bourguiba_interiors   # imports this module, so load it late
        room = bourguiba_interiors.dress(shop, f, width, xs, rng)
    obj = b.finish(collection)
    obj["bb_width"] = width
    if room:
        _empty(obj, "ShopPoint", (*room["shop_point"], 0.0))
        for i, p in enumerate(room["lights"]):
            _empty(obj, f"InteriorLight_{i}", p)
    return obj


def _empty(parent, name, loc):
    e = bpy.data.objects.new(name, None)
    e.empty_display_type = "ARROWS"
    e.empty_display_size = 0.5
    e.location = loc
    e.parent = parent
    for c in parent.users_collection:
        c.objects.link(e)
    return e


# ---------------------------------------------------------------- catalogue

BUILDINGS = {
    "bb_building_a":  dict(bays=4, floors=4, palette="white_blue", seed=11, crown="cornice", balconies="bay"),
    "bb_building_b":  dict(bays=3, floors=5, palette="white_green", seed=12, crown="balustrade", balconies="continuous"),
    "bb_building_c":  dict(bays=5, floors=3, palette="cream_blue", seed=13, ground="glass", crown="pediment", balconies="bay"),
    "bb_building_d":  dict(bays=3, floors=4, palette="yellow_blue", seed=14, crown="cornice", balconies="juliet"),
    "bb_building_e":  dict(bays=4, floors=5, palette="sand_teal", seed=15, ground="arch", crown="balustrade", balconies="bay"),
    "bb_building_gate": dict(bays=3, floors=2, palette="stone", seed=16, ground="arch", crown="crenel", balconies="none",
                             windows="rect"),
    "shop_cafe":      dict(bays=4, floors=4, palette="white_blue", seed=21, ground="open", crown="pediment", shop="cafe"),
    "shop_tools":     dict(bays=3, floors=4, palette="cream_blue", seed=22, ground="open", crown="cornice", balconies="juliet",
                           shop="tools"),
    "shop_grocery":   dict(bays=3, floors=3, palette="white_green", seed=23, ground="open", crown="balustrade", shop="grocery"),
    "shop_recycling": dict(bays=4, floors=3, palette="white_blue", seed=24, ground="open", crown="cornice",
                           balconies="continuous", shop="recycling"),
}


def _builder(name):
    return lambda collection: build_building(collection, name, **BUILDINGS[name])


MODELS = {name: _builder(name) for name in BUILDINGS}
