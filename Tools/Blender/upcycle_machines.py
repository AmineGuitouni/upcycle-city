"""Procedural low-poly manual recycling & crafting machines for Upcycle City.

Each model is built in metres (origin on the ground at (0, 0, 0), front facing -Y in Blender -> +Z in Unity),
with an upcycled, hand-powered mechanical look matching the Bourguiba & UpcycleProps palette.

Named child objects / empties exported with every machine so Unity drives animations, hoppers & upgrades:
  HopperZone     empty at the centre of the intake hopper opening (catch trigger anchor)
  OutputPoint    empty at the front chute / build deck (spawn / particle anchor)
  SteamPoint     empty at the chimney / vent (steam / sparks while running)
  Upgrade_Lvl1   mesh child containing Level 1+ upgrade parts (hidden at Lv 0, shown at Lv >= 1)
  GlowMesh       mesh child whose emissive material pulses while processing
  CrankPivot / FlywheelPivot / PedalPivot / RotorPivot / RollerPivotA / RollerPivotB / BellowsPivot / DynamoPivot
                 pivoted child meshes animated in Unity while the machine is processing.

Machines (6):
  machine_pulper           Hand-crank Shredder / Pulper Vat (Paper & Wood refining)
  machine_plastic_melter   Pedal-driven Plastic Melter & Extruder (Plastic refining)
  machine_crucible_furnace Bellows Crucible Furnace (Aluminum smelting, Lv 1)
  machine_tyre_stripper    Tyre Stripper & Thermal Separator (Old Wheel breakdown, Lv 1)
  machine_workbench        Upcycle Assembly Workbench (Building cards)
  machine_pedal_generator  Pedal & Crank Energy Generator (Manual NRG source)
"""

import math
import bpy
from mathutils import Matrix, Vector

import mesh_kit as mk

# ---------------------------------------------------------------- palette (AGENTS.md stream & brand colours)

PAPER_BLUE = (0.180, 0.435, 0.710)      # #2E6FB5
PLASTIC_YELLOW = (0.949, 0.710, 0.106)  # #F2B51B
COMPANY_GREEN = (0.180, 0.545, 0.341)   # #2E8B57
CANS_RED = (0.784, 0.275, 0.231)        # #C8463B
TEAL = (0.184, 0.561, 0.541)            # #2F8F8A
TEAL_DARK = (0.11, 0.34, 0.36)
SAFETY_YELLOW = (0.949, 0.761, 0.106)   # #F2C21B
RUST = (0.706, 0.337, 0.180)            # #B4562E
HIVIS_ORANGE = (1.0, 0.478, 0.102)      # #FF7A1A

STEEL = (0.48, 0.51, 0.54)
DARK_METAL = (0.17, 0.19, 0.21)
BRASS = (0.80, 0.63, 0.24)
COPPER = (0.76, 0.40, 0.22)
CAST_IRON = (0.14, 0.14, 0.16)
WOOD = (0.64, 0.46, 0.27)
WOOD_DARK = (0.42, 0.28, 0.16)
LEATHER = (0.48, 0.26, 0.15)
BRICK = (0.58, 0.25, 0.17)
CONCRETE = (0.56, 0.55, 0.52)
RUBBER = (0.09, 0.09, 0.10)
GLASS = (0.55, 0.78, 0.85)
CREAM = (0.90, 0.88, 0.82)
EMBER_GLOW = (1.0, 0.42, 0.08)
LAMP_GLOW = (0.98, 0.90, 0.55)
CHARGE_GLOW = (0.28, 0.95, 0.52)


def _empty(name, parent, location):
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.12
    e.location = location
    e.parent = parent
    parent.users_collection[0].objects.link(e)
    return e


def _child(obj, parent, location=(0, 0, 0)):
    obj.parent = parent
    obj.location = location
    return obj


def _spoked_wheel(b, center, radius, rim_r, spokes, mat_rim, mat_hub, axis="X"):
    """Build a chunky spoked flywheel/handwheel centered at `center` with axle along X or Y."""
    cx, cy, cz = center
    rot = Matrix.Rotation(math.pi / 2, 4, "Y") if axis == "X" else Matrix.Rotation(math.pi / 2, 4, "X")
    base = Matrix.Translation((cx, cy, cz)) @ rot
    # Rim via torus-like revolved profile
    prof = [
        (radius - rim_r, -rim_r * 0.8),
        (radius + rim_r, -rim_r * 0.8),
        (radius + rim_r, rim_r * 0.8),
        (radius - rim_r, rim_r * 0.8),
        (radius - rim_r, -rim_r * 0.8),
    ]
    b.revolve(prof, mat_rim, seg=18, matrix=base, caps=False)
    # Hub
    b.revolve([(0, -rim_r * 1.4), (rim_r * 2.0, -rim_r * 1.4), (rim_r * 2.0, rim_r * 1.4), (0, rim_r * 1.4)],
              mat_hub, seg=12, matrix=base)
    # Spokes
    for i in range(spokes):
        a = 2 * math.pi * i / spokes
        if axis == "X":
            p1 = (cx, cy + math.cos(a) * (radius - rim_r * 0.3), cz + math.sin(a) * (radius - rim_r * 0.3))
        else:
            p1 = (cx + math.cos(a) * (radius - rim_r * 0.3), cy, cz + math.sin(a) * (radius - rim_r * 0.3))
        b.tube(center, p1, rim_r * 0.42, mat_hub, seg=6)


def _hopper_funnel(b, center, top_size, bot_size, height, wall_t, mat_body, mat_rim):
    """Open-top flared rectangular intake hopper centered at `center` (bottom at center.z - height/2)."""
    cx, cy, cz = center
    tx, ty = top_size
    bx, by = bot_size
    flare_x = math.degrees(math.atan2((tx - bx) / 2, height))
    flare_y = math.degrees(math.atan2((ty - by) / 2, height))
    mx = (tx + bx) / 4
    my = (ty + by) / 4
    hx = height / max(0.3, math.cos(math.radians(flare_x)))
    hy = height / max(0.3, math.cos(math.radians(flare_y)))
    for s in (-1, 1):
        b.box((cx + s * mx, cy, cz), (wall_t, (ty + by) / 2 + wall_t, hx), mat_body, rot=(0, s * flare_x, 0))
        b.box((cx, cy + s * my, cz), ((tx + bx) / 2 + wall_t, wall_t, hy), mat_body, rot=(-s * flare_y, 0, 0))
    # Safety rim lip
    top_z = cz + height / 2 + 0.02
    for s in (-1, 1):
        b.box((cx, cy + s * (ty / 2 + wall_t * 0.4), top_z), (tx + wall_t * 2.2, wall_t * 1.5, 0.045), mat_rim, bevel=0.008)
        b.box((cx + s * (tx / 2 + wall_t * 0.4), cy, top_z), (wall_t * 1.5, ty + wall_t * 2.2, 0.045), mat_rim, bevel=0.008)


