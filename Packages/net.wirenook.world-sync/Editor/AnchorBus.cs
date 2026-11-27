using System;
using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;
using Wirenook.Assets.Editor;
using Wirenook.WorldSync.Core;

namespace Wirenook.WorldSync.Editor
{
    /// <summary>
    /// Many places in the world, the same for everyone, for the synced cost
    /// of one.
    ///
    /// A <see cref="SyncedAnchor"/> spends sixty-six synced bits, seven
    /// constraints and six contacts on each thing it places, so four of them
    /// are more than an avatar may sync. A bus places any number of things
    /// ("slots") with one measuring rig and one channel:
    ///
    ///   Cursor   the one object that is ever measured. It follows the
    ///            wearer; to place a slot, the wearer's client freezes it,
    ///            reads its coordinates exactly as a synced anchor does, and
    ///            copies them into that slot's own parameters.
    ///   Slots    each slot's coordinates live in parameters of its own,
    ///            which are not synced. On the wearer's client they are
    ///            saved with the avatar, so a layout survives a restart.
    ///            Every client moves the slot from those parameters.
    ///   Channel  one set of synced parameters the size of one position. The
    ///            wearer's client puts each slot's coordinates on it in turn,
    ///            round and round, with the slot's number; every other client
    ///            copies what it hears into its own copy of that slot. Someone
    ///            who arrives late has the whole layout after one turn. A slot
    ///            that has just been placed jumps the queue, so the people
    ///            present have it a second or two after it is set down
    ///            however many slots there are.
    ///
    /// The cost of another slot is parameters that are not synced and states
    /// in the animator, which is why this is generated and not drawn.
    ///
    /// Slots have a position and stand upright in the world's axes; there is
    /// no heading. <see cref="WorldGrid"/> holds the arithmetic, as it does
    /// for a synced anchor.
    /// </summary>
    public sealed class AnchorBus
    {
        private static readonly string[] Axes = { "X", "Y", "Z" };

        /// <summary>Seconds the wearer's client lets the rig settle before it trusts a reading.</summary>
        public const float SettleSeconds = 0.3f;

        /// <summary>
        /// Seconds a slot's coordinates sit on the channel before its number
        /// goes up, the number stays up, and the channel rests between slots.
        /// At every instant the number is up, the coordinates beside it are
        /// that slot's, so a listener that reads both from one update can
        /// never pair a number with another slot's coordinates.
        /// </summary>
        public const float LoadSeconds = 0.3f;
        public const float AirSeconds = 0.5f;
        public const float GapSeconds = 0.15f;

        /// <summary>
        /// Seconds a listener waits after hearing a slot's number before it
        /// copies, in case a client eases synced floats towards new values
        /// rather than jumping to them.
        /// </summary>
        public const float HearSeconds = 0.2f;

        /// <summary>Synced bits the whole bus costs, whatever the number of slots.</summary>
        public const int SyncedBits = 8 + 3 * 8 + 3 * 8 + 1;

        private readonly GameObject _root;
        private readonly string _prefix;
        private readonly WorldGrid _grid;
        private readonly int _slots;

        private GameObject _cursor;
        private Transform _cursorCell;
        private readonly List<Transform> _cells = new List<Transform>();
        private readonly List<Transform> _poses = new List<Transform>();

        private AnchorBus(GameObject root, string prefix, int slots, float range)
        {
            if (slots < 1 || slots > 250) throw new ArgumentOutOfRangeException(nameof(slots), "A bus carries 1 to 250 slots.");
            _root = root;
            _prefix = prefix;
            _slots = slots;
            _grid = new WorldGrid(range);
        }

        public int Slots => _slots;
        public WorldGrid Grid => _grid;

        /// <summary>The object that is measured. It follows the carry points given to <see cref="Configure"/>.</summary>
        public Transform Cursor => _cursor.transform;

