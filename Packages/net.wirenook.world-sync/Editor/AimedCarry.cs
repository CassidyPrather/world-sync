using System.Collections.Generic;
using nadena.dev.modular_avatar.core;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDKBase;
using Wirenook.Assets;
using Wirenook.Assets.Editor;

namespace Wirenook.WorldSync.Editor
{
    /// <summary>
    /// A carry point that is aimed: the spot a hand points at, for a prop
    /// that is to be put down somewhere in particular and not a step in
    /// front of the wearer.
    ///
    /// Switching the prop on no longer drops it. It shows it to the wearer
    /// alone, standing where the hand points and turned to face them, and
    /// following as the hand moves; closing that hand into a fist (a trigger
    /// pull, on most controllers) sets it down, and from then on the anchor
    /// it is given to holds it for everyone. Switching it off before that
    /// cancels; switching it off afterwards picks it up, as before.
    ///
    ///   Mount/Grip/Ray  a VRCRaycast along the hand, against the world only
    ///   Spot            where the ray ends; the component moves it there
    ///   Spot/Facing     turned about the vertical to face the wearer
    ///   Level           the wearer's place at the spot's height, to aim that at
    ///   Carry           on Facing while the ray hits something, and on the
    ///                   fallback point (a step in front) while it does not
    ///
    /// The ray is cast by every viewer's client from the hand as it sees it,
    /// so nothing of the aiming is synced: one synced bit says "set down
    /// now". What other people see while the wearer aims is nothing; the
    /// prop appears for them when it is set down.
    ///
    /// While aiming, the hand's gestures are buttons, and the rig says so
    /// with a <see cref="WirenookHandClaim"/> so that an avatar which reads
    /// those does not pull a face at the click.
    ///
    /// None of this has run in VRChat. In particular, whether a raycast's
    /// result is in place before constraints read it within one frame is
    /// not documented; if it is not, the preview trails the hand by a frame.
    /// </summary>
    public sealed class AimedCarry
    {
        private readonly GameObject _root;
        private readonly string _prefix;
        private readonly HumanBodyBones _hand;
        private Transform _ray;
        private Transform _carry;

        /// <summary>The transform to give an anchor as its carry point.</summary>
        public Transform Carry => _carry;

        /// <summary>Synced: the prop has been set down. The anchor's drop switch.</summary>
        public string Dropped => _prefix + "/Set";

        /// <summary>Worked out on every client: the prop is to be drawn here. The pop toggle's switch.</summary>
        public string Visible => _prefix + "/Shown";

        /// <summary>Worked out on every client: the wearer is aiming, and the hand is a button.</summary>
        public string Aiming => _prefix + "/Aiming";

        private string RayParameter => _prefix + "/Ray";
        private string Hit => RayParameter + VRCRaycast.PARAM_HIT;
        private string Gesture => _hand == HumanBodyBones.LeftHand ? "GestureLeft" : "GestureRight";

        private AimedCarry(GameObject root, string prefix, HumanBodyBones hand)
        {
            _root = root;
            _prefix = prefix;
            _hand = hand;
        }

        /// <param name="fallback">Where the prop is carried while the ray hits nothing: the old carry point.</param>
        /// <param name="prefix">Prefix for the rig's parameters, unique per prop on an avatar.</param>
        /// <param name="reach">How far the hand can point, in metres.</param>
        public static AimedCarry Configure(GameObject root, Transform fallback, string prefix,
            HumanBodyBones hand = HumanBodyBones.RightHand, float reach = 10f)
        {
            var rig = new AimedCarry(root, prefix, hand);
            rig.Build(fallback, reach);
            return rig;
        }