def _pressure_gauge(b, pos, mat_brass, mat_cream, mat_glass, facing="-Y"):
    """Small circular analog gauge with brass bezel and white dial."""
    rot = Matrix.Rotation(math.pi / 2, 4, "X") if facing == "-Y" else Matrix.Rotation(math.pi / 2, 4, "Y")
    m = Matrix.Translation(pos) @ rot
    b.revolve([(0, -0.025), (0.085, -0.025), (0.085, 0.025), (0, 0.025)], mat_brass, seg=14, matrix=m)
    b.revolve([(0, 0.024), (0.068, 0.024), (0.068, 0.030), (0, 0.030)], mat_cream, seg=14, matrix=m)
    b.revolve([(0, 0.030), (0.070, 0.030), (0.070, 0.034), (0, 0.034)], mat_glass, seg=14, matrix=m)


# ================================================================ 1. SHREDDER / PULPER VAT

def build_pulper(collection):
    b = mk.Builder("machine_pulper", scale=1.0)
    blue = b.mat("mach_paper_blue", PAPER_BLUE, 0.5)
    teal = b.mat("mach_teal", TEAL, 0.48)
    steel = b.mat("mach_steel", STEEL, 0.4, 0.65)
    dark = b.mat("mach_dark_metal", DARK_METAL, 0.55, 0.5)
    wood = b.mat("mach_wood", WOOD, 0.8)
    yellow = b.mat("mach_safety_yellow", SAFETY_YELLOW, 0.42)
    copper = b.mat("mach_copper_metal", COPPER, 0.35, 0.75)

    # Heavy timber & steel base skid
    b.box((0, 0, 0.06), (1.55, 1.32, 0.12), dark, bevel=0.015)
    for sx in (-0.64, 0.64):
        for sy in (-0.52, 0.52):
            b.box((sx, sy, 0.46), (0.09, 0.09, 0.72), steel, bevel=0.01)
    # Cross braces
    for sy in (-0.52, 0.52):
        b.box((0, sy, 0.26), (1.28, 0.06, 0.06), steel)
        b.box((0, sy, 0.76), (1.28, 0.06, 0.06), steel)
    for sx in (-0.64, 0.64):
        b.box((sx, 0, 0.26), (0.06, 1.04, 0.06), steel)

    # Riveted cylindrical pulping vat (open top so paddles show)
    b.revolve([
        (0.0, 0.30), (0.56, 0.30), (0.58, 0.36), (0.58, 1.18),
        (0.52, 1.18), (0.52, 0.40), (0.0, 0.40),
    ], blue, seg=18)
    # Teal reinforcement hoops around the vat
    for z in (0.48, 0.78, 1.08):
        b.revolve([(0.585, z - 0.03), (0.605, z - 0.03), (0.605, z + 0.03), (0.585, z + 0.03)],
                  teal, seg=18, caps=False)
    # Front wooden slat jacket accent
    for k in range(5):
        ang = math.radians(-135 + k * 22.5)
        x = math.cos(ang) * 0.60
        y = math.sin(ang) * 0.60
        b.box((x, y, 0.78), (0.10, 0.03, 0.68), wood, rot=(0, 0, math.degrees(ang) + 90))

    # Wide flared intake hopper perched slightly back-top
    _hopper_funnel(b, (0, 0.08, 1.38), (0.86, 0.78), (0.56, 0.52), 0.36, 0.04, teal, yellow)

    # Front pulp discharge trough & spigot
    b.box((0, -0.68, 0.56), (0.34, 0.36, 0.05), steel, rot=(14, 0, 0))
    for sx in (-0.16, 0.16):
        b.box((sx, -0.68, 0.63), (0.04, 0.36, 0.12), steel, rot=(14, 0, 0))
    b.tube((0, -0.52, 0.66), (0, -0.66, 0.62), 0.06, copper, seg=10)
    # Catch bucket sitting in front
    b.revolve([(0, 0.12), (0.20, 0.12), (0.24, 0.42), (0.21, 0.42), (0.17, 0.16), (0, 0.16)],
              teal, seg=14, matrix=Matrix.Translation((0, -0.78, 0)))

    # Side gearbox housing (drives crank -> internal rotor)
    b.box((0.68, 0, 0.88), (0.20, 0.30, 0.30), dark, bevel=0.02)
    b.tube((0.58, 0, 0.88), (0.82, 0, 0.88), 0.045, steel, seg=10)
    # Small exhaust steam vent pipe on back-left
    b.tube((-0.44, 0.38, 1.12), (-0.44, 0.38, 1.62), 0.055, dark, seg=10)
    b.revolve([(0.055, 1.60), (0.095, 1.64), (0.095, 1.68), (0.055, 1.68)], steel, seg=10,
              matrix=Matrix.Translation((-0.44, 0.38, 0)))

    root = b.finish(collection)

    # Moving Part 1: Hand Crank on right side (spins about X)
    bc = mk.Builder("CrankPivot", scale=1.0)
    c_steel = bc.mat("mach_steel", STEEL, 0.4, 0.65)
    c_wood = bc.mat("mach_wood", WOOD, 0.8)
    c_orange = bc.mat("mach_hivis_orange", HIVIS_ORANGE, 0.45)
    bc.revolve([(0, -0.03), (0.08, -0.03), (0.08, 0.04), (0, 0.04)], c_steel, seg=12,
               matrix=Matrix.Rotation(math.pi / 2, 4, "Y"))
    bc.box((0.05, 0, 0.16), (0.045, 0.065, 0.34), c_orange, bevel=0.008)
    bc.tube((0.05, 0, 0.31), (0.24, 0, 0.31), 0.028, c_wood, seg=10)
    bc.revolve([(0, 0.22), (0.038, 0.22), (0.038, 0.25), (0, 0.25)], c_steel, seg=10,
               matrix=Matrix.Translation((0, 0, 0.31)) @ Matrix.Rotation(math.pi / 2, 4, "Y"))
    _child(bc.finish(collection), root, (0.80, 0.0, 0.88))

    # Moving Part 2: Internal shredding paddles (spins about Z in Blender -> Y in Unity)
    br = mk.Builder("RotorPivot", scale=1.0)
    r_steel = br.mat("mach_steel", STEEL, 0.4, 0.65)
    r_dark = br.mat("mach_dark_metal", DARK_METAL, 0.5, 0.6)
    br.tube((0, 0, -0.35), (0, 0, 0.22), 0.05, r_steel, seg=10)
    for k in range(4):
        ang = k * 90
        rad = math.radians(ang)
        z_off = -0.18 + (k % 2) * 0.18
        br.box((math.cos(rad) * 0.22, math.sin(rad) * 0.22, z_off), (0.38, 0.045, 0.11), r_dark, rot=(18, 0, ang))
    _child(br.finish(collection), root, (0, 0, 0.88))

    # Glow indicator lamp on front panel
    bg = mk.Builder("GlowMesh", scale=1.0)
    g_lamp = bg.mat("mach_lamp_glow", CHARGE_GLOW, 0.25, emission=2.2)
    bg.ellipsoid((0, -0.60, 1.02), (0.055, 0.04, 0.055), g_lamp, seg=10, rings=6)
    _child(bg.finish(collection), root)

    # Level 1 Upgrade parts: Copper steam boiler tank + pressure gauge + tall hopper collar
    bu = mk.Builder("Upgrade_Lvl1", scale=1.0)
    u_copper = bu.mat("mach_copper_metal", COPPER, 0.32, 0.78)
    u_brass = bu.mat("mach_brass_metal", BRASS, 0.35, 0.78)
    u_cream = bu.mat("mach_cream", CREAM, 0.5)
    u_glass = bu.mat("mach_glass", GLASS, 0.1, 0.1)
    u_yellow = bu.mat("mach_safety_yellow", SAFETY_YELLOW, 0.42)
    # Auxiliary steam tank on left
    bu.revolve([(0, 0.24), (0.22, 0.24), (0.24, 0.30), (0.24, 1.05), (0.18, 1.15), (0, 1.18)],
               u_copper, seg=14, matrix=Matrix.Translation((-0.78, -0.06, 0)))
    for z in (0.42, 0.88):
        bu.revolve([(0.245, z - 0.025), (0.26, z - 0.025), (0.26, z + 0.025), (0.245, z + 0.025)],
                   u_brass, seg=14, caps=False, matrix=Matrix.Translation((-0.78, -0.06, 0)))
    # Connecting pipe into vat
    bu.path([(-0.78, -0.06, 1.15), (-0.78, -0.06, 1.32), (-0.42, -0.06, 1.32), (-0.42, -0.06, 1.12)], 0.036, u_brass, seg=8)
    _pressure_gauge(bu, (-0.78, -0.31, 0.88), u_brass, u_cream, u_glass, facing="-Y")
    # Extended hopper safety guard collar
    _hopper_funnel(bu, (0, 0.08, 1.66), (0.98, 0.88), (0.86, 0.78), 0.20, 0.035, u_brass, u_yellow)
    _child(bu.finish(collection), root)

    _empty("HopperZone", root, (0, 0.08, 1.48))
    _empty("OutputPoint", root, (0, -0.82, 0.45))
    _empty("SteamPoint", root, (-0.44, 0.38, 1.72))
    return root