        /// <summary>
        /// Where slot <paramref name="slot"/> (counted from 1) stands: an
        /// object every client moves to the slot's place. Put things under
        /// it, or constrain them to it.
        /// </summary>
        public Transform Pose(int slot) => _poses[slot - 1];

        // The channel.
        public string OnAir => _prefix + "/Slot";
        public string Sent(string axis) => _prefix + "/Tx" + axis;
        public string SentFine(string axis) => _prefix + "/TxFine" + axis;
        public string SentPlaced => _prefix + "/TxSet";

        // One slot.
        public string Cell(int slot, string axis) => _prefix + "/" + slot + "/" + axis;
        public string Fine(int slot, string axis) => _prefix + "/" + slot + "/Fine" + axis;

        /// <summary>True once slot <paramref name="slot"/> has been placed, on every client that knows it has.</summary>
        public string Placed(int slot) => _prefix + "/" + slot + "/Set";

        // The cursor's measurement, on the wearer's client only.
        private string CursorCell(string axis) => _prefix + "/Cursor" + axis;
        private string CursorLatch(string axis) => _prefix + "/Cursor" + axis + "Latch";
        private string CoarseReading(string axis) => _prefix + "/Coarse_SenderLocalPosition" + axis;
        private string FineReading(string axis) => _prefix + "/Fine_SenderLocalPosition" + axis;

        /// <summary>
        /// On the wearer's client only: the number of the slot being
        /// placed, from the moment it is chosen until its coordinates have
        /// been stored, and 0 otherwise. A feature that waits for a
        /// placement to finish watches it go back to 0.
        /// </summary>
        public string Pending => _prefix + "/Pending";

        /// <summary>
        /// On the wearer's client only: the number of a slot that has been
        /// stored and not yet read out since, and 0 once the broadcast has
        /// turned to it. While the slot's number is on air and this is 0,
        /// what is on air is where the slot stands now.
        /// </summary>
        public string Fresh => _prefix + "/Fresh";

        /// <summary>
        /// Builds the rig under <paramref name="root"/>.
        /// </summary>
        /// <param name="prefix">Prefix for the bus's parameters, unique on an avatar.</param>
        /// <param name="slots">How many places it carries.</param>
        /// <param name="carryPoints">
        /// Where the cursor rides until something is placed: one or more
        /// points on the avatar. With several, <see cref="AddLayers"/> takes
        /// which one each slot is placed from.
        /// </param>
        /// <param name="range">Metres from the world origin a slot can be placed at, on each axis.</param>
        public static AnchorBus Configure(GameObject root, string prefix, int slots, Transform[] carryPoints, float range = 512f)
        {
            if (carryPoints == null || carryPoints.Length == 0) throw new ArgumentException("A bus needs a carry point.", nameof(carryPoints));
            var bus = new AnchorBus(root, prefix, slots, range);
            bus.Build(carryPoints);
            return bus;
        }

        private void Build(Transform[] carryPoints)
        {
            var originAsset = AssetDatabase.LoadAssetAtPath<GameObject>(SyncedAnchor.OriginPrefab);
            if (originAsset == null)
                throw new InvalidOperationException("Call SyncedAnchor.Prepare() before building a prefab with an anchor bus.");
            var origin = originAsset.transform;

            var world = FeaturePrefab.Child(FeaturePrefab.Child(_root, "Bus"), "World");
            Follow<VRCParentConstraint>(world, (origin, 1f));
            // Avatar scaling scales everything under the avatar; the world does not scale.
            Follow<VRCScaleConstraint>(world, (origin, 1f));

            _cursor = FeaturePrefab.Child(_root, "Cursor");
            var sources = new (Transform, float)[carryPoints.Length];
            for (var i = 0; i < carryPoints.Length; i++) sources[i] = (carryPoints[i], i == 0 ? 1f : 0f);
            Follow<VRCParentConstraint>(_cursor, sources);

            // The same two readings a synced anchor takes: which cell, then
            // where within it, the second against a cell the animator has
            // moved into place from the first.
            Receiver(world, "Coarse Receiver", _prefix + "/Coarse", "coarse");
            var coarseProxy = FeaturePrefab.Child(world, "Coarse Proxy");
            Follow<VRCPositionConstraint>(coarseProxy, (world.transform, _grid.CoarseOriginWeight), (_cursor.transform, 1f));
            Sender(coarseProxy, 2f, "coarse");

            _cursorCell = FeaturePrefab.Child(world, "Cursor Cell").transform;
            Receiver(_cursorCell.gameObject, "Fine Receiver", _prefix + "/Fine", "fine");
            var fineProxy = FeaturePrefab.Child(world, "Fine Proxy");
            Follow<VRCPositionConstraint>(fineProxy, (_cursorCell, _grid.FineOriginWeight), (_cursor.transform, 1f));
            Sender(fineProxy, 1f, "fine");

            for (var slot = 1; slot <= _slots; slot++)
            {
                var cell = FeaturePrefab.Child(world, "Slot " + slot + " Cell");
                _cells.Add(cell.transform);
                _poses.Add(FeaturePrefab.Child(cell, "Slot " + slot).transform);
            }
        }

