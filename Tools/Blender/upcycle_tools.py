"""The player's gear for Upcycle City: the litter picker held in the first-person fist, and the shopping cart.

  litter_picker   a municipal reacher/grabber. Origin in the middle of the fist (ToolSocket), the shaft running
                  forwards (-Y here, +Z in Unity), up +Z. Moving parts are child objects pivoted where they hinge,
                  so Unity can animate them:
                    JawL / JawR   the two jaws with rubber cups, pivoted on the head; modelled closed,
                                  Unity swings them open about the vertical axis
                    Trigger       the lever under the grip, pivoted at its top; Unity squeezes it back
                    ClawPoint     an empty between the closed cups where gripped trash is held
  shopping_cart   an open wire-basket trolley so whatever is collected shows through the wires. Faces -Y here
                  (+Z in Unity) with the push handle at the back (+Y). Children:
                    Wheel_FL/FR/BL/BR   castor wheels, origin on the axle (Unity spins them about X)
                    BasketFloor         empty in the middle of the basket floor (Unity stacks trash from here)
                    HandleGrip          empty in the middle of the push handle

Modelled in metres. The basket and handle numbers are shared with ShoppingCart.cs, so keep them in sync.
"""

import math

import bpy
from mathutils import Matrix, Vector

import mesh_kit as mk

# ---------------------------------------------------------------- palette (AGENTS.md brand colours)

HIVIS_ORANGE = (1.0, 0.478, 0.102)     # #FF7A1A player hi-vis
SAFETY_YELLOW = (0.949, 0.761, 0.106)  # #F2C21B
COMPANY_GREEN = (0.180, 0.545, 0.341)  # #2E8B57
TEAL = (0.184, 0.561, 0.541)           # #2F8F8A
RUBBER = (0.07, 0.07, 0.075)
ALU = (0.74, 0.76, 0.79)
STEEL = (0.62, 0.64, 0.67)
DARK_PLASTIC = (0.12, 0.13, 0.14)
WHITE = (0.95, 0.95, 0.92)

# ---------------------------------------------------------------- litter picker

PICKER_SHAFT_END = -0.80      # y where the shaft meets the jaw head
PICKER_HINGE_Y = -0.835       # jaws pivot here
PICKER_HINGE_X = 0.011
PICKER_CLAW_Y = -0.915        # between the closed cups


def _along_y(y):
    """Matrix for revolve(): local Z becomes -Y (forwards), starting at y."""
    return Matrix.Translation((0, y, 0)) @ Matrix.Rotation(math.radians(90), 4, "X")


def _empty(name, parent, location):
    e = bpy.data.objects.new(name, None)
    e.empty_display_size = 0.02
    e.location = location
    e.parent = parent
    parent.users_collection[0].objects.link(e)
    return e


def _child(obj, parent, location):
    obj.parent = parent
    obj.location = location
    return obj