# ================================================================ 2. PEDAL PLASTIC MELTER & EXTRUDER

def build_plastic_melter(collection):
    b = mk.Builder("machine_plastic_melter", scale=1.0)
    yellow = b.mat("mach_plastic_yellow", PLASTIC_YELLOW, 0.42)
    orange = b.mat("mach_hivis_orange", HIVIS_ORANGE, 0.45)
    teal = b.mat("mach_teal", TEAL, 0.48)
    steel = b.mat("mach_steel", STEEL, 0.38, 0.7)
    dark = b.mat("mach_dark_metal", DARK_METAL, 0.5, 0.55)
    rubber = b.mat("mach_rubber", RUBBER, 0.85)
    copper = b.mat("mach_copper_metal", COPPER, 0.35, 0.75)

    # Welded steel skid base
    b.box((0, 0, 0.05), (1.88, 1.15, 0.10), dark, bevel=0.015)

    # Left side: repurposed bicycle pedal station welded to skid
    bx = -0.58
    b.tube((bx, -0.22, 0.10), (bx, -0.10, 0.82), 0.032, teal, seg=8)    # seat post tube
    b.tube((bx, 0.28, 0.10), (bx, 0.18, 0.92), 0.032, teal, seg=8)      # head tube
    b.tube((bx, -0.10, 0.72), (bx, 0.20, 0.82), 0.028, teal, seg=8)     # top tube
    b.tube((bx, -0.02, 0.38), (bx, 0.20, 0.78), 0.028, teal, seg=8)     # down tube
    b.tube((bx, -0.18, 0.38), (bx, -0.02, 0.38), 0.028, dark, seg=8)    # bottom bracket stay
    # Saddle + handlebars
    b.box((bx, -0.12, 0.88), (0.16, 0.28, 0.06), rubber, bevel=0.02)
    b.tube((bx - 0.24, 0.20, 0.98), (bx + 0.24, 0.20, 0.98), 0.022, steel, seg=8)
    for s in (-1, 1):
        b.tube((bx + s * 0.16, 0.20, 0.98), (bx + s * 0.27, 0.20, 0.98), 0.030, orange, seg=8)
    # Belt/chain guard linking pedal station to melter
    b.box((-0.18, 0.04, 0.44), (0.86, 0.08, 0.18), yellow, bevel=0.02)

    # Right side: insulated melting barrel & auger extruder
    mx = 0.34
    for sx in (-0.34, 0.34):
        for sy in (-0.34, 0.34):
            b.box((mx + sx, sy, 0.44), (0.08, 0.08, 0.68), steel)
    # Main horizontal heating barrel along Y (rear +Y to front -Y nozzle)
    barrel_m = Matrix.Translation((mx, 0.0, 0.84)) @ Matrix.Rotation(math.pi / 2, 4, "X")
    b.revolve([(0, -0.50), (0.24, -0.50), (0.26, -0.44), (0.26, 0.46), (0.16, 0.58), (0.08, 0.68), (0, 0.68)],
              yellow, seg=14, matrix=barrel_m)
    # Metal cooling fins & clamp bands
    for y_off in (-0.34, -0.12, 0.12, 0.32):
        fm = Matrix.Translation((mx, y_off, 0.84)) @ Matrix.Rotation(math.pi / 2, 4, "X")
        b.revolve([(0.262, -0.025), (0.285, -0.025), (0.285, 0.025), (0.262, 0.025)], dark, seg=14, caps=False, matrix=fm)
    # Extrusion nozzle tip & polymer pellet catch tray at front (-Y)
    b.tube((mx, -0.60, 0.84), (mx, -0.76, 0.84), 0.055, copper, seg=10)
    b.box((mx, -0.68, 0.22), (0.48, 0.38, 0.18), dark, bevel=0.015)

    # Flared top bottle/scrap intake hopper above rear of barrel
    _hopper_funnel(b, (mx, 0.14, 1.30), (0.72, 0.64), (0.34, 0.32), 0.42, 0.038, yellow, orange)

    # Exhaust vent chimney on back
    b.tube((mx + 0.24, 0.36, 0.98), (mx + 0.24, 0.36, 1.56), 0.048, steel, seg=10)

    root = b.finish(collection)

    # Moving Part 1: Bicycle pedal crankset (spins about X)
    bp = mk.Builder("PedalPivot", scale=1.0)
    p_steel = bp.mat("mach_steel", STEEL, 0.35, 0.75)
    p_dark = bp.mat("mach_dark_metal", DARK_METAL, 0.5, 0.6)
    p_orange = bp.mat("mach_hivis_orange", HIVIS_ORANGE, 0.45)
    bp.revolve([(0, -0.02), (0.11, -0.02), (0.11, 0.02), (0, 0.02)], p_steel, seg=14,
               matrix=Matrix.Rotation(math.pi / 2, 4, "Y"))
    for s in (-1, 1):
        bp.box((s * 0.09, 0, s * 0.09), (0.03, 0.035, 0.20), p_dark)
        bp.box((s * 0.17, 0, s * 0.18), (0.12, 0.08, 0.03), p_orange, bevel=0.005)
    _child(bp.finish(collection), root, (bx, -0.02, 0.38))

    # Moving Part 2: Heavy rear flywheel driving auger (spins about X)
    bf = mk.Builder("FlywheelPivot", scale=1.0)
    f_dark = bf.mat("mach_dark_metal", DARK_METAL, 0.45, 0.7)
    f_orange = bf.mat("mach_hivis_orange", HIVIS_ORANGE, 0.45)
    _spoked_wheel(bf, (0, 0, 0), 0.29, 0.032, 6, f_dark, f_orange, axis="X")
    _child(bf.finish(collection), root, (-0.06, 0.06, 0.48))

    # GlowMesh: Thermal heating coil bands glowing bright orange when melting
    bg = mk.Builder("GlowMesh", scale=1.0)
    g_ember = bg.mat("mach_ember_glow", EMBER_GLOW, 0.25, emission=2.6)
    for y_off in (-0.23, 0.0, 0.22):
        gm = Matrix.Translation((mx, y_off, 0.84)) @ Matrix.Rotation(math.pi / 2, 4, "X")
        bg.revolve([(0.265, -0.032), (0.282, -0.032), (0.282, 0.032), (0.265, 0.032)], g_ember, seg=14, caps=False, matrix=gm)
    _child(bg.finish(collection), root)

    # Level 1 Upgrade: Brass twin-die extruder head, thermostat tower & auxiliary side hopper
    bu = mk.Builder("Upgrade_Lvl1", scale=1.0)
    u_brass = bu.mat("mach_brass_metal", BRASS, 0.35, 0.78)
    u_teal = bu.mat("mach_teal", TEAL, 0.45)
    u_cream = bu.mat("mach_cream", CREAM, 0.5)
    u_glass = bu.mat("mach_glass", GLASS, 0.1, 0.1)
    # Twin brass extrusion nozzles
    for sx in (-0.08, 0.08):
        bu.tube((mx + sx, -0.58, 0.84), (mx + sx, -0.82, 0.84), 0.036, u_brass, seg=10)
    # Thermostat gauge tower on right side
    bu.box((mx + 0.44, -0.05, 0.75), (0.22, 0.24, 0.65), u_teal, bevel=0.02)
    _pressure_gauge(bu, (mx + 0.44, -0.18, 0.96), u_brass, u_cream, u_glass, facing="-Y")
    # Cooling shroud over front nozzle
    bu.box((mx, -0.56, 1.06), (0.36, 0.24, 0.16), u_brass, bevel=0.015)
    _child(bu.finish(collection), root)

    _empty("HopperZone", root, (mx, 0.14, 1.42))
    _empty("OutputPoint", root, (mx, -0.78, 0.42))
    _empty("SteamPoint", root, (mx + 0.24, 0.36, 1.62))
    return root