        private static T Follow<T>(GameObject target, params (Transform source, float weight)[] sources) where T : VRCConstraintBase
        {
            var constraint = FeaturePrefab.Ensure<T>(target);
            constraint.ClearSources();
            foreach (var (source, weight) in sources)
                constraint.AddSource(new VRCConstraintSource { SourceTransform = source, Weight = weight });
            constraint.IsActive = true;
            // Unlocked constraints re-measure their offset instead of moving the object.
            constraint.Locked = true;
            constraint.FreezeToWorld = false;
            constraint.RebakeOffsetsWhenUnfrozen = false;
            return constraint;
        }

        /// <summary>Contacts pair up by tag. They are local-only: only the wearer's client measures.</summary>
        private string Tag(string reading) => "Wirenook/Bus/" + _prefix + "/" + reading;

        private void Sender(GameObject holder, float radius, string reading)
        {
            var sender = FeaturePrefab.Ensure<VRCContactSender>(holder);
            sender.shapeType = ContactBase.ShapeType.Sphere;
            sender.radius = radius;
            sender.localOnly = true;
            sender.collisionTags = new List<string> { Tag(reading) };
        }

        private void Receiver(GameObject parent, string name, string parameter, string reading)
        {
            var holder = FeaturePrefab.Child(parent, name);
            var receiver = FeaturePrefab.Ensure<VRCContactReceiver>(holder);
            receiver.shapeType = ContactBase.ShapeType.Sphere;
            receiver.radius = 3f;
            receiver.localOnly = true;
            receiver.allowSelf = true;
            receiver.allowOthers = false;
            receiver.collisionTags = new List<string> { Tag(reading) };
            receiver.receiverType = ContactReceiver.ReceiverType.Constant;
            receiver.parameter = parameter;
            receiver.advancedParameters = ContactReceiver.AdvancedParameter.SenderLocalPosition;
        }

        /// <summary>
        /// Adds the bus's layers to a feature's FX controller.
        /// </summary>
        /// <param name="place">
        /// An integer the feature sets to a slot's number to place that slot
        /// where the cursor is, and back to 0. A momentary menu button will
        /// do: the choice is remembered from the moment it is seen.
        /// </param>
        /// <param name="clear">A bool the feature raises to un-place every slot.</param>
        /// <param name="broadcast">A bool that, while true, has the wearer's client send the layout round.</param>
        /// <param name="carryFor">
        /// For each slot, which carry point (counted from 0) the cursor rides
        /// while that slot is being placed. Null when there is one.
        /// </param>
        public void AddLayers(ControllerBuilder fx, string place, string clear, string broadcast, int[] carryFor = null)
        {
            fx.Int(place).Bool(clear).Bool(broadcast).Bool("IsLocal").Float(SyncedAnchor.One, 1f)
                .Int(Pending).Int(Fresh).Float(OnAir).Bool(SentPlaced);
            foreach (var axis in Axes)
            {
                // Integers on the network, floats in the animator: blend trees
                // only take floats, and VRChat converts between the two.
                fx.Float(CursorCell(axis), WorldGrid.OriginIndex).Int(CursorLatch(axis), WorldGrid.OriginIndex)
                    .Float(CoarseReading(axis)).Float(FineReading(axis))
                    .Float(Sent(axis), WorldGrid.OriginIndex).Float(SentFine(axis));
            }

            for (var slot = 1; slot <= _slots; slot++)
            {
                fx.Bool(Placed(slot));
                foreach (var axis in Axes) fx.Float(Cell(slot, axis), WorldGrid.OriginIndex).Float(Fine(slot, axis));
            }

            DecodeLayer(fx);
            PlaceLayer(fx, place, clear, carryFor);
            BroadcastLayer(fx, broadcast);
            ListenLayer(fx);
        }