def build_litter_picker(collection):
    b = mk.Builder("litter_picker", scale=1.0)
    orange = b.mat("tool_hivis_orange", HIVIS_ORANGE, 0.45)
    rubber = b.mat("tool_rubber", RUBBER, 0.85)
    alu = b.mat("tool_aluminium", ALU, 0.3, 0.85)
    teal = b.mat("tool_teal", TEAL, 0.4)
    dark = b.mat("tool_dark_plastic", DARK_PLASTIC, 0.55)

    # Grip: a moulded orange handle with a ribbed rubber over-mould where the fingers close (the fist sits on
    # y = 0), a rounded butt behind the hand and a flared guard in front of it.
    b.revolve([(0.0, -0.072), (0.009, -0.070), (0.015, -0.064), (0.0165, -0.056), (0.0165, 0.052), (0.023, 0.060),
               (0.023, 0.068), (0.016, 0.078), (0.013, 0.094), (0.0, 0.094)], orange, seg=18, matrix=_along_y(0.0))
    for k in range(6):   # finger ribs
        z = -0.045 + k * 0.018
        b.revolve([(0.0172, z - 0.005), (0.0192, z - 0.003), (0.0192, z + 0.003), (0.0172, z + 0.005)],
                  rubber, seg=18, caps=False, matrix=_along_y(0.0))
    # thumb rest on top of the grip, and the trigger guard loop underneath
    b.ellipsoid((0, -0.035, 0.019), (0.012, 0.03, 0.007), orange, seg=12, rings=6)
    b.path([(0, -0.058, -0.016), (0, -0.064, -0.050), (0, -0.086, -0.058), (0, -0.094, -0.020)], 0.0045, orange, seg=8)

    # Shaft: anodised aluminium tube with the pull rod running under it, a teal sticker band and a collar each end
    b.tube((0, -0.09, 0), (0, PICKER_SHAFT_END, 0), 0.0115, alu, seg=14)
    b.tube((0, -0.10, -0.012), (0, PICKER_SHAFT_END + 0.01, -0.012), 0.0022, dark, seg=6)
    b.tube((0, -0.24, 0), (0, -0.30, 0), 0.0128, teal, seg=14)
    b.tube((0, -0.31, 0), (0, -0.318, 0), 0.0128, b.mat("tool_white", WHITE, 0.5), seg=14)
    b.tube((0, -0.088, 0), (0, -0.112, 0), 0.0135, orange, seg=14, r1=0.011)

    # Head: an orange housing the jaws pivot in, with a stub magnet for bottle caps underneath
    b.tube((0, PICKER_SHAFT_END + 0.01, 0), (0, PICKER_SHAFT_END - 0.012, 0), 0.0125, orange, seg=14)
    b.box((0, PICKER_HINGE_Y + 0.008, 0), (0.042, 0.03, 0.022), orange, bevel=0.005)
    b.tube((0, PICKER_HINGE_Y + 0.005, -0.011), (0, PICKER_HINGE_Y + 0.005, -0.022), 0.006, dark, seg=10)
    picker = b.finish(collection)

    for side, name in ((-1, "JawL"), (1, "JawR")):
        jaw = _build_jaw(name, side, collection)
        _child(jaw, picker, (side * PICKER_HINGE_X, PICKER_HINGE_Y, 0))

    trig = _build_trigger(collection)
    _child(trig, picker, (0, -0.066, -0.017))

    _empty("ClawPoint", picker, (0, PICKER_CLAW_Y, 0))
    _empty("ToolGrip", picker, (0, 0, 0))
    return picker


def _build_jaw(name, side, collection):
    """A jaw bowed outwards with a rubber cup at the tip facing the other jaw. Origin on its hinge, closed pose."""
    b = mk.Builder(name, scale=1.0)
    orange = b.mat("tool_hivis_orange", HIVIS_ORANGE, 0.45)
    green = b.mat("tool_cup_green", COMPANY_GREEN, 0.75)
    steel = b.mat("tool_steel", STEEL, 0.35, 0.8)
    s = side
    # flat spring-steel finger, bowed out then back in towards the cup
    b.path([(0, 0, 0), (s * 0.010, -0.025, 0), (s * 0.014, -0.055, 0), (s * 0.005, -0.082, 0)], 0.0045, steel, seg=8)
    b.box((s * 0.004, -0.004, 0), (0.012, 0.016, 0.018), orange, bevel=0.003)   # knuckle in the head
    # cup: a shallow rubber disc on the inside face of the tip
    cup = Matrix.Translation((s * 0.002, -0.086, 0)) @ Matrix.Rotation(math.radians(-90 * s), 4, "Y")
    b.revolve([(0.0, 0.0), (0.018, 0.0), (0.018, 0.005), (0.014, 0.008), (0.0, 0.005)], green, seg=14, matrix=cup)
    return b.finish(collection)


def _build_trigger(collection):
    """The lever under the grip; origin at its pivot at the top. Squeezing it rotates it back towards the grip."""
    b = mk.Builder("Trigger", scale=1.0)
    dark = b.mat("tool_dark_plastic", DARK_PLASTIC, 0.55)
    b.path([(0, 0, 0), (0, 0.004, -0.018), (0, 0.012, -0.034)], 0.0055, dark, seg=8)
    b.box((0, 0.012, -0.034), (0.014, 0.008, 0.012), dark, bevel=0.003)
    return b.finish(collection)