# ================================================================ 3. BELLOWS CRUCIBLE FURNACE

def build_crucible_furnace(collection):
    b = mk.Builder("machine_crucible_furnace", scale=1.0)
    red = b.mat("mach_cans_red", CANS_RED, 0.55)
    brick = b.mat("mach_brick", BRICK, 0.85)
    iron = b.mat("mach_cast_iron_metal", CAST_IRON, 0.5, 0.72)
    steel = b.mat("mach_steel", STEEL, 0.42, 0.68)
    concrete = b.mat("mach_concrete", CONCRETE, 0.9)
    brass = b.mat("mach_brass_metal", BRASS, 0.35, 0.78)
    wood = b.mat("mach_wood_dark", WOOD_DARK, 0.8)

    # Fireproof concrete plinth
    b.box((0, 0, 0.09), (1.66, 1.56, 0.18), concrete, bevel=0.02)

    # Chunky cylindrical brick & red-iron drum kiln
    b.revolve([
        (0, 0.18), (0.56, 0.18), (0.58, 0.24), (0.54, 1.24), (0.56, 1.30),
        (0.42, 1.30), (0.38, 0.65), (0, 0.65),
    ], brick, seg=14)
    # Red-oxide steel outer jacket bands
    for z in (0.32, 0.74, 1.16):
        b.revolve([(0.565, z - 0.06), (0.59, z - 0.06), (0.59, z + 0.06), (0.565, z + 0.06)],
                  red, seg=14, caps=False)
    # Vertical iron tie-straps
    for k in range(6):
        ang = k * 60 + 30
        rad = math.radians(ang)
        b.box((math.cos(rad) * 0.57, math.sin(rad) * 0.57, 0.74), (0.04, 0.06, 1.06), iron, rot=(0, 0, ang))

    # Flared top can-drop hopper rim
    _hopper_funnel(b, (0, 0.0, 1.46), (0.82, 0.82), (0.52, 0.52), 0.30, 0.04, iron, red)

    # Back-right iron chimney stack
    b.tube((0.36, 0.38, 1.05), (0.36, 0.38, 2.05), 0.11, iron, seg=10)
    b.revolve([(0.11, 2.02), (0.17, 2.10), (0.17, 2.14), (0.09, 2.14)], steel, seg=10,
              matrix=Matrix.Translation((0.36, 0.38, 0)))

    # Front ingot pouring spout & cast-iron mould tray
    b.box((0, -0.66, 0.58), (0.28, 0.34, 0.08), iron, rot=(16, 0, 0))
    b.box((0, -0.82, 0.28), (0.54, 0.34, 0.18), iron, bevel=0.015)
    # Two shiny aluminum ingot bars resting on mould bench
    for sx in (-0.12, 0.12):
        b.box((sx, -0.82, 0.40), (0.16, 0.10, 0.06), steel, taper=(0.75, 0.75))

    # Left side: wooden bellows frame & brass air tuyere pipe into firebox
    b.box((-0.78, 0.0, 0.44), (0.42, 0.56, 0.52), wood, bevel=0.015)
    b.tube((-0.72, 0.0, 0.64), (-0.46, 0.0, 0.58), 0.055, brass, seg=10)

    root = b.finish(collection)

    # Moving Part: Hinged leather-and-wood forge bellows (rocks about X on back hinge)
    bb = mk.Builder("BellowsPivot", scale=1.0)
    b_wood = bb.mat("mach_wood", WOOD, 0.78)
    b_leather = bb.mat("mach_leather", LEATHER, 0.85)
    b_brass = bb.mat("mach_brass_metal", BRASS, 0.35, 0.75)
    # Pleated leather bag
    bb.box((0, -0.18, 0.08), (0.36, 0.44, 0.18), b_leather, taper=(0.85, 0.88))
    # Top paddle board + long handle
    bb.box((0, -0.18, 0.20), (0.40, 0.48, 0.045), b_wood, bevel=0.008)
    bb.tube((0, -0.36, 0.22), (0, -0.64, 0.32), 0.026, b_wood, seg=8)
    bb.ellipsoid((0, -0.65, 0.33), (0.042, 0.042, 0.042), b_brass, seg=10, rings=6)
    _child(bb.finish(collection), root, (-0.78, 0.22, 0.72))

    # GlowMesh: Molten crucible interior & front firebox arch glow
    bg = mk.Builder("GlowMesh", scale=1.0)
    g_ember = bg.mat("mach_ember_glow", EMBER_GLOW, 0.2, emission=3.0)
    bg.revolve([(0, 0.68), (0.36, 0.68), (0.36, 0.76), (0, 0.76)], g_ember, seg=12)
    bg.box((0, -0.55, 0.78), (0.28, 0.06, 0.22), g_ember, bevel=0.02)
    _child(bg.finish(collection), root)

    # Level 1 Upgrade: Twin brass air-injection manifold, reinforced heat shield & tall spark-arrestor crown
    bu = mk.Builder("Upgrade_Lvl1", scale=1.0)
    u_brass = bu.mat("mach_brass_metal", BRASS, 0.32, 0.8)
    u_copper = bu.mat("mach_copper_metal", COPPER, 0.32, 0.78)
    u_red = bu.mat("mach_cans_red", CANS_RED, 0.5)
    u_cream = bu.mat("mach_cream", CREAM, 0.5)
    u_glass = bu.mat("mach_glass", GLASS, 0.1, 0.1)
    # Copper induction ring wrapping kiln + twin turbo air pipes
    bu.revolve([(0.60, 0.88), (0.64, 0.88), (0.64, 0.98), (0.60, 0.98)], u_copper, seg=14, caps=False)
    bu.path([(-0.62, 0.0, 0.72), (-0.62, 0.38, 0.96), (0.0, 0.64, 0.96), (0.58, 0.22, 0.96)], 0.042, u_brass, seg=8)
    # Tall spark-arrestor chimney extension
    bu.tube((0.36, 0.38, 2.12), (0.36, 0.38, 2.75), 0.10, u_red, seg=10)
    bu.revolve([(0, 2.74), (0.22, 2.74), (0.14, 2.90), (0, 2.92)], u_brass, seg=10,
               matrix=Matrix.Translation((0.36, 0.38, 0)))
    # Side thermometer gauge on right
    bu.box((0.68, -0.18, 0.84), (0.14, 0.22, 0.22), u_red, bevel=0.015)
    _pressure_gauge(bu, (0.68, -0.30, 0.86), u_brass, u_cream, u_glass, facing="-Y")
    _child(bu.finish(collection), root)

    _empty("HopperZone", root, (0, 0, 1.54))
    _empty("OutputPoint", root, (0, -0.84, 0.48))
    _empty("SteamPoint", root, (0.36, 0.38, 2.20))
    return root


