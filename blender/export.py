"""Checks hand-edited prop sources against the contract and exports them.

    blender -b --python blender/export.py -- blender/anchor-pin.blend [MORE.blend ...]

Runs the same validation as build_props.py; a file that fails is not exported.
"""

import pathlib
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))

import build_props
from wirenook_props import main_export

if __name__ == "__main__":
    main_export(build_props.PROPS, build_props.MODELS)