        /// <summary>Moves the cursor's cell and every slot from their parameters, on every client.</summary>
        private void DecodeLayer(ControllerBuilder fx)
        {
            var trees = new List<Motion>();
            for (var i = 0; i < Axes.Length; i++)
            {
                var component = "m_LocalPosition." + Axes[i].ToLowerInvariant();
                trees.Add(Slide(fx, _cursorCell, component, CursorCell(Axes[i]), 0f, -_grid.Range, WorldGrid.MaxIndex, _grid.Range));
                for (var slot = 1; slot <= _slots; slot++)
                {
                    trees.Add(Slide(fx, _cells[slot - 1], component, Cell(slot, Axes[i]), 0f, -_grid.Range, WorldGrid.MaxIndex, _grid.Range));
                    trees.Add(Slide(fx, _poses[slot - 1], component, Fine(slot, Axes[i]),
                        -WorldGrid.FineReach, -_grid.Cell, WorldGrid.FineReach, _grid.Cell));
                }
            }

            fx.Layer(_prefix + " Decode").State("Decode", fx.Direct(_prefix + " decode", SyncedAnchor.One, trees.ToArray()));
        }

        /// <summary>One coordinate of one object, moved in a straight line by one parameter.</summary>
        private BlendTree Slide(ControllerBuilder fx, Transform target, string property, string parameter,
            float low, float lowMetres, float high, float highMetres)
        {
            var path = FeaturePrefab.PathTo(_root, target);
            AnimationClip At(float metres) => fx.Clip(_prefix + " " + target.name + " " + property + " " + metres.ToString("0.###"),
                clip => clip.Constant(path, typeof(Transform), property, metres));
            return fx.Tree(_prefix + " " + target.name + " " + property, parameter, (low, At(lowMetres)), (high, At(highMetres)));
        }