# ================================================================ 4. TYRE STRIPPER & THERMAL SEPARATOR

def build_tyre_stripper(collection):
    b = mk.Builder("machine_tyre_stripper", scale=1.0)
    rust = b.mat("mach_rust", RUST, 0.62)
    dark = b.mat("mach_dark_metal", DARK_METAL, 0.5, 0.62)
    steel = b.mat("mach_steel", STEEL, 0.38, 0.72)
    yellow = b.mat("mach_safety_yellow", SAFETY_YELLOW, 0.42)
    rubber = b.mat("mach_rubber", RUBBER, 0.88)
    teal = b.mat("mach_teal", TEAL, 0.48)

    # Heavy steel H-base
    b.box((0, 0, 0.06), (2.05, 1.38, 0.12), dark, bevel=0.015)
    # Sturdy side cheek plates (chunky industrial shredder housing)
    for sx in (-0.64, 0.64):
        b.box((sx, 0, 0.62), (0.14, 1.06, 1.02), rust, bevel=0.02)
    # Lower thermal separation chamber between cheeks
    b.box((0, 0.04, 0.44), (1.18, 0.92, 0.64), dark, bevel=0.02)
    # Front twin separator chutes (left: aluminum bead tray, right: polymer crumb tray)
    b.box((-0.30, -0.62, 0.34), (0.44, 0.36, 0.22), steel, bevel=0.012)
    b.box((0.30, -0.62, 0.34), (0.44, 0.36, 0.22), teal, bevel=0.012)
    # Safety warning stripe across front apron
    b.box((0, -0.45, 0.76), (1.16, 0.06, 0.10), yellow)

    # Wide flared tyre drop hopper on top
    _hopper_funnel(b, (0, 0.0, 1.32), (1.28, 0.96), (1.02, 0.72), 0.40, 0.045, rust, yellow)
    # Heavy rubber splash flaps inside hopper rim
    for k in range(4):
        b.box((-0.36 + k * 0.24, -0.34, 1.24), (0.20, 0.025, 0.22), rubber)

    # Exhaust chimney at back-left
    b.tube((-0.48, 0.46, 0.76), (-0.48, 0.46, 1.82), 0.085, dark, seg=10)
    b.revolve([(0.085, 1.80), (0.14, 1.86), (0.14, 1.90), (0.08, 1.90)], steel, seg=10,
              matrix=Matrix.Translation((-0.48, 0.46, 0)))

    root = b.finish(collection)

    # Moving Part 1 & 2: Counter-rotating toothed shredding rollers inside hopper throat
    for name, y_pos in (("RollerPivotA", -0.16), ("RollerPivotB", 0.16)):
        br = mk.Builder(name, scale=1.0)
        r_steel = br.mat("mach_steel", STEEL, 0.35, 0.78)
        r_dark = br.mat("mach_dark_metal", DARK_METAL, 0.45, 0.7)
        rm = Matrix.Rotation(math.pi / 2, 4, "Y")
        br.revolve([(0, -0.56), (0.11, -0.56), (0.11, 0.56), (0, 0.56)], r_steel, seg=12, matrix=rm)
        for k in range(5):
            x_off = -0.42 + k * 0.21
            br.revolve([(0.11, x_off - 0.025), (0.165, x_off - 0.015), (0.165, x_off + 0.015), (0.11, x_off + 0.025)],
                       r_dark, seg=8, caps=False, matrix=rm)
        _child(br.finish(collection), root, (0, y_pos, 0.96))

    # Moving Part 3: Big side spoked handwheel on right side (spins about X)
    bf = mk.Builder("FlywheelPivot", scale=1.0)
    f_red = bf.mat("mach_cans_red", CANS_RED, 0.48)
    f_steel = bf.mat("mach_steel", STEEL, 0.38, 0.72)
    f_yellow = bf.mat("mach_safety_yellow", SAFETY_YELLOW, 0.45)
    _spoked_wheel(bf, (0, 0, 0), 0.38, 0.036, 6, f_red, f_steel, axis="X")
    bf.tube((0.03, 0, 0.34), (0.20, 0, 0.34), 0.028, f_yellow, seg=8)
    _child(bf.finish(collection), root, (0.80, 0.0, 0.88))

    # GlowMesh: Thermal separation window glow
    bg = mk.Builder("GlowMesh", scale=1.0)
    g_ember = bg.mat("mach_ember_glow", EMBER_GLOW, 0.2, emission=2.6)
    bg.box((0, -0.44, 0.52), (0.64, 0.05, 0.14), g_ember, bevel=0.015)
    _child(bg.finish(collection), root)

    # Level 1 Upgrade: Overhead gantry hoist frame with yellow pulley hook + catalytic dome
    bu = mk.Builder("Upgrade_Lvl1", scale=1.0)
    u_yellow = bu.mat("mach_safety_yellow", SAFETY_YELLOW, 0.42)
    u_brass = bu.mat("mach_brass_metal", BRASS, 0.35, 0.78)
    u_steel = bu.mat("mach_steel", STEEL, 0.4, 0.7)
    u_cream = bu.mat("mach_cream", CREAM, 0.5)
    u_glass = bu.mat("mach_glass", GLASS, 0.1, 0.1)
    for sx in (-0.82, 0.82):
        bu.box((sx, 0.15, 1.32), (0.09, 0.09, 2.38), u_yellow)
    bu.box((0, 0.15, 2.48), (1.78, 0.12, 0.12), u_yellow, bevel=0.012)
    # Trolley & cable hook over hopper
    bu.box((0, 0.15, 2.38), (0.24, 0.18, 0.14), u_steel)
    bu.tube((0, 0.15, 2.32), (0, 0.15, 1.98), 0.018, u_steel, seg=6)
    bu.ellipsoid((0, 0.15, 1.92), (0.06, 0.04, 0.08), u_brass, seg=10, rings=6)
    # Catalytic drum & pressure dial on left flank
    bu.revolve([(0, 0.48), (0.18, 0.48), (0.18, 1.05), (0, 1.05)], u_brass, seg=12,
               matrix=Matrix.Translation((-0.88, -0.18, 0)))
    _pressure_gauge(bu, (-0.88, -0.37, 0.86), u_brass, u_cream, u_glass, facing="-Y")
    _child(bu.finish(collection), root)

    _empty("HopperZone", root, (0, 0.0, 1.44))
    _empty("OutputPoint", root, (0, -0.78, 0.46))
    _empty("SteamPoint", root, (-0.48, 0.46, 1.94))
    return root


