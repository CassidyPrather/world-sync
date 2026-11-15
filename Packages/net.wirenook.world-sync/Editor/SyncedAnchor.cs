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
    /// An anchor whose dropped position is the same for everyone, including
    /// people who arrive later.
    ///
    /// The wearer's client is the source of truth: it freezes the anchor where
    /// it was dropped, reads that spot's world coordinates, and syncs them.
    /// Every other client places the anchor from the synced coordinates.
    ///
    ///   World         a frame held at the world's origin and axes
    ///   Coarse Proxy  the anchor's position scaled down toward the origin,
    ///                 so a contact at the origin can read it
    ///   Coarse        moved to the cell the anchor is in, from synced integers
    ///   Fine Proxy    the anchor's position within that cell, scaled likewise
    ///   Fine          moved within the cell, from synced floats
    ///   Pose          turned to the anchor's heading; where remote anchors go
    ///
    /// The readings come from contact receivers' sender-position output, one
    /// receiver per reading. Scaling is done by constraints with two weighted
    /// sources, which place an object at a weighted average of them.
    /// <see cref="WorldGrid"/> holds the arithmetic and its tests.
    /// </summary>
    public sealed class SyncedAnchor
    {
        public const string Package = "Packages/net.wirenook.world-sync";

        /// <summary>
        /// An empty prefab that is never placed in a scene. A constraint whose
        /// source is an object outside the scene resolves to the origin, with
        /// no rotation and unit scale: that is the world frame. The technique
        /// is the one VRLabs' World Constraint established.
        /// </summary>
        public const string OriginPrefab = Package + "/Prefabs/Support/World Origin.prefab";

        /// <summary>A float that is always 1, for weighting direct blend trees.</summary>
        public const string One = "Wirenook/One";

        private static readonly string[] Axes = { "X", "Y", "Z" };

        /// <summary>Seconds the wearer's client lets the rig settle before it trusts a reading.</summary>
        private const float SettleSeconds = 0.3f;

        private readonly GameObject _root;
        private readonly GameObject _anchor;
        private readonly string _prefix;
        private readonly WorldGrid _grid;

        private Transform _coarse;
        private Transform _fine;
        private Transform _yawTarget;

        private SyncedAnchor(GameObject root, GameObject anchor, string prefix, float range)
        {
            _root = root;
            _anchor = anchor;
            _prefix = prefix;
            _grid = new WorldGrid(range);
        }

        private string Coarse(string axis) => _prefix + "/" + axis;
        private string Latch(string axis) => _prefix + "/" + axis + "Latch";
        private string CoarseReading(string axis) => _prefix + "/Coarse" + SenderPosition(axis);
        private string Fine(string axis) => _prefix + "/Fine" + SenderPosition(axis);
        private string Heading(string axis) => _prefix + "/Heading" + SenderPosition(axis);
        private string Ready => _prefix + "/Ready";

        /// <summary>The suffix a contact receiver adds to its parameter for the sender's position on one axis.</summary>
        private static string SenderPosition(string axis) => "_SenderLocalPosition" + axis;

        /// <summary>Synced bits one anchor costs besides its own drop switch.</summary>
        public const int SyncedBits = 3 * 8 + 3 * 8 + 2 * 8 + 1;

        /// <summary>
        /// Builds the rig under <paramref name="root"/> for an anchor that
        /// follows <paramref name="carry"/> until dropped.
        /// </summary>
        /// <param name="prefix">Prefix for the rig's parameters, unique per anchor on an avatar.</param>
        /// <param name="range">Metres from the world origin the anchor can be dropped at, on each axis.</param>
        public static SyncedAnchor Configure(GameObject root, GameObject anchor, Transform carry, string prefix, float range = 512f)
        {
            var rig = new SyncedAnchor(root, anchor, prefix, range);
            rig.Build(carry);
            return rig;
        }

        /// <summary>
        /// Creates the world-origin prefab if the package does not have it
        /// yet. Call before editing any prefab that will hold a synced anchor;
        /// one prefab cannot be created while another is open for editing.
        /// </summary>
        public static void Prepare()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(OriginPrefab) == null)
                FeaturePrefab.Edit(OriginPrefab, origin => { });
        }

        private void Build(Transform carry)
        {
            var originAsset = AssetDatabase.LoadAssetAtPath<GameObject>(OriginPrefab);
            if (originAsset == null)
                throw new InvalidOperationException("Call SyncedAnchor.Prepare() before building a prefab with a synced anchor.");
            var origin = originAsset.transform;

            var world = FeaturePrefab.Child(FeaturePrefab.Child(_root, "Sync"), "World");
            Follow<VRCParentConstraint>(world, (origin, 1f));
            // Avatar scaling scales everything under the avatar; the world does not scale.
            Follow<VRCScaleConstraint>(world, (origin, 1f));

            // Coarse: where in the world, to the nearest cell.
            Receiver(world, "Coarse Receiver", _prefix + "/Coarse", 3f, "coarse");
            var coarseProxy = FeaturePrefab.Child(world, "Coarse Proxy");
            Follow<VRCPositionConstraint>(coarseProxy, (world.transform, _grid.CoarseOriginWeight), (_anchor.transform, 1f));
            Sender(coarseProxy, 2f, "coarse");

            _coarse = FeaturePrefab.Child(world, "Coarse").transform;

            // Fine: where within the cell.
            Receiver(_coarse.gameObject, "Fine Receiver", _prefix + "/Fine", 3f, "fine");
            var fineProxy = FeaturePrefab.Child(world, "Fine Proxy");
            Follow<VRCPositionConstraint>(fineProxy, (_coarse, _grid.FineOriginWeight), (_anchor.transform, 1f));
            Sender(fineProxy, 1f, "fine");

            _fine = FeaturePrefab.Child(_coarse.gameObject, "Fine").transform;

            // Heading: a point one metre ahead of the anchor, read from a
            // receiver that sits on the anchor but keeps the world's axes. Its
            // x and z are the sine and cosine of the anchor's heading.
            var ahead = FeaturePrefab.Child(_anchor, "Heading Sender");
            ahead.transform.localPosition = Vector3.forward;
            Sender(ahead, 0.5f, "heading");
            var probe = FeaturePrefab.Child(world, "Heading Probe");
            Follow<VRCPositionConstraint>(probe, (_anchor.transform, 1f));
            Receiver(probe, "Heading Receiver", _prefix + "/Heading", 1.5f, "heading");

            _yawTarget = FeaturePrefab.Child(_fine.gameObject, "Heading Target").transform;
            _yawTarget.localPosition = Vector3.forward;
            var pose = FeaturePrefab.Child(_fine.gameObject, "Pose");
            var aim = Follow<VRCAimConstraint>(pose, (_yawTarget, 1f));
            aim.AimAxis = Vector3.forward;
            aim.UpAxis = Vector3.up;
            aim.WorldUp = VRCConstraintBase.WorldUpType.SceneUp;

            // The anchor itself: on the carry point until dropped, then held
            // in place (the wearer) or moved to the synced pose (everyone else).
            Follow<VRCParentConstraint>(_anchor, (carry, 1f), (pose.transform, 0f));
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

        /// <summary>
        /// Contacts pair up by tag, so each reading gets its own. They are
        /// local-only: only the wearer's client measures, which is also why
        /// they cost nothing towards the avatar's performance rank.
        /// </summary>
        private string Tag(string reading) => "Wirenook/Sync/" + _prefix + "/" + reading;

        private void Sender(GameObject holder, float radius, string reading)
        {
            var sender = FeaturePrefab.Ensure<VRCContactSender>(holder);
            sender.shapeType = ContactBase.ShapeType.Sphere;
            sender.radius = radius;
            sender.localOnly = true;
            sender.collisionTags = new List<string> { Tag(reading) };
        }

        private void Receiver(GameObject parent, string name, string parameter, float radius, string reading)
        {
            var holder = FeaturePrefab.Child(parent, name);
            var receiver = FeaturePrefab.Ensure<VRCContactReceiver>(holder);
            receiver.shapeType = ContactBase.ShapeType.Sphere;
            receiver.radius = radius;
            receiver.localOnly = true;
            receiver.allowSelf = true;
            receiver.allowOthers = false;
            receiver.collisionTags = new List<string> { Tag(reading) };
            receiver.receiverType = ContactReceiver.ReceiverType.Constant;
            receiver.parameter = parameter;
            receiver.advancedParameters = ContactReceiver.AdvancedParameter.SenderLocalPosition;
        }

        /// <summary>
        /// The usual pairing: a prop that pops into view at the carry point
        /// and is dropped there in the same instant, then picked up again when
        /// it is switched off. Adds the pop layer and the rig's own layers.
        /// </summary>
        /// <param name="dropParameter">The synced bool that shows and drops the prop.</param>
        /// <param name="propPath">Animation path of the object that scales the prop in and out.</param>
        /// <param name="extras">Further curves per pop phase ("off", "appear", "on", "disappear").</param>
        public PopToggle.States DropLayers(ControllerBuilder fx, string layerName, string dropParameter, string propPath,
            Action<ClipBuilder, string> extras = null)
        {
            var states = PopToggle.Layer(fx, layerName, dropParameter, propPath, extras);
            // The prop shrinks away where it stands; only then is it let go.
            AddLayers(fx, dropParameter, PopToggle.DisappearSeconds);
            return states;
        }

        /// <summary>
        /// Adds the rig's two layers to a feature's FX controller.
        /// </summary>
        /// <param name="dropParameter">The synced bool that drops the anchor.</param>
        /// <param name="releaseDelay">
        /// Seconds the anchor stays where it was dropped after the switch goes
        /// off, for whatever plays in place before it returns to the carry point.
        /// </param>
        public void AddLayers(ControllerBuilder fx, string dropParameter, float releaseDelay = 0f)
        {
            fx.Bool(dropParameter).Bool(Ready).Bool("IsLocal").Float(One, 1f);
            foreach (var axis in Axes)
            {
                // Integers on the network, floats in the animator: blend trees
                // only take floats, and VRChat converts between the two.
                fx.Float(Coarse(axis), WorldGrid.OriginIndex).Int(Latch(axis), WorldGrid.OriginIndex)
                    .Float(CoarseReading(axis)).Float(Fine(axis));
            }

            fx.Float(Heading("X")).Float(Heading("Z"), 1f);

            DecodeLayer(fx);
            AnchorLayer(fx, dropParameter, releaseDelay);
        }

        /// <summary>
        /// Moves Coarse, Fine and the heading target from the synced values, on
        /// every client. The wearer's client needs Coarse as well: the fine
        /// reading is taken against it, so both ends work from the same cell.
        /// </summary>
        private void DecodeLayer(ControllerBuilder fx)
        {
            var trees = new List<Motion>();
            for (var i = 0; i < Axes.Length; i++)
            {
                var component = "m_LocalPosition." + Axes[i].ToLowerInvariant();
                trees.Add(Slide(fx, _coarse, component, Coarse(Axes[i]), 0f, -_grid.Range, WorldGrid.MaxIndex, _grid.Range));
                trees.Add(Slide(fx, _fine, component, Fine(Axes[i]), -WorldGrid.FineReach, -_grid.Cell, WorldGrid.FineReach, _grid.Cell));
            }

            trees.Add(Slide(fx, _yawTarget, "m_LocalPosition.x", Heading("X"), -1f, -1f, 1f, 1f));
            trees.Add(Slide(fx, _yawTarget, "m_LocalPosition.z", Heading("Z"), -1f, -1f, 1f, 1f));

            var layer = fx.Layer(_prefix + " Decode");
            layer.State("Decode", fx.Direct(_prefix + " decode", One, trees.ToArray()));
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

        private void AnchorLayer(ControllerBuilder fx, string dropParameter, float releaseDelay)
        {
            var anchorPath = FeaturePrefab.PathTo(_root, _anchor.transform);

            AnimationClip Hold(string name, bool carried, bool synced, bool frozen, float seconds = 1f / 60f) =>
                fx.Clip(_prefix + " anchor " + name, clip => clip
                    .Constant(anchorPath, typeof(VRCParentConstraint), "Sources.source0.Weight", carried ? 1f : 0f, seconds)
                    .Constant(anchorPath, typeof(VRCParentConstraint), "Sources.source1.Weight", synced ? 1f : 0f, seconds)
                    .Constant(anchorPath, typeof(VRCParentConstraint), "FreezeToWorld", frozen ? 1f : 0f, seconds));

            var layer = fx.Layer(_prefix + " Anchor");
            var carried = layer.State("Carried", Hold("carried", true, false, false));

            // The wearer: freeze, let the readings settle, fix the cell, let the
            // fine reading settle against it, then tell everyone it is ready.
            var measure = layer.State("Measure", Hold("measure", true, false, true, SettleSeconds));
            var latch = layer.State("Latch", Hold("latch", true, false, true, SettleSeconds));
            var published = layer.State("Published", Hold("published", true, false, true));
            var release = layer.State("Release", Hold("release", true, false, false));

            latch.Behaviour<VRCAvatarParameterDriver>(driver =>
            {
                driver.localOnly = true;
                driver.parameters = new List<VRC_AvatarParameterDriver.Parameter>();
                foreach (var axis in Axes)
                {
                    // Into an integer first, so the cell index is a whole
                    // number here exactly as it will be on the network, then
                    // into the float the blend trees read.
                    driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter
                    {
                        type = VRC_AvatarParameterDriver.ChangeType.Copy,
                        source = CoarseReading(axis),
                        name = Latch(axis),
                        convertRange = true,
                        sourceMin = -WorldGrid.CoarseReach,
                        sourceMax = WorldGrid.CoarseReach,
                        destMin = 0f,
                        destMax = WorldGrid.MaxIndex,
                    });
                    driver.parameters.Add(new VRC_AvatarParameterDriver.Parameter
                    {
                        type = VRC_AvatarParameterDriver.ChangeType.Copy,
                        source = Latch(axis),
                        name = Coarse(axis),
                    });
                }
            });
            published.Behaviour<VRCAvatarParameterDriver>(driver => Set(driver, Ready, 1f));
            release.Behaviour<VRCAvatarParameterDriver>(driver => Set(driver, Ready, 0f));

            carried.To(measure).IfTrue(dropParameter).IfTrue("IsLocal");
            measure.To(latch).AfterTime(1f);
            latch.To(published).AfterTime(1f);
            // Picked up again: stay put for the release delay, then let go.
            var lingers = releaseDelay > 0f;
            var leaving = lingers ? layer.State("Leaving", Hold("leaving", true, false, true, releaseDelay)) : release;
            foreach (var state in new[] { measure, latch, published })
                state.To(leaving).IfFalse(dropParameter);
            if (lingers) leaving.To(release).AfterTime(1f);
            release.To(carried).IfFalse(dropParameter);

            // Everyone else: hold the anchor where it was seen dropped until
            // the synced position is ready, then go to it. Someone who arrives
            // later finds it ready and goes straight there.
            var held = layer.State("Held", Hold("held", true, false, true));
            var synced = layer.State("Synced", Hold("synced", false, true, false));
            carried.To(synced).IfTrue(dropParameter).IfFalse("IsLocal").IfTrue(Ready);
            carried.To(held).IfTrue(dropParameter).IfFalse("IsLocal").IfFalse(Ready);
            held.To(synced).Fade(0.25f).IfTrue(Ready);
            if (lingers)
            {
                var heldLeaving = layer.State("Held Leaving", Hold("held leaving", true, false, true, releaseDelay));
                var syncedLeaving = layer.State("Synced Leaving", Hold("synced leaving", false, true, false, releaseDelay));
                held.To(heldLeaving).IfFalse(dropParameter);
                synced.To(syncedLeaving).IfFalse(dropParameter);
                heldLeaving.To(carried).AfterTime(1f);
                syncedLeaving.To(carried).AfterTime(1f);
            }
            else
            {
                held.To(carried).IfFalse(dropParameter);
                synced.To(carried).IfFalse(dropParameter);
            }
        }

        private static void Set(VRCAvatarParameterDriver driver, string parameter, float value)
        {
            driver.localOnly = true;
            driver.parameters = new List<VRC_AvatarParameterDriver.Parameter>
            {
                new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = parameter, value = value },
            };
        }

        /// <summary>
        /// The rig's expression parameters, for the feature's Modular Avatar
        /// declaration. The drop switch itself is the caller's.
        /// </summary>
        public IEnumerable<ParameterConfig> Parameters()
        {
            yield return FeaturePrefab.SyncedBool(Ready);
            foreach (var axis in Axes)
            {
                yield return FeaturePrefab.SyncedInt(Coarse(axis), WorldGrid.OriginIndex);
                yield return SyncedFloat(Fine(axis), 0f);
                yield return Unsynced(CoarseReading(axis), 0f);
                yield return Unsynced(Latch(axis), WorldGrid.OriginIndex);
            }

            yield return SyncedFloat(Heading("X"), 0f);
            yield return SyncedFloat(Heading("Z"), 1f);
            yield return Unsynced(One, 1f);
        }

        private static ParameterConfig SyncedFloat(string name, float defaultValue)
        {
            return new ParameterConfig
            {
                nameOrPrefix = name,
                syncType = ParameterSyncType.Float,
                defaultValue = defaultValue,
                hasExplicitDefaultValue = true,
            };
        }

        private static ParameterConfig Unsynced(string name, float defaultValue)
        {
            return new ParameterConfig
            {
                nameOrPrefix = name,
                syncType = ParameterSyncType.NotSynced,
                localOnly = true,
                defaultValue = defaultValue,
                hasExplicitDefaultValue = true,
            };
        }

        /// <summary>The grid this anchor's coordinates are sent on.</summary>
        public WorldGrid Grid => _grid;

        /// <summary>The synced parameter carrying the cell index on one axis ("X", "Y" or "Z").</summary>
        public string CoarseParameter(string axis) => Coarse(axis);

        /// <summary>The synced parameter carrying the position within the cell on one axis.</summary>
        public string FineParameter(string axis) => Fine(axis);

        /// <summary>The synced parameters carrying the sine ("X") and cosine ("Z") of the heading.</summary>
        public string HeadingParameter(string axis) => Heading(axis);

        public string ReadyParameter => Ready;
    }
}
