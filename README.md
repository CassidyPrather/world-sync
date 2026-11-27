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
- **`AimedCarry`**: a carry point that is aimed. Give it to an anchor in
  place of a point in front of the wearer and the prop is put down where a
  hand points. See [Aiming](#aiming).

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

## Aiming

By default a dropped prop appears at its carry point, a step in front of
the wearer, and is dropped there in the same instant. `AimedCarry` makes
that two steps. Switching the prop on shows it to the wearer alone,
standing where the right hand points and turned to face them, and
following the hand; a fist (a trigger pull, on most controllers) sets it
down, and from then on the anchor holds it for everyone as before.
Switching it off before that cancels, and afterwards picks it up.

```csharp
var aim = AimedCarry.Configure(root, inFront, "My/Prop/Aim");        // inFront: where it goes when the hand points at nothing
var rig = SyncedAnchor.Configure(root, anchor, aim.Carry, "My/Prop");
rig.DropLayers(fx, "My Prop", "My/Prop", aim, propPath);             // adds the aim's layers too
FeaturePrefab.Parameters(root, rig.Parameters().Concat(aim.Parameters())
    .Prepend(FeaturePrefab.SyncedBool("My/Prop")).ToArray());
```

- **The ray** is a `VRCRaycast` (version 1.1) along the hand, against the
  world only, ten metres long, and enabled only while aiming. Every
  viewer's client casts it from the hand as it sees it, so the aiming
  itself syncs nothing. It counts as one raycast for performance rank
  (PC: 1 is Excellent, 4 Good; mobile: 1 and 2).
- **One synced bit** says "set down". What other people see while the
  wearer aims is nothing: the prop is drawn for them from the moment it is
  down.
- **The heading** is towards the wearer, about the vertical only, however
  far above or below them the spot is.
- **The hand is a button while aiming**, and the rig says so with a
  `WirenookHandClaim` (`net.wirenook.assets`). An avatar that reads those
  shows no facial expression for the fist that confirms. A hand that is
  already a fist when the prop comes out has to open first.
- It costs three constraints and one raycast on top of the anchor's own.

None of this has run in VRChat. Not known: whether a raycast's result is
in place before constraints read it in the same frame (if not, the preview
trails the hand by a frame); whether "the world only" includes surfaces a
given world would rather it did not (triggers, pickups); and how well the
fist reads as a click on each kind of controller. There is no menu button
for setting down yet, for controllers that never make a fist.

## Many anchors: the bus

A synced anchor costs 66 bits, seven constraints and six contacts for each
thing it places, and an avatar may sync 256 bits, so three is the most that
fit and two is the most that leave room for anything else. `AnchorBus`
places any number of things for the synced cost of one.

```csharp
SyncedAnchor.Prepare();                               // once, before editing prefabs
var bus = AnchorBus.Configure(root, "My/Bus", slots: 6, new[] { carryPoint });
myThing.parent = bus.Pose(1);                         // or constrain to it
bus.AddLayers(fx, place: "My/Place", clear: "My/Clear", broadcast: "My/Live");
FeaturePrefab.Parameters(root, bus.Parameters().Concat(mine).ToArray());
```

| | `SyncedAnchor` | `AnchorBus` |
| --- | --- | --- |
| Synced bits | 66 each | 57 for all, or 65 with a value for each slot |
| Constraints | 7 each | 5 for all, however many slots |
| Contacts | 6 each, local-only | 4 for all, local-only |
| Heading | Synced | None: slots stand upright in the world's axes |
| People present when it is placed | See it within about a second | See it within about two seconds |
| People who arrive later | See it at once | See it within one turn |
| Kept between sessions | No | Yes, with the avatar |

It has three parts.

**A cursor.** One object follows the wearer and is the only thing ever
measured. To place a slot, the wearer's client freezes the cursor, reads
its coordinates exactly as a synced anchor reads its anchor (cell, then
place within the cell), and copies them into that slot's own parameters.
That takes 0.7 s.

**Slots.** Each slot's coordinates are six parameters and a "placed" flag
of its own. They are not synced, which is why they cost nothing; on the
wearer's client they are saved with the avatar, so a layout survives
changing avatars and restarting. Every client moves each slot from its own
copy of those parameters, through the same blend trees a synced anchor
uses.

**A channel.** One set of synced parameters holds one slot's coordinates
and a number saying whose they are. The wearer's client loads a slot's
coordinates, waits, raises the slot's number, holds it, lowers it, and goes
on to the next slot, round and round for as long as it is asked to
broadcast. Every other client that hears a number copies the coordinates
beside it into its own copy of that slot. One turn takes just under a
second per slot.

The order of those steps is what makes it safe. At every instant the
number is up, the coordinates beside it are that slot's, so a client that
receives both in one update can never pair a number with another slot's
coordinates, however rarely updates arrive. A listener also waits a fifth
of a second after hearing a number before it copies, in case its client
eases synced floats towards new values rather than jumping, and copies a
second time a fifth of a second after that if the number is still up:
with fifteen slots, one copy left an easing client a step of the grid out
on some of them. If updates come less often than a number stays up (half
a second), a listener can miss a slot on one turn and catch it on a later
one.

A slot that has just been placed jumps the queue: the broadcast turns to
it as soon as it has finished the slot it is on, and goes on round from
there. So the people present have a new place within a second or two
however many slots there are, where a whole round is just under a second
for each slot.

Two parameters on the wearer's client say where a placement has got to,
for a feature that needs to wait for it: `bus.Pending` is the slot's
number from the moment it is chosen until its coordinates are stored, and
`bus.Fresh` is the number of a slot that has been stored and not yet read
out. While a slot's number is on air and `Fresh` is 0, what is on air is
where the slot stands now. The game master's kit in
[adventure-tools](https://github.com/CassidyPrather/adventure-tools) waits
for exactly that before it shows a thing to anyone, so that nobody sees
it where it used to stand.

What another slot costs is seven unsynced parameters, three objects, six
small blend trees and a dozen animator states. Nobody would draw that by
hand, and nobody has to.

### A value for each slot

A thing set down in the world usually has a setting as well as a place:
how big it is, which way it faces. As a synced parameter each, those are
eight bits apiece, and a dozen things would spend most of what an avatar
may sync on dials. A bus can carry them instead:

```csharp
var bus = AnchorBus.Configure(root, "My/Bus", slots: 6, new[] { carryPoint },
    values: new[] { 0.5f, 0.5f, 0f, 0f, 0f, 0f });        // what each slot's value rests at
fx.Layer("Size").State("Size", sweep).MotionTime(bus.Value(1));   // read it like any float
```

`bus.Value(slot)` is a float from 0 to 1 that every client has under one
name and that is not synced. On the wearer's client it is theirs to set,
with a dial in the menu, and it is saved with the avatar. Everyone
else's copy is written from the channel, which gains eight bits
(`bus.SentValue`) that carry the value of whichever slot is on air. So
every slot's value together costs eight synced bits, whatever the number
of slots: fifteen things cost 8 where a synced float each would cost 120.

It is time-division, and time is the price:

- **A change is not instant.** The wearer's client notices that a value
  is no longer what it last read out (a blend tree keeps the difference
  for every slot, since an animator cannot compare two parameters) and
  has that slot read out next, after any slot that has just been placed.
  The people present have it a second or two later. While a dial is
  being turned they follow it in steps about a second apart, not
  smoothly.
- **A late arrival waits a round**, as for the places: just under a
  second for each slot. Until a slot's turn comes, its value is what it
  rests at.
- **Nothing is read out unless the bus is broadcasting.** A feature that
  only broadcasts while something is set down (the game master's kit)
  sends a dial turned beforehand when the thing is next set down.
- **Eight bits**: 255 steps, the same as a synced float.
- **It leans on the same assumptions as the places**: that a parameter
  driver copies on other people's clients, and that synced parameters
  arrive together.

What it suits is anything that changes rarely and may arrive a second
late. What it does not suit is anything a viewer has to see move with
the wearer's hand. The channel could carry more than one byte a slot the
same way, and a slot need not stand for a place at all: a slot nothing
is ever set down in still has a turn, and its value still travels.

Used by the shape collector of
[party-tricks](https://github.com/CassidyPrather/party-tricks), which is
why it exists, and by the game master's kit of
[adventure-tools](https://github.com/CassidyPrather/adventure-tools).

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
- **The bus**, by `AnchorBusCheck`, run from the package that uses it
  (`Tools > Wirenook > Shape Collector > Verify` in party-tricks). Its
  `Client`, one client's copy of an avatar with the client's work done
  for it, is public, so a feature built on a bus can play its own layers
  the same way; adventure-tools does. Two
  copies of the generated animator play the wearer's client and that of a
  viewer who arrives after everything is placed. The check stands in for
  the three things only VRChat provides: it carries out the parameter
  drivers of each state an animator enters, works out the wearer's two
  contact readings from where the cursor is said to be, and copies the
  synced parameters across every so often, floats rounded to eight bits.
  With six slots it places them all, and the viewer has the whole layout
  to within 2.9 cm after one turn of the broadcast (5.4 s) when updates
  arrive every 0.1 s or every 0.45 s, and after 8.4 s when they arrive
  once a second. With the viewer's floats easing towards new values
  instead of jumping, one slot in one run needed a second turn. A clear
  reaches the viewer. All of it holds whether a float copied into an
  integer is rounded or truncated. On a bus that carries values, each
  slot is given one of its own and the viewer has to end up with those
  too; then the wearer changes one, and the viewer must have it within
  three seconds, read out once and not over and over. With fifteen slots
  (the kit for The Sunken Crypt in cass-avatar) the layout arrives in
  one turn of 14.3 s and a changed value in 1.0 s; with updates only
  once a second, fifteen slots do not all arrive, which the check
  reports and does not require.

Not checked, because it only runs in the VRChat client:

- **The bus's assumptions.** That a parameter driver on someone else's
  client copies a synced parameter into an unsynced one; that an avatar's
  synced parameters arrive together, so a slot's number never arrives
  ahead of its coordinates; and how often they arrive in a crowded
  instance. The check assumes the first two and varies the third.

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
then push a tag named for that version (`v` and the number). CI makes the GitHub release and attaches the `.zip`
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