# ================================================================ 5. UPCYCLE ASSEMBLY WORKBENCH

def build_workbench(collection):
    b = mk.Builder("machine_workbench", scale=1.0)
    green = b.mat("mach_company_green", COMPANY_GREEN, 0.48)
    teal = b.mat("mach_teal", TEAL, 0.48)
    wood = b.mat("mach_wood", WOOD, 0.76)
    wood_d = b.mat("mach_wood_dark", WOOD_DARK, 0.82)
    steel = b.mat("mach_steel", STEEL, 0.38, 0.72)
    dark = b.mat("mach_dark_metal", DARK_METAL, 0.5, 0.6)
    orange = b.mat("mach_hivis_orange", HIVIS_ORANGE, 0.45)
    yellow = b.mat("mach_safety_yellow", SAFETY_YELLOW, 0.42)

    # Sturdy green tubular steel frame & legs
    for sx in (-0.96, 0.96):
        for sy in (-0.48, 0.48):
            b.box((sx, sy, 0.44), (0.08, 0.08, 0.88), green, bevel=0.01)
    # Lower storage shelf
    b.box((0, 0, 0.24), (1.88, 0.92, 0.05), wood_d)
    # Stacked recycled planks & polymer crate on lower shelf
    b.box((-0.45, 0, 0.34), (0.68, 0.56, 0.14), teal, bevel=0.015)
    b.box((0.46, 0, 0.32), (0.66, 0.46, 0.10), wood)

    # Thick butcher-block timber workbench top
    b.box((0, 0, 0.91), (2.12, 1.12, 0.09), wood, bevel=0.014)

    # Rear pegboard & tool rack wall
    b.box((0, 0.50, 1.44), (2.06, 0.06, 0.96), dark, bevel=0.01)
    b.box((0, 0.52, 1.94), (2.12, 0.12, 0.08), green, bevel=0.01)
    # Tool silhouettes hanging on pegboard (saw, wrench, square, clamp)
    b.box((-0.62, 0.45, 1.50), (0.48, 0.02, 0.14), steel)                 # hand saw blade
    b.box((-0.86, 0.45, 1.50), (0.12, 0.04, 0.16), orange, bevel=0.01)    # saw handle
    b.box((-0.08, 0.45, 1.52), (0.08, 0.03, 0.38), steel)                 # wrench shaft
    b.box((0.26, 0.45, 1.46), (0.28, 0.02, 0.06), yellow)                 # carpenter square
    b.box((0.15, 0.45, 1.58), (0.06, 0.02, 0.24), yellow)
    b.box((0.68, 0.45, 1.50), (0.24, 0.04, 0.30), teal, bevel=0.01)       # blueprint board

    # Front-left heavy teal bench vise body
    b.box((-0.78, -0.52, 0.98), (0.22, 0.24, 0.16), teal, bevel=0.015)
    b.box((-0.78, -0.66, 1.00), (0.22, 0.08, 0.14), steel, bevel=0.01)

    # Right-side recessed parts intake tray (HopperZone)
    _hopper_funnel(b, (0.72, -0.05, 1.08), (0.58, 0.58), (0.44, 0.44), 0.24, 0.035, green, yellow)

    # Overhead hinged work lamp arm
    b.path([(0.0, 0.46, 1.88), (0.0, 0.16, 2.08), (0.0, -0.14, 1.96)], 0.024, steel, seg=8)
    b.revolve([(0, 0), (0.16, 0), (0.10, 0.12), (0, 0.14)], green, seg=12,
              matrix=Matrix.Translation((0.0, -0.14, 1.84)))

    root = b.finish(collection)

    # Moving Part: Vise screw crank handle (spins about Y/Z -> we use CrankPivot spinning about X or Y; let's spin along X axle on side!)
    bc = mk.Builder("CrankPivot", scale=1.0)
    c_steel = bc.mat("mach_steel", STEEL, 0.35, 0.75)
    c_orange = bc.mat("mach_hivis_orange", HIVIS_ORANGE, 0.45)
    bc.tube((-0.18, 0, 0), (0.18, 0, 0), 0.018, c_steel, seg=8)
    bc.ellipsoid((-0.19, 0, 0), (0.03, 0.03, 0.03), c_orange, seg=8, rings=5)
    bc.ellipsoid((0.19, 0, 0), (0.03, 0.03, 0.03), c_orange, seg=8, rings=5)
    _child(bc.finish(collection), root, (-0.78, -0.74, 0.98))

    # GlowMesh: Work lamp bulb glowing bright warm yellow while building
    bg = mk.Builder("GlowMesh", scale=1.0)
    g_lamp = bg.mat("mach_lamp_glow", LAMP_GLOW, 0.2, emission=2.8)
    bg.ellipsoid((0.0, -0.14, 1.84), (0.085, 0.085, 0.055), g_lamp, seg=10, rings=6)
    _child(bg.finish(collection), root)

    # Level 1 Upgrade: Precision jig rail, jib hoist arm with yellow hook, side tool cabinet & twin LED bar
    bu = mk.Builder("Upgrade_Lvl1", scale=1.0)
    u_yellow = bu.mat("mach_safety_yellow", SAFETY_YELLOW, 0.42)
    u_teal = bu.mat("mach_teal", TEAL, 0.45)
    u_brass = bu.mat("mach_brass_metal", BRASS, 0.35, 0.78)
    u_steel = bu.mat("mach_steel", STEEL, 0.4, 0.7)
    # Overhead jib boom on left corner
    bu.tube((-0.96, 0.46, 0.96), (-0.96, 0.46, 2.38), 0.048, u_yellow, seg=8)
    bu.box((-0.34, 0.46, 2.32), (1.32, 0.09, 0.10), u_yellow, bevel=0.01)
    bu.tube((0.18, 0.46, 2.28), (0.18, 0.46, 1.98), 0.016, u_steel, seg=6)
    bu.ellipsoid((0.18, 0.46, 1.94), (0.045, 0.035, 0.065), u_brass, seg=8, rings=5)
    # Precision brass guide rail across table + side drawer chest
    bu.box((-0.14, 0.26, 0.98), (1.10, 0.06, 0.05), u_brass)
    bu.box((1.24, 0.0, 0.48), (0.42, 0.88, 0.92), u_teal, bevel=0.02)
    for k in range(3):
        bu.box((1.24, -0.45, 0.24 + k * 0.25), (0.34, 0.03, 0.18), u_yellow)
    _child(bu.finish(collection), root)

    _empty("HopperZone", root, (0.72, -0.05, 1.18))
    _empty("OutputPoint", root, (0.0, -1.65, 0.0))
    _empty("SteamPoint", root, (0.0, 0.0, 1.25))
    return root