        /// <summary>
        /// The wearer's client places a slot: freeze the cursor, let the
        /// coarse reading settle and fix the cell, let the fine reading settle
        /// against it, and copy both into the slot.
        /// </summary>
        private void PlaceLayer(ControllerBuilder fx, string place, string clear, int[] carryFor)
        {
            var cursorPath = FeaturePrefab.PathTo(_root, _cursor.transform);
            var carries = _cursor.GetComponent<VRCParentConstraint>().Sources.Count;

            // Which carry point the cursor rides, and whether it is held still.
            AnimationClip Ride(int carry, bool frozen, float seconds = 1f / 60f) =>
                fx.Clip(_prefix + " cursor " + carry + (frozen ? " held " : " riding ") + seconds.ToString("0.###"), clip =>
                {
                    for (var source = 0; source < carries; source++)
                    {
                        clip.Constant(cursorPath, typeof(VRCParentConstraint), "Sources.source" + source + ".Weight",
                            source == carry ? 1f : 0f, seconds);
                    }

                    clip.Constant(cursorPath, typeof(VRCParentConstraint), "FreezeToWorld", frozen ? 1f : 0f, seconds);
                });

            var layer = fx.Layer(_prefix + " Place");
            var idle = layer.State("Idle", Ride(0, false));
            // Lets go of the choice only once the button is up, so one press places once.
            var done = layer.State("Done", Ride(0, false));
            done.Behaviour<VRCAvatarParameterDriver>(driver => Drive(driver, Set(Pending, 0f)));
            done.To(idle).Equal(place, 0);

            var clearing = layer.State("Clearing", Ride(0, false));
            clearing.Behaviour<VRCAvatarParameterDriver>(driver =>
            {
                var changes = new List<VRC_AvatarParameterDriver.Parameter>();
                for (var slot = 1; slot <= _slots; slot++) changes.Add(Set(Placed(slot), 0f));
                Drive(driver, changes.ToArray());
            });
            idle.To(clearing).IfTrue("IsLocal").IfTrue(clear);
            clearing.To(idle).IfFalse(clear);

            // The choice is remembered at once: a menu button is only down
            // for as long as a finger is.
            var chosen = layer.State("Chosen", Ride(0, false));
            chosen.Behaviour<VRCAvatarParameterDriver>(driver => Drive(driver, Copy(place, Pending)));
            idle.To(chosen).IfTrue("IsLocal").NotEqual(place, 0);

            for (var slot = 1; slot <= _slots; slot++)
            {
                var carry = carryFor != null ? carryFor[slot - 1] : 0;
                // A moment on the right carry point before it is held there.
                var aim = layer.State("Aim " + slot, Ride(carry, false, 0.1f));
                var measure = layer.State("Measure " + slot, Ride(carry, true, SettleSeconds));
                var latch = layer.State("Latch " + slot, Ride(carry, true, SettleSeconds));
                var store = layer.State("Store " + slot, Ride(carry, true));

                latch.Behaviour<VRCAvatarParameterDriver>(driver =>
                {
                    var changes = new List<VRC_AvatarParameterDriver.Parameter>();
                    foreach (var axis in Axes)
                    {
                        // Into an integer first, so the cell index is a whole
                        // number here exactly as it will be on the network.
                        changes.Add(new VRC_AvatarParameterDriver.Parameter
                        {
                            type = VRC_AvatarParameterDriver.ChangeType.Copy,
                            source = CoarseReading(axis),
                            name = CursorLatch(axis),
                            convertRange = true,
                            sourceMin = -WorldGrid.CoarseReach,
                            sourceMax = WorldGrid.CoarseReach,
                            destMin = 0f,
                            destMax = WorldGrid.MaxIndex,
                        });
                        changes.Add(Copy(CursorLatch(axis), CursorCell(axis)));
                    }

                    Drive(driver, changes.ToArray());
                });

                var index = slot;
                store.Behaviour<VRCAvatarParameterDriver>(driver =>
                {
                    var changes = new List<VRC_AvatarParameterDriver.Parameter>();
                    foreach (var axis in Axes)
                    {
                        changes.Add(Copy(CursorCell(axis), Cell(index, axis)));
                        changes.Add(Copy(FineReading(axis), Fine(index, axis)));
                    }

                    changes.Add(Set(Placed(index), 1f));
                    // Read this one out next.
                    changes.Add(Set(Fresh, index));
                    Drive(driver, changes.ToArray());
                });

                chosen.To(aim).Equal(Pending, slot);
                aim.To(measure).AfterTime(1f);
                measure.To(latch).AfterTime(1f);
                latch.To(store).AfterTime(1f);
                store.To(done).AfterTime(0f);
            }

            // A number with no slot behind it is dropped.
            chosen.To(done).Greater(Pending, _slots).Or().Less(Pending, 1);
        }

