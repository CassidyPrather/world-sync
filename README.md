# Wirenook World Sync

Leaves part of a VRChat avatar behind in the world: a prop follows a carry
point on the avatar until it is dropped, then stays where it was.

Package: `net.wirenook.world-sync`. Needs the VRChat Avatars SDK 3.10.6 or
newer, [Modular Avatar](https://modular-avatar.nadena.dev/) and
[`net.wirenook.assets`](https://github.com/CassidyPrather/wirenook-assets).

## What is in it

- **`Prefabs/Wirenook Anchor Pin.prefab`**: a map pin you plant a step in
  front of you from the menu ("Drop Pin"). Drag it onto an avatar; Modular
  Avatar wires up the menu, parameter and animator at build time. It costs
  one synced bit and one constraint.
- **`WorldAnchor`** (editor API, `Wirenook.WorldSync.Editor`): the building
  block the pin is made from, for other packages to make their own props
  droppable. [`avatar-stations`](https://github.com/CassidyPrather/avatar-stations)
  uses it for its portal.

```csharp
var anchor = FeaturePrefab.Child(root, "Anchor");
WorldAnchor.Configure(anchor, carryPoint);      // follow carryPoint until dropped
WorldAnchor.DropLayer(fx, "My Prop", "My/Param", // pop in, drop, pick up again
    anchorPath, propBonePath);
```

## How it works

An anchor is an object with a VRChat Parent Constraint whose only source is
the carry point. The animator switches the constraint's **Freeze To World**
option with one synced bool. While frozen, VRChat holds the object at its
world position on every client.

## What it guarantees, and what it does not

Each viewer's client freezes the anchor when the parameter change reaches
it, at the place where that viewer sees the carry point at that moment.

- People who are present when the anchor is dropped agree on its position to
  within network delay. At walking speed that is a few centimetres.
- Someone who joins, or first loads the avatar, **after** the drop freezes
  the anchor wherever the wearer is standing at that moment instead. Picking
  the anchor up and dropping it again puts everyone back in agreement.

Closing that gap needs the anchor's world position itself to be synced,
which means measuring it on the wearer's client and sending it through
synced parameters. The SDK 3.10.6 contact receivers can report a sender's
relative position (`_SenderLocalPositionX/Y/Z`), which makes the measuring
half practical, but a contact reaches three metres at most, so covering a
world takes a coarse-to-fine scheme on top. That is not built. Nothing in
this package claims late-joiner consistency.

None of this has been run in the VRChat client yet; see
[cass-avatar](https://github.com/CassidyPrather/cass-avatar) for the state of
in-game testing.

## Rebuilding the generated assets

Everything under `Generated/` and `Prefabs/` is written by
`Tools > Wirenook > World Sync > Rebuild`
(`Wirenook.WorldSync.Editor.WorldSyncBuild.Rebuild`). The model comes from
`blender/`; see `blender/README.md`.

## Development

Requires Unity 2022.3.22f1 (see `ProjectSettings/ProjectVersion.txt`) via a VPM
client (prefer [ALCOM](https://vrc-get.anatawa12.com/en/alcom/)), and the
[.NET SDK](https://dotnet.microsoft.com/download) 11 for the headless checks.

The Unity project at the repository root is the development harness. It
takes `net.wirenook.assets` from a checkout of
[wirenook-assets](https://github.com/CassidyPrather/wirenook-assets) beside
this one (`../wirenook-assets`). The simplest way to get that layout is to
work in [cass-avatar](https://github.com/CassidyPrather/cass-avatar), which
mounts this repository and its siblings as submodules under `modules/`.

```bash
scripts/check-meta.sh          # every visible file has a committed .meta
scripts/check-manifests.py     # package.json sanity across Packages/
scripts/format.sh --check      # dotnet-format over the CI-visible C#
scripts/test.sh                # headless NUnit run of Runtime/Core + Tests
scripts/build-packages.sh      # VPM .zip + .unitypackage per package -> dist/
```

Pre-commit hook: `git config core.hooksPath .githooks`

Release: bump `version` in the package's `package.json`, commit and push,
then create a GitHub release tagged with that version. CI attaches the `.zip`
and `.unitypackage`; the listing at
[CassidyPrather/vpm](https://github.com/CassidyPrather/vpm) picks them up
once this repository is in its `githubRepos`.

## Legal

Copyright 2026 Cassidy Prather <cassidy@wirenook.net>

Everything in this repository henceforth is licensed under
[AGPLv3](https://www.gnu.org/licenses/agpl-3.0.html) (See `LICENSE`) unless
otherwise specified.

As a special exception, the copyright holders give permission to link this
Program with the VRChat SDK and with Unity Technologies' proprietary engine
components, and to convey the resulting work under terms of your choice,
provided that you also convey the Corresponding Source of this Program under
the terms of the GNU Affero General Public License version 3 in all other
respects. (This exception also in `LICENSE_ADDENDUM`.)

## Credits

Descended from https://github.com/vrchat-community/template-package
([VRCHAT DISTRO LICENSE FILE](https://github.com/vrchat-community/template-package/blob/d9cf13fe9f56867cbf7315a4dbbf1901bc1537ec/Packages/com.vrchat.core.bootstrap/License.md))

Third-party material is inventoried in [CREDITS.md](CREDITS.md).