# ---------------------------------------------------------------- shopping cart

# Basket (inside the wires). Shared with ShoppingCart.cs.
BASKET_FLOOR_Z = 0.50
BASKET_RIM_Z = 0.90
BASKET_BOTTOM = (0.44, 0.30, -0.30)   # width, back y, front y at the floor
BASKET_TOP = (0.52, 0.40, -0.44)      # width, back y, front y at the rim
HANDLE_Y = 0.50
HANDLE_Z = 0.98
WHEEL_R = 0.065
WHEELS = {"Wheel_BL": (-0.20, 0.34), "Wheel_BR": (0.20, 0.34), "Wheel_FL": (-0.17, -0.40), "Wheel_FR": (0.17, -0.40)}
WIRE_R = 0.0032
RAIL_R = 0.0065
PITCH = 0.048


def _basket_point(u, v, h):
    """A point on the basket surface: u across (-1..1), v along (0 back .. 1 front), h height (0 floor .. 1 rim)."""
    wb, by, fy = BASKET_BOTTOM
    wt, ty, tfy = BASKET_TOP
    w = wb + (wt - wb) * h
    back = by + (ty - by) * h
    front = fy + (tfy - fy) * h
    return Vector((u * w / 2, back + (front - back) * v, BASKET_FLOOR_Z + (BASKET_RIM_Z - BASKET_FLOOR_Z) * h))