        /// <summary>
        /// The wearer's client reads the layout out, one slot at a time,
        /// for as long as it is asked to. A slot stored since it was last
        /// read out (<see cref="Fresh"/>) is taken next, whichever slot's
        /// turn it was, and the round goes on from there.
        /// </summary>
        private void BroadcastLayer(ControllerBuilder fx, string broadcast)
        {
            var layer = fx.Layer(_prefix + " Broadcast");
            var quiet = layer.State("Quiet");
            quiet.Behaviour<VRCAvatarParameterDriver>(driver => Drive(driver, Set(OnAir, 0f)));

            var load = fx.Clip(_prefix + " load", clip => clip.Hold(LoadSeconds));
            var air = fx.Clip(_prefix + " air", clip => clip.Hold(AirSeconds));
            var gap = fx.Clip(_prefix + " gap", clip => clip.Hold(GapSeconds));

            var loading = new List<StateBuilder>();
            var resting = new List<StateBuilder>();
            var jumping = new List<StateBuilder>();
            for (var slot = 1; slot <= _slots; slot++)
            {
                var index = slot;
                var loads = layer.State("Load " + slot, load);
                loads.Behaviour<VRCAvatarParameterDriver>(driver =>
                {
                    var changes = new List<VRC_AvatarParameterDriver.Parameter>();
                    foreach (var axis in Axes)
                    {
                        changes.Add(Copy(Cell(index, axis), Sent(axis)));
                        changes.Add(Copy(Fine(index, axis), SentFine(axis)));
                    }

                    changes.Add(Copy(Placed(index), SentPlaced));
                    Drive(driver, changes.ToArray());
                });
                var onAir = layer.State("Air " + slot, air);
                onAir.Behaviour<VRCAvatarParameterDriver>(driver => Drive(driver, Set(OnAir, index)));
                var rest = layer.State("Gap " + slot, gap);
                rest.Behaviour<VRCAvatarParameterDriver>(driver => Drive(driver, Set(OnAir, 0f)));

                // The way in for a slot that jumps the queue. It lets go of
                // the request as it loads: whatever is stored from here on
                // asks again.
                var jump = layer.State("Jump " + slot);
                jump.Behaviour<VRCAvatarParameterDriver>(driver => Drive(driver, Set(Fresh, 0f)));
                jump.To(loads).AfterTime(0f);

                loads.To(onAir).AfterTime(1f);
                onAir.To(rest).AfterTime(1f);
                loading.Add(loads);
                resting.Add(rest);
                jumping.Add(jump);
            }

            // Transitions are tried in the order they were added. Told to
            // stop, it finishes the slot it is on first, so the number never
            // comes down in the middle of a slot; then a slot that has just
            // been stored; then the next in the round.
            for (var slot = 1; slot <= _slots; slot++)
            {
                var rest = resting[slot - 1];
                rest.To(quiet).AfterTime(1f).IfFalse(broadcast);
                for (var fresh = 1; fresh <= _slots; fresh++) rest.To(jumping[fresh - 1]).AfterTime(1f).Equal(Fresh, fresh);
                rest.To(loading[slot % _slots]).AfterTime(1f).IfTrue(broadcast);
            }

            for (var fresh = 1; fresh <= _slots; fresh++)
                quiet.To(jumping[fresh - 1]).IfTrue("IsLocal").IfTrue(broadcast).Equal(Fresh, fresh);
            quiet.To(loading[0]).IfTrue("IsLocal").IfTrue(broadcast);
        }

        /// <summary>Every other client copies what it hears into its own copy of the slot named.</summary>
        private void ListenLayer(ControllerBuilder fx)
        {
            var layer = fx.Layer(_prefix + " Listen");
            var listening = layer.State("Listening");
            var hear = fx.Clip(_prefix + " hear", clip => clip.Hold(HearSeconds));
            for (var slot = 1; slot <= _slots; slot++)
            {
                var index = slot;
                var hearing = layer.State("Hear " + slot, hear);
                var take = layer.State("Take " + slot);
                take.Behaviour<VRCAvatarParameterDriver>(driver =>
                {
                    var changes = new List<VRC_AvatarParameterDriver.Parameter>();
                    foreach (var axis in Axes)
                    {
                        changes.Add(Copy(Sent(axis), Cell(index, axis)));
                        changes.Add(Copy(SentFine(axis), Fine(index, axis)));
                    }

                    changes.Add(Copy(SentPlaced, Placed(index)));
                    // On every client but the wearer's: these are the copies.
                    Drive(driver, false, changes.ToArray());
                });

                // The number is a float in the animator, so "equal" is a
                // narrow band round it.
                listening.To(hearing).IfFalse("IsLocal").Greater(OnAir, slot - 0.5f).Less(OnAir, slot + 0.5f);
                hearing.To(listening).Less(OnAir, slot - 0.5f).Or().Greater(OnAir, slot + 0.5f);
                hearing.To(take).AfterTime(1f);
                take.To(listening).Less(OnAir, slot - 0.5f).Or().Greater(OnAir, slot + 0.5f);
            }
        }

