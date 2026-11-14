# Prop sources

Blender sources for the models in `Packages/net.wirenook.world-sync/Models/`.
Each prop is generated from code, saved here as `.blend`, validated, exported as FBX.

| Source | Prop | Bones | Notes |
| --- | --- | --- | --- |
| `anchor-pin.blend` | `AnchorPin`: map pin with the rabbit mark, 0.25 m tall | `AnchorPin` (tip, on the ground) | Head and ground ripple use emissive swatches |

## Rebuild

Needs Blender 5.0 and `wirenook-assets` cloned beside this repo (shared kit: `../wirenook-assets/tools/blender/wirenook_props.py`).

```sh
blender -b --python blender/build_props.py                      # all props
blender -b --python blender/build_props.py -- --only anchor-pin   # one prop
blender -b --python blender/build_props.py -- --previews DIR     # also render a preview sheet per prop
```

Exit code 1 and no FBX written if a prop breaks the contract.

Never delete an `.fbx` to force a rebuild. The build replaces each one in a single step; a running Unity that sees a model missing deletes its `.meta`, and prefabs lose the GUID they reference.

## Editing by hand

Fine, as long as the contract holds. Edit the `.blend`, then:

```sh
blender -b --python blender/export.py -- blender/anchor-pin.blend
```

Same validation as the build. Note `build_props.py` regenerates the `.blend` from code and overwrites hand edits; move lasting changes into the code, or stop rebuilding that prop.

## Contract

Unity depends on all of this; `wirenook_props.validate` enforces it.

- Objects: armature `<Prop>Rig`, mesh `<Prop>` parented to it, one Armature modifier, no others. Transforms applied, metres.
- Bones: root `<Prop>` at origin (hold point), children `<Prop>.<Part>` as in the table. All point along +Z, no roll.
- Axes: forward is Blender -Y, up is +Z. Exported so Unity sees forward +Z, up +Y, no rotation or scale on any object or bone.
- Skinning: every vertex weight 1.0 on exactly one bone. Unity hides a prop by scaling its root bone to zero.
- Colour: one UV layer, `UVMap`. All loops of a face on one swatch centre of `wirenook-palette.png` (names in `wirenook-palette.json`).
- Materials: `wirenook` for solid parts, emissive swatches included. `wirenook-glow` only for light (beams, halos, waves), emissive swatches only.
- Triangle budget per prop: see `PropSpec` in `build_props.py`.
- Style: <https://wirenook.net/style/>. No flowers, no goth.
