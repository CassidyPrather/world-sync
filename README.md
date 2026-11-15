# Wirenook World Sync

Leaves part of a VRChat avatar behind in the world, in the same place for
everyone, including people who arrive later.

Package: `net.wirenook.world-sync`. Needs the VRChat Avatars SDK 3.10.6 or
newer, [Modular Avatar](https://modular-avatar.nadena.dev/) and
[`net.wirenook.assets`](https://github.com/CassidyPrather/wirenook-assets).

## What is in it

- **`Prefabs/Wirenook Anchor Pin.prefab`**: a map pin you plant a step in
  front of you from the menu ("Drop Pin"). Drag it onto an avatar; Modular
  Avatar wires up the menu, parameters and animator at build time.
- **`SyncedAnchor`** (editor API, `Wirenook.WorldSync.Editor`): makes any
  prop droppable with its position synced. The pin is built with it, and so
  is the portal in
  [`avatar-stations`](https://github.com/CassidyPrather/avatar-stations).
- **`WorldAnchor`**: the same without syncing, for props where one synced
  bit matters more than late joiners.

```csharp
SyncedAnchor.Prepare();                          // once, before editing prefabs
var rig = SyncedAnchor.Configure(root, anchor, carryPoint, "My/Prop");
rig.DropLayers(fx, "My Prop", "My/Prop", propPath);   // pop in, drop, pick up
FeaturePrefab.Parameters(root, rig.Parameters()
    .Prepend(FeaturePrefab.SyncedBool("My/Prop")).ToArray());
```

| | `SyncedAnchor` | `WorldAnchor` |
| --- | --- | --- |
| People present at the drop | Same place, to about 3 cm | Same place, to within network delay |
| People who arrive later | Same place, to about 3 cm | Wrong place until it is dropped again |
| Heading | Synced, to about half a degree | As each viewer saw it |
| Synced bits | 66 | 1 |
| Constraints | 7 | 1 |
| Contacts | 6, local-only (free for rank) | none |
| Reach | ±512 m from the world origin by default | unlimited |

## How the synced anchor works

The wearer's client is the source of truth. It freezes the anchor where it
was dropped, reads that spot's world coordinates, and syncs them. Every
other client places the anchor from the synced coordinates, so remote
viewers agree with each other exactly and with the wearer to within the
resolution.

**A world frame.** `World` is held at the world's origin and axes by a
constraint whose source is a prefab that is never placed in a scene; such
a source resolves to the origin. This is the technique VRLabs' World
Constraint established.

**Reading a position.** SDK 3.10.6 lets a contact receiver report where a
sender is, relative to itself, on three axes at once
(`_SenderLocalPositionX/Y/Z`). One receiver at the origin could therefore
read the anchor's coordinates directly, except that a contact reaches
three metres and a world is hundreds across. So the anchor's position is
first scaled down by a constraint with two weighted sources, the origin
and the anchor: such a constraint puts its object at their weighted
average, which is the anchor's position divided by a constant. That
object carries the sender.

**Two values per axis.** A synced parameter carries eight bits at most,
which over a whole world is metres. Each axis is sent as the index of a
coarse cell (an integer, exact on every client) and how far into the cell
the anchor is (a float; only this part is rounded, and it spans one cell).
The second reading is taken the same way as the first, against an object
that every client moves to the cell from the synced integer, so the wearer
measures the remainder against exactly what remote clients will add it to.

**Heading.** A sender one metre ahead of the anchor, read by a receiver
that sits on the anchor but keeps the world's axes, gives the sine and
cosine of the heading. Remote clients put a target at that offset and aim
the anchor at it; no trigonometry is needed in the animator.

**The sequence.** When the anchor is dropped, the wearer's animator waits
0.3 s for the readings to settle, copies the coarse reading into the cell
index, waits 0.3 s for the fine reading to settle against it, then sets a
synced `Ready` flag. Everyone else holds the anchor where they saw it
dropped until `Ready` arrives, then moves it to the synced spot. Someone
who loads the avatar later finds `Ready` already set and goes straight
there.

`Runtime/Core/WorldGrid.cs` holds the arithmetic (cell size, blend
weights, what each reading must be) with tests for the full round trip.

### Compared with earlier approaches

[JuzoVR's WorldSync](https://github.com/JuzoVR/WorldSync) and
[VRLabs' Custom Object Sync](https://github.com/VRLabs/Custom-Object-Sync)
solved this before contacts could report positions. They read each axis
from pairs of proximity receivers and send the result bit by bit through
parameter drivers over several steps. This rig reads all three axes from
one receiver and sends whole values at once: fewer parts and no protocol,
at the price of more synced bits (66 against roughly 14 for Juzo's).
Both are MIT-licensed and were read for reference; no code or assets are
taken from either.

## What has and has not been checked

Checked in the editor (`Tools > Wirenook > World Sync > Verify`):

- **Receiving.** Fed the values a wearer would sync for six positions
  across the range and six headings, the generated animator puts the
  target within 2.7 cm and 0.22° of where it should be.
- **Wiring.** The blend weights, contact radii and pairings on the built
  prefab match what `WorldGrid` requires.
- **Arithmetic.** Ten headless tests cover the round trip at ranges from
  160 m to 4 km, whether VRChat rounds or truncates the cell index.

Not checked, because it only runs in the VRChat client:

- **Sending.** That a receiver's `_SenderLocalPosition` values are metres
  along its own axes, that local-only contacts on the same avatar see each
  other, and that a constraint with two weighted sources lands on their
  weighted average. The documentation says so in each case; nobody has
  watched this rig do it.
- **The world frame**, which depends on the client treating an off-scene
  constraint source the way VRLabs' prefab relies on.
- **Timing.** 0.3 s is a guess at "long enough for constraints and
  contacts to settle".

Known limits:

- An anchor dropped beyond the range is clamped to its edge for remote
  viewers. Raise `range` in `SyncedAnchor.Configure` for larger worlds; the
  resolution grows with it (3.1 cm at ±1000 m).
- Only the heading is synced, not tilt. Drop things upright.
- Each synced anchor costs 66 bits. Two on one avatar is 132 of VRChat's
  256.

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
