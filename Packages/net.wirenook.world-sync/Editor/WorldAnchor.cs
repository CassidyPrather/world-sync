using System;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using Wirenook.Assets.Editor;

namespace Wirenook.WorldSync.Editor
{
    /// <summary>
    /// Leaves part of an avatar behind in the world.
    ///
    /// An anchor is an object that follows a carry point on the avatar until
    /// it is dropped, then stays where it was. It is built on VRChat's own
    /// Freeze To World constraint option, switched by one synced parameter.
    ///
    /// What that does and does not guarantee: every viewer freezes the anchor
    /// on their own client at the moment the parameter change reaches them,
    /// at the spot where they see the carry point just then. People present
    /// at the drop therefore agree to within network delay, which at walking
    /// speed is centimetres. Someone who joins or first loads the avatar
    /// afterwards freezes it where the wearer is standing at that moment
    /// instead. <see cref="SyncedAnchor"/> closes that gap by syncing the
    /// position itself; this is the cheap version, for props where one
    /// synced bit matters more than late joiners.
    /// </summary>
    public static class WorldAnchor
    {
        /// <summary>
        /// Makes <paramref name="anchor"/> follow <paramref name="carry"/>,
        /// ready to be frozen in place. Both must sit under the same feature
        /// root; the anchor's own position in the hierarchy stops mattering,
        /// because the constraint places it in world space.
        /// </summary>
        public static VRCParentConstraint Configure(GameObject anchor, Transform carry)
        {
            var constraint = FeaturePrefab.Ensure<VRCParentConstraint>(anchor);
            constraint.ClearSources();
            constraint.AddSource(new VRCConstraintSource
            {
                SourceTransform = carry,
                Weight = 1f,
                ParentPositionOffset = Vector3.zero,
                ParentRotationOffset = Vector3.zero,
            });
            constraint.IsActive = true;
            // Unlocked constraints re-measure their offset instead of moving
            // the object.
            constraint.Locked = true;
            constraint.FreezeToWorld = false;
            // Picking the anchor back up should return it to the carry point,
            // not keep whatever distance had opened up while it was dropped.
            constraint.RebakeOffsetsWhenUnfrozen = false;
            return constraint;
        }

        /// <summary>Adds the curve that holds the anchor in the world (or lets it follow) to a clip.</summary>
        /// <param name="anchorPath">Animation path of the anchor object.</param>
        public static void Freeze(ClipBuilder clip, string anchorPath, bool frozen)
        {
            clip.Constant(anchorPath, typeof(VRCParentConstraint), "FreezeToWorld", frozen ? 1f : 0f);
        }

        /// <summary>
        /// The usual pairing: a prop that pops into view at the carry point
        /// and is dropped there in the same instant, then picked up again when
        /// it is switched off.
        /// </summary>
        /// <param name="propPath">Animation path of the bone that scales the prop in and out.</param>
        /// <param name="extras">Further curves per phase ("off", "appear", "on", "disappear").</param>
        public static PopToggle.States DropLayer(ControllerBuilder fx, string layerName, string parameter,
            string anchorPath, string propPath, Action<ClipBuilder, string> extras = null)
        {
            return PopToggle.Layer(fx, layerName, parameter, propPath, (clip, phase) =>
            {
                // Frozen from the first frame it is visible, so it never
                // trails after the avatar while it is still appearing.
                Freeze(clip, anchorPath, phase != "off");
                extras?.Invoke(clip, phase);
            });
        }
    }
}