        private void Build(Transform fallback, float reach)
        {
            var aim = FeaturePrefab.Child(_root, "Aim");
            var mount = FeaturePrefab.Child(aim, "Mount");
            FeaturePrefab.AttachToBone(mount, _hand);
            // Fitted to the avatar's own fingers like any held tool, so the ray leaves along them.
            var grip = FeaturePrefab.Child(mount, "Grip");
            FeaturePrefab.Ensure<WirenookHandGrip>(grip).hand = _hand;
            var ray = FeaturePrefab.Child(grip, "Ray");
            _ray = ray.transform;

            var spot = FeaturePrefab.Child(aim, "Spot");
            var facing = FeaturePrefab.Child(spot, "Facing");
            var level = FeaturePrefab.Child(aim, "Level");

            var raycast = FeaturePrefab.Ensure<VRCRaycast>(ray);
            var serialized = new SerializedObject(raycast);
            // Version 1.1, as the laser pointer uses.
            serialized.FindProperty("version").enumValueIndex = 1;
            serialized.FindProperty("raycastDirection").vector3Value = Vector3.forward;
            serialized.FindProperty("distance").floatValue = reach;
            serialized.FindProperty("collisionMode").enumValueIndex = 0; // the world only: nothing is set down on a person
            serialized.FindProperty("resultTransform").objectReferenceValue = spot.transform;
            // The prop stands upright whatever the surface; Facing gives it its heading.
            serialized.FindProperty("applyRotation").boolValue = false;
            serialized.FindProperty("behaviorOnMiss").enumValueIndex = 2; // out at the ray's end, where nothing reads it
            serialized.FindProperty("parameter").stringValue = RayParameter;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            // Cast only while aiming (AddLayers).
            raycast.enabled = false;

            // The wearer's own place, lifted or lowered to the spot's height: aiming at it turns
            // Facing about the vertical only, however far above or below the spot is.
            var lift = FeaturePrefab.Ensure<VRCPositionConstraint>(level);
            lift.ClearSources();
            lift.AddSource(new VRCConstraintSource { SourceTransform = spot.transform, Weight = 1f });
            lift.PositionOffset = Vector3.zero;
            lift.AffectsPositionX = false;
            lift.AffectsPositionY = true;
            lift.AffectsPositionZ = false;
            Settle(lift);

            var turn = FeaturePrefab.Ensure<VRCAimConstraint>(facing);
            turn.ClearSources();
            turn.AddSource(new VRCConstraintSource { SourceTransform = level.transform, Weight = 1f });
            turn.AimAxis = Vector3.forward;
            turn.UpAxis = Vector3.up;
            turn.WorldUp = VRCConstraintBase.WorldUpType.SceneUp;
            Settle(turn);

            var carry = FeaturePrefab.Child(aim, "Carry");
            _carry = carry.transform;
            var follow = FeaturePrefab.Ensure<VRCParentConstraint>(carry);
            follow.ClearSources();
            follow.AddSource(new VRCConstraintSource { SourceTransform = facing.transform, Weight = 0f });
            follow.AddSource(new VRCConstraintSource { SourceTransform = fallback, Weight = 1f });
            Settle(follow);

            var claim = FeaturePrefab.Ensure<WirenookHandClaim>(aim);
            claim.hand = _hand == HumanBodyBones.LeftHand ? WirenookHand.Left : WirenookHand.Right;
            claim.parameter = Aiming;
        }

        private static void Settle(VRCConstraintBase constraint)
        {
            constraint.IsActive = true;
            // Unlocked constraints re-measure their offset instead of moving the object.
            constraint.Locked = true;
            constraint.FreezeToWorld = false;
            constraint.RebakeOffsetsWhenUnfrozen = false;
        }

        /// <summary>
        /// Adds the rig's three layers. Give <see cref="Visible"/> to the
        /// prop's pop toggle and <see cref="Dropped"/> to the anchor in place
        /// of the switch itself.
        /// </summary>
        /// <param name="show">The synced bool the menu switches: the prop is out, aimed or set down.</param>
        public void AddLayers(ControllerBuilder fx, string show)
        {
            fx.Bool(show).Bool(Dropped).Bool(Visible).Bool(Aiming).Bool(Hit).Bool("IsLocal").Int(Gesture);
            var rayPath = FeaturePrefab.PathTo(_root, _ray);
            var carryPath = FeaturePrefab.PathTo(_root, _carry);

            // What phase the prop is in, worked out by every client from the two synced bits.
            AnimationClip Cast(bool casting) => fx.Clip(_prefix + (casting ? " aim cast" : " aim rest"), clip =>
                clip.Constant(rayPath, typeof(VRCRaycast), "m_Enabled", casting ? 1f : 0f));

            void Say(StateBuilder state, bool aiming, bool visible) => state.Behaviour<VRCAvatarParameterDriver>(driver =>
            {
                driver.localOnly = false;
                driver.parameters = new List<VRC_AvatarParameterDriver.Parameter>
                {
                    new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = Aiming, value = aiming ? 1f : 0f },
                    new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = Visible, value = visible ? 1f : 0f },
                };
            });