        private static VRC_AvatarParameterDriver.Parameter Set(string name, float value)
        {
            return new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = name, value = value };
        }

        private static VRC_AvatarParameterDriver.Parameter Copy(string source, string name)
        {
            return new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Copy, source = source, name = name };
        }

        private static void Drive(VRCAvatarParameterDriver driver, params VRC_AvatarParameterDriver.Parameter[] changes)
        {
            Drive(driver, true, changes);
        }

        private static void Drive(VRCAvatarParameterDriver driver, bool localOnly, params VRC_AvatarParameterDriver.Parameter[] changes)
        {
            driver.localOnly = localOnly;
            driver.parameters = new List<VRC_AvatarParameterDriver.Parameter>(changes);
        }

        /// <summary>
        /// The bus's expression parameters, for the feature's Modular Avatar
        /// declaration. The place, clear and broadcast parameters are the
        /// caller's.
        /// </summary>
        public IEnumerable<ParameterConfig> Parameters()
        {
            yield return Synced(OnAir, ParameterSyncType.Int, 0f);
            yield return Synced(SentPlaced, ParameterSyncType.Bool, 0f);
            foreach (var axis in Axes)
            {
                yield return Synced(Sent(axis), ParameterSyncType.Int, WorldGrid.OriginIndex);
                yield return Synced(SentFine(axis), ParameterSyncType.Float, 0f);
                yield return AnimatorOnly(CoarseReading(axis), 0f);
                yield return AnimatorOnly(FineReading(axis), 0f);
                yield return AnimatorOnly(CursorLatch(axis), WorldGrid.OriginIndex);
                yield return AnimatorOnly(CursorCell(axis), WorldGrid.OriginIndex);
            }

            yield return AnimatorOnly(Pending, 0f);
            yield return AnimatorOnly(Fresh, 0f);
            yield return AnimatorOnly(SyncedAnchor.One, 1f);

            // Kept with the avatar on the wearer's client, and never sent:
            // the channel is how they travel.
            for (var slot = 1; slot <= _slots; slot++)
            {
                yield return Kept(Placed(slot), ParameterSyncType.Bool, 0f);
                foreach (var axis in Axes)
                {
                    yield return Kept(Cell(slot, axis), ParameterSyncType.Int, WorldGrid.OriginIndex);
                    yield return Kept(Fine(slot, axis), ParameterSyncType.Float, 0f);
                }
            }
        }

        private static ParameterConfig Synced(string name, ParameterSyncType type, float defaultValue)
        {
            return new ParameterConfig { nameOrPrefix = name, syncType = type, defaultValue = defaultValue, hasExplicitDefaultValue = true };
        }

        private static ParameterConfig Kept(string name, ParameterSyncType type, float defaultValue)
        {
            return new ParameterConfig
            {
                nameOrPrefix = name, syncType = type, localOnly = true, saved = true,
                defaultValue = defaultValue, hasExplicitDefaultValue = true,
            };
        }

        private static ParameterConfig AnimatorOnly(string name, float defaultValue)
        {
            return new ParameterConfig
            {
                nameOrPrefix = name, syncType = ParameterSyncType.NotSynced, localOnly = true,
                defaultValue = defaultValue, hasExplicitDefaultValue = true,
            };
        }
    }
}