# ================================================================ 6. PEDAL & CRANK ENERGY GENERATOR

def build_pedal_generator(collection):
    b = mk.Builder("machine_pedal_generator", scale=1.0)
    orange = b.mat("mach_hivis_orange", HIVIS_ORANGE, 0.45)
    teal = b.mat("mach_teal", TEAL, 0.48)
    green = b.mat("mach_company_green", COMPANY_GREEN, 0.48)
    steel = b.mat("mach_steel", STEEL, 0.38, 0.72)
    dark = b.mat("mach_dark_metal", DARK_METAL, 0.5, 0.6)
    copper = b.mat("mach_copper_metal", COPPER, 0.32, 0.78)
    rubber = b.mat("mach_rubber", RUBBER, 0.88)
    yellow = b.mat("mach_safety_yellow", SAFETY_YELLOW, 0.42)

    # Heavy rubber-footed steel base skid
    b.box((0, 0, 0.05), (1.46, 1.16, 0.10), dark, bevel=0.015)

    # Upcycled bike frame welded in the centre-left
    fx = -0.22
    b.tube((fx, -0.34, 0.10), (fx, -0.18, 0.86), 0.034, orange, seg=8)  # seat tube
    b.tube((fx, 0.30, 0.10), (fx, 0.24, 0.96), 0.034, orange, seg=8)    # front post
    b.tube((fx, -0.18, 0.78), (fx, 0.24, 0.86), 0.030, orange, seg=8)   # top tube
    b.tube((fx, -0.02, 0.40), (fx, 0.24, 0.80), 0.030, orange, seg=8)   # down tube
    b.tube((fx, -0.30, 0.40), (fx, -0.02, 0.40), 0.028, dark, seg=8)    # stay
    # Saddle + handlebars
    b.box((fx, -0.20, 0.92), (0.18, 0.30, 0.065), rubber, bevel=0.02)
    b.tube((fx - 0.26, 0.24, 1.02), (fx + 0.26, 0.24, 1.02), 0.024, steel, seg=8)
    for s in (-1, 1):
        b.tube((fx + s * 0.17, 0.24, 1.02), (fx + s * 0.28, 0.24, 1.02), 0.032, teal, seg=8)

    # Rear dynamo housing + copper stator coil
    dx = 0.36
    b.box((dx, 0.12, 0.32), (0.38, 0.44, 0.44), dark, bevel=0.015)
    dm = Matrix.Translation((dx, 0.12, 0.68)) @ Matrix.Rotation(math.pi / 2, 4, "Y")
    b.revolve([(0, -0.18), (0.18, -0.18), (0.20, -0.14), (0.20, 0.14), (0.18, 0.18), (0, 0.18)],
              copper, seg=14, matrix=dm)
    # Upright team battery cabinet on right front
    b.box((0.46, -0.34, 0.62), (0.36, 0.34, 1.04), teal, bevel=0.02)
    b.box((0.46, -0.52, 0.92), (0.26, 0.03, 0.16), yellow)   # lightning badge panel
    # Thick power cable from dynamo to battery bank
    b.path([(dx, 0.12, 0.86), (dx, -0.12, 0.98), (0.46, -0.28, 1.12)], 0.026, orange, seg=8)

    # Belt linking flywheel to dynamo
    b.box((0.08, 0.06, 0.56), (0.62, 0.06, 0.12), rubber)

    root = b.finish(collection)

    # Moving Part 1: Pedal crankset (spins about X)
    bp = mk.Builder("PedalPivot", scale=1.0)
    p_steel = bp.mat("mach_steel", STEEL, 0.35, 0.75)
    p_dark = bp.mat("mach_dark_metal", DARK_METAL, 0.5, 0.6)
    p_yellow = bp.mat("mach_safety_yellow", SAFETY_YELLOW, 0.42)
    bp.revolve([(0, -0.02), (0.11, -0.02), (0.11, 0.02), (0, 0.02)], p_steel, seg=14,
               matrix=Matrix.Rotation(math.pi / 2, 4, "Y"))
    for s in (-1, 1):
        bp.box((s * 0.09, 0, s * 0.09), (0.03, 0.035, 0.20), p_dark)
        bp.box((s * 0.17, 0, s * 0.18), (0.12, 0.08, 0.03), p_yellow, bevel=0.005)
    _child(bp.finish(collection), root, (fx, -0.02, 0.40))

    # Moving Part 2: Heavy spoked cast-iron flywheel (spins about X)
    bf = mk.Builder("FlywheelPivot", scale=1.0)
    f_dark = bf.mat("mach_dark_metal", DARK_METAL, 0.45, 0.72)
    f_green = bf.mat("mach_company_green", COMPANY_GREEN, 0.45)
    _spoked_wheel(bf, (0, 0, 0), 0.34, 0.034, 6, f_dark, f_green, axis="X")
    _child(bf.finish(collection), root, (fx + 0.14, 0.28, 0.46))

    # Moving Part 3: Spinning dynamo pulley on right
    bd = mk.Builder("DynamoPivot", scale=1.0)
    d_brass = bd.mat("mach_brass_metal", BRASS, 0.32, 0.78)
    d_steel = bd.mat("mach_steel", STEEL, 0.35, 0.75)
    _spoked_wheel(bd, (0, 0, 0), 0.14, 0.022, 4, d_brass, d_steel, axis="X")
    _child(bd.finish(collection), root, (dx - 0.22, 0.12, 0.68))

    # GlowMesh: Charge indicator bars on battery bank
    bg = mk.Builder("GlowMesh", scale=1.0)
    g_charge = bg.mat("mach_charge_glow", CHARGE_GLOW, 0.2, emission=2.6)
    for k in range(3):
        bg.box((0.46, -0.52, 0.34 + k * 0.16), (0.24, 0.04, 0.09), g_charge, bevel=0.01)
    _child(bg.finish(collection), root)

    # Level 1 Upgrade: Twin capacitor bank + angled solar assist panel on mast
    bu = mk.Builder("Upgrade_Lvl1", scale=1.0)
    u_brass = bu.mat("mach_brass_metal", BRASS, 0.35, 0.78)
    u_steel = bu.mat("mach_steel", STEEL, 0.38, 0.72)
    u_solar = bu.mat("mach_solar_glass", (0.12, 0.22, 0.45), 0.15, 0.35)
    u_yellow = bu.mat("mach_safety_yellow", SAFETY_YELLOW, 0.42)
    # Solar mast rising behind battery
    bu.tube((0.46, 0.34, 0.10), (0.46, 0.34, 1.92), 0.038, u_steel, seg=8)
    bu.box((0.46, 0.30, 1.98), (0.86, 0.56, 0.045), u_steel, rot=(-28, 0, 0), bevel=0.008)
    bu.box((0.46, 0.28, 2.00), (0.78, 0.48, 0.025), u_solar, rot=(-28, 0, 0))
    # Twin brass capacitor cylinders on battery top
    for sx in (-0.09, 0.09):
        bu.revolve([(0, 1.14), (0.075, 1.14), (0.075, 1.44), (0, 1.44)], u_brass, seg=10,
                   matrix=Matrix.Translation((0.46 + sx, -0.34, 0)))
    bu.box((0.46, -0.34, 1.47), (0.28, 0.06, 0.04), u_yellow)
    _child(bu.finish(collection), root)

    _empty("HopperZone", root, (0, 0, 0.8))
    _empty("OutputPoint", root, (0.46, -0.58, 0.72))
    _empty("SteamPoint", root, (dx, 0.12, 0.96))
    return root


MODELS = {
    "machine_pulper": build_pulper,
    "machine_plastic_melter": build_plastic_melter,
    "machine_crucible_furnace": build_crucible_furnace,
    "machine_tyre_stripper": build_tyre_stripper,
    "machine_workbench": build_workbench,
    "machine_pedal_generator": build_pedal_generator,
}