def build_shopping_cart(collection):
    b = mk.Builder("shopping_cart", scale=1.0)
    chrome = b.mat("cart_chrome", (0.80, 0.82, 0.85), 0.25, 0.9)
    frame = b.mat("cart_frame", (0.55, 0.58, 0.62), 0.35, 0.8)
    green = b.mat("cart_handle_green", COMPANY_GREEN, 0.5)
    grey = b.mat("cart_bumper", (0.20, 0.21, 0.22), 0.7)
    white = b.mat("cart_white", WHITE, 0.5)
    yellow = b.mat("cart_yellow", SAFETY_YELLOW, 0.5)

    def wire(p0, p1, r=WIRE_R, mat=chrome):
        b.tube(p0, p1, r, mat, seg=6, caps=False)

    # rails round the basket: floor edge, two mid rails, the rolled rim
    for h, r in ((0.0, RAIL_R), (0.36, WIRE_R * 1.3), (0.70, WIRE_R * 1.3), (1.0, RAIL_R * 1.25)):
        corners = [_basket_point(-1, 0, h), _basket_point(1, 0, h), _basket_point(1, 1, h), _basket_point(-1, 1, h)]
        b.path(corners + [corners[0], corners[1]], r, chrome, seg=8, caps=False)

    # vertical wires on the long sides, the front and the back gate
    length_top = BASKET_TOP[1] - BASKET_TOP[2]
    n_side = max(2, int(length_top / PITCH))
    for i in range(1, n_side):
        v = i / n_side
        for u in (-1, 1):
            wire(_basket_point(u, v, 0), _basket_point(u, v, 1))
    n_end = max(2, int(BASKET_TOP[0] / PITCH))
    for i in range(1, n_end):
        u = -1 + 2 * i / n_end
        wire(_basket_point(u, 1, 0), _basket_point(u, 1, 1))
        wire(_basket_point(u, 0, 0), _basket_point(u, 0, 1))
    # floor grid
    n_floor = max(2, int(BASKET_BOTTOM[0] / PITCH))
    for i in range(1, n_floor):
        u = -1 + 2 * i / n_floor
        wire(_basket_point(u, 0, 0), _basket_point(u, 1, 0))
    for i in range(1, 5):
        v = i / 5
        wire(_basket_point(-1, v, 0), _basket_point(1, v, 0))

    # front corner bumpers and a green Upcycle plate with the recycling arrows on the front
    for u in (-1, 1):
        p = _basket_point(u, 1, 0.5)
        b.box((p.x, p.y - 0.01, p.z), (0.03, 0.03, 0.42), grey, bevel=0.008)
    plate_c = _basket_point(0, 1, 0.55) + Vector((0, -0.012, 0))
    b.box(plate_c, (0.22, 0.012, 0.13), green, bevel=0.01)
    mk.recycle_icon(b, white, Matrix.Translation(plate_c + Vector((0, -0.007, 0))) @ Matrix.Rotation(math.radians(90), 4, "X"),
                    radius=0.045, width=0.016, depth=0.004)

    # push handle: posts up and back from the rear corners to a bar with a green grip
    for u in (-1, 1):
        top_back = _basket_point(u, 0, 1)
        b.path([_basket_point(u, 0, 0.0), top_back, (u * 0.25, HANDLE_Y, HANDLE_Z - 0.03)], 0.009, frame, seg=8)
    b.tube((-0.275, HANDLE_Y, HANDLE_Z - 0.02), (0.275, HANDLE_Y, HANDLE_Z - 0.02), 0.011, frame, seg=10)
    b.tube((-0.235, HANDLE_Y, HANDLE_Z - 0.02), (0.235, HANDLE_Y, HANDLE_Z - 0.02), 0.017, green, seg=14)
    for u in (-1, 1):
        b.tube((u * 0.235, HANDLE_Y, HANDLE_Z - 0.02), (u * 0.25, HANDLE_Y, HANDLE_Z - 0.02), 0.019, yellow, seg=14)

    # chassis: a U of tube from the back legs to the front castors, struts up to the basket, a lower wire tray
    rail_z = 0.15
    for s in (-1, 1):
        back = (s * 0.20, 0.34, rail_z)
        front = (s * 0.17, -0.40, rail_z - 0.02)
        b.path([_basket_point(s, 0, 0) + Vector((0, 0, -0.01)), (s * 0.20, 0.36, 0.30), back, front], 0.011, frame, seg=8)
        b.tube(_basket_point(s, 1, 0), (s * 0.17, -0.38, rail_z), 0.008, frame, seg=8)
    b.tube((-0.17, -0.40, rail_z - 0.02), (0.17, -0.40, rail_z - 0.02), 0.011, frame, seg=8)
    b.tube((-0.20, 0.30, rail_z + 0.01), (0.20, 0.30, rail_z + 0.01), 0.007, frame, seg=8)   # tray back bar
    for i in range(1, 6):
        x = -0.18 + 0.36 * i / 6
        wire((x, 0.30, rail_z + 0.01), (x, -0.40, rail_z - 0.02), r=WIRE_R)
    # castor forks (the wheels themselves are separate so Unity can spin them)
    for name, (x, y) in WHEELS.items():
        top = Vector((x, y, rail_z - 0.01))
        b.tube(top, top + Vector((0, 0, -0.025)), 0.014, grey, seg=10)
        for side in (-1, 1):
            b.box((x + side * 0.022, y, WHEEL_R + 0.02), (0.006, 0.03, 0.09), frame)

    cart = b.finish(collection)

    for name, (x, y) in WHEELS.items():
        wb = mk.Builder(name, scale=1.0)
        tyre = wb.mat("cart_tyre", (0.10, 0.10, 0.11), 0.85)
        hub = wb.mat("cart_hub", (0.75, 0.76, 0.78), 0.4, 0.6)
        mk.wheel(wb, (0, 0, 0), WHEEL_R, 0.03, tyre, hub, hub_mat=hub, rim_ratio=0.55, axis="X")
        _child(wb.finish(collection), cart, (x, y, WHEEL_R))

    _empty("BasketFloor", cart, (0, (BASKET_BOTTOM[1] + BASKET_BOTTOM[2]) / 2, BASKET_FLOOR_Z))
    _empty("HandleGrip", cart, (0, HANDLE_Y, HANDLE_Z - 0.02))
    return cart


MODELS = {
    "litter_picker": build_litter_picker,
    "shopping_cart": build_shopping_cart,
}
