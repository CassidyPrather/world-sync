"""Builds the world-sync prop: the pin that marks where something was left.

    blender -b --python blender/build_props.py [-- --only STEM] [-- --previews DIR]

The prop is modelled from the code below, saved as `blender/<stem>.blend`,
checked against the wirenook prop contract and exported to the package's
`Models/<stem>.fbx`. See README.md beside this file.
"""

import math
import pathlib
import sys

sys.dont_write_bytecode = True

REPO = pathlib.Path(__file__).resolve().parents[1]
MODELS = REPO / "Packages" / "net.wirenook.world-sync" / "Models"
LIBRARY = REPO.parent / "wirenook-assets" / "tools" / "blender"
if not (LIBRARY / "wirenook_props.py").is_file():
    raise SystemExit(f"The shared prop kit is missing: {LIBRARY / 'wirenook_props.py'}\n"
                     "Clone wirenook-assets beside this repository and run again.")
sys.path.insert(0, str(LIBRARY))

from mathutils import Vector

from wirenook_props import FORWARD, UP, PropSpec, main_build, rabbit_badge

ANCHOR_PIN = PropSpec("AnchorPin", "anchor-pin", triangle_budget=200)

# Height at which the dark point gives way to the glowing head.
SHANK = 0.075


def teardrop(height, head_radius):
    """A map pin's outline as (across, up) points: a cone from the tip that meets
    the round head where it is tangent to it, so there is no shoulder."""
    head_centre = height - head_radius
    tangent = math.asin(head_radius / head_centre)
    side = [(SHANK * math.tan(tangent), SHANK)]
    side += [(head_radius * math.cos(a), head_centre + head_radius * math.sin(a))
             for a in (-tangent, math.radians(12.0), math.radians(42.0), math.radians(68.0))]
    return [(0.0, 0.0)] + side + [(0.0, height)] + [(-x, z) for x, z in reversed(side)]


def build_anchor_pin(mesh):
    height, head_radius = 0.25, 0.066
    outline = teardrop(height, head_radius)
    # A thick token rather than a turned teardrop: its flat faces carry the
    # mark, which a round head would leave floating off the surface.
    half_depth, bevel, inset = 0.036, 0.014, 0.8
    pivot = Vector((0.0, 0.0, height * 0.56))

    def ring(depth, scale):
        # Below the head the token thins towards the ground, so that it
        # ends in a point from the side as well as from the front.
        points = [(Vector((x, 0.0, z)), 0.08 + 0.92 * min(z / SHANK, 1.0)) for x, z in outline]
        return [pivot + (p - pivot) * scale + FORWARD * (depth * thin) for p, thin in points]

    def tone(point):
        return "ink" if point.z < SHANK else "glow-orange"

    pin = mesh.part()
    rings = [ring(half_depth, inset), ring(half_depth - bevel, 1.0), ring(bevel - half_depth, 1.0),
             ring(-half_depth, inset)]
    pin.loft(rings, tone, smooth=True)
    for face, facing in ((rings[0], FORWARD), (rings[-1], -FORWARD)):
        pin.polygon([face[0], face[1], face[-1]], "ink", facing)
        pin.polygon(face[1:], "glow-orange", facing)
        rabbit_badge(pin, UP * (height - head_radius - 0.012) + facing * (half_depth + 0.0015), facing, 0.019,
                     "glow-white")

    # A raised ripple where the tip touches down, so the spot still reads from above.
    pin.lathe((0.0, 0.0, 0.0), UP, [(0.0, 0.090), (0.007, 0.076), (0.0, 0.062)], "glow-cyan", segments=10,
              phase=0.5, sharp=(1,))
    return {}


PROPS = [(ANCHOR_PIN, build_anchor_pin)]

if __name__ == "__main__":
    main_build(PROPS, REPO / "blender", MODELS)