            var phase = fx.Layer(_prefix + " Aim");
            var away = phase.State("Away", Cast(false));
            var mine = phase.State("Aiming", Cast(true));
            var theirs = phase.State("Aiming (others)", Cast(true));
            var down = phase.State("Set Down", Cast(false));
            Say(away, false, false);
            Say(mine, true, true);
            // Other people see nothing of the aiming, but their copy of the hand is busy too.
            Say(theirs, true, false);
            Say(down, false, true);
            phase.Any(away).IfFalse(show);
            phase.Any(mine).IfTrue(show).IfFalse(Dropped).IfTrue("IsLocal");
            phase.Any(theirs).IfTrue(show).IfFalse(Dropped).IfFalse("IsLocal");
            phase.Any(down).IfTrue(show).IfTrue(Dropped);

            // Where the prop is carried: on the spot the hand points at, or a step in front when it points at nothing.
            AnimationClip Carried(bool aimed) => fx.Clip(_prefix + (aimed ? " carry aimed" : " carry in front"), clip => clip
                .Constant(carryPath, typeof(VRCParentConstraint), "Sources.source0.Weight", aimed ? 1f : 0f)
                .Constant(carryPath, typeof(VRCParentConstraint), "Sources.source1.Weight", aimed ? 0f : 1f));

            var source = fx.Layer(_prefix + " Aim Spot");
            var inFront = source.State("In Front", Carried(false));
            var aimedAt = source.State("Aimed", Carried(true));
            inFront.To(aimedAt).IfTrue(Hit);
            aimedAt.To(inFront).IfFalse(Hit);

            // The click, on the wearer's client. A hand that is already a fist when the prop comes
            // out has to open first, or switching it on would set it down in the same breath.
            var click = fx.Layer(_prefix + " Aim Click");
            var idle = click.State("Idle");
            var wait = click.State("Wait");
            var ready = click.State("Ready");
            var set = click.State("Set");
            var clear = click.State("Clear");
            set.Behaviour<VRCAvatarParameterDriver>(driver => SetLocally(driver, Dropped, 1f));
            clear.Behaviour<VRCAvatarParameterDriver>(driver => SetLocally(driver, Dropped, 0f));
            idle.To(wait).IfTrue("IsLocal").IfTrue(show).IfFalse(Dropped);
            wait.To(ready).NotEqual(Gesture, 1);
            ready.To(set).Equal(Gesture, 1);
            // Switched off, set down or not: the bit goes back, and the next time starts with aiming.
            click.Any(clear).IfTrue("IsLocal").IfFalse(show).IfTrue(Dropped);
            clear.To(idle).IfFalse(Dropped);
            foreach (var state in new[] { wait, ready }) state.To(idle).IfFalse(show);
            set.To(idle).IfFalse(show).IfFalse(Dropped);
        }

        private static void SetLocally(VRCAvatarParameterDriver driver, string parameter, float value)
        {
            driver.localOnly = true;
            driver.parameters = new List<VRC_AvatarParameterDriver.Parameter>
            {
                new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = parameter, value = value },
            };
        }

        /// <summary>
        /// The rig's expression parameters, for the feature's Modular Avatar
        /// declaration: one synced bit, and the rest each client's own.
        /// </summary>
        public IEnumerable<ParameterConfig> Parameters()
        {
            yield return FeaturePrefab.SyncedBool(Dropped);
            foreach (var name in new[] { Visible, Aiming, Hit, RayParameter + VRCRaycast.PARAM_RATIO, RayParameter + VRCRaycast.PARAM_DISTANCE })
                yield return new ParameterConfig { nameOrPrefix = name, syncType = ParameterSyncType.NotSynced, localOnly = true };
        }
    }
}
