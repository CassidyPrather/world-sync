using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using Wirenook.WorldSync.Core;

namespace Wirenook.WorldSync.Editor
{
    /// <summary>
    /// Checks the half of a synced anchor that the editor can run: the
    /// receiving side. It feeds the generated animator the values a wearer
    /// would sync for a set of world positions and headings, and measures
    /// where the animator puts the anchor's target.
    ///
    /// The sending side, contacts reading positions off constraints, only
    /// runs in the VRChat client. For that side this checks the rig is wired
    /// as <see cref="WorldGrid"/> expects (blend weights, radii, local-only
    /// contacts); the arithmetic itself is covered by WorldGrid's tests.
    /// </summary>
    public static class SyncedAnchorCheck
    {
        private static readonly (Vector3 position, float heading)[] Samples =
        {
            (Vector3.zero, 0f),
            (new Vector3(3.2f, 1.5f, -7.8f), 90f),
            (new Vector3(-120.4f, 36.7f, 88.9f), -45f),
            (new Vector3(400.25f, -80.5f, -333.3f), 170f),
            (new Vector3(-511f, 250f, 511f), -179f),
            (new Vector3(0.011f, -0.02f, 0.03f), 12.5f),
        };

        /// <returns>A report ending in "ok" or listing what failed.</returns>
        public static string Run(string prefabPath, string controllerPath, string prefix, float range = 512f)
        {
            var grid = new WorldGrid(range);
            var problems = new List<string>();
            var text = new StringBuilder();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controllerPath);
            if (prefab == null || controller == null) return "FAILED\n  missing " + prefabPath + " or " + controllerPath;

            var instance = UnityEngine.Object.Instantiate(prefab);
            try
            {
                Wiring(instance, grid, problems);

                var animator = instance.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.Rebind();
                animator.Update(0f);

                var fine = Find(instance, "Fine");
                var headingTarget = Find(instance, "Heading Target");
                var worst = 0f;
                var worstHeading = 0f;
                foreach (var (position, heading) in Samples)
                {
                    for (var axis = 0; axis < 3; axis++)
                    {
                        var name = "XYZ"[axis].ToString();
                        var index = grid.IndexFromReading(grid.CoarseReading(position[axis]), false);
                        animator.SetFloat(prefix + "/" + name, index);
                        animator.SetFloat(prefix + "/Fine_SenderLocalPosition" + name, WorldGrid.Synced(grid.Fine(position[axis], index)));
                    }

                    var radians = heading * Mathf.Deg2Rad;
                    animator.SetFloat(prefix + "/Heading_SenderLocalPositionX", WorldGrid.Synced(Mathf.Sin(radians)));
                    animator.SetFloat(prefix + "/Heading_SenderLocalPositionZ", WorldGrid.Synced(Mathf.Cos(radians)));
                    animator.Update(0.05f);
                    animator.Update(0.05f);

                    // The instance sits at the scene origin, where the World
                    // frame would hold itself in game.
                    var error = (fine.position - position).magnitude;
                    var read = Mathf.Atan2(headingTarget.localPosition.x, headingTarget.localPosition.z) * Mathf.Rad2Deg;
                    var headingError = Mathf.Abs(Mathf.DeltaAngle(read, heading));
                    worst = Mathf.Max(worst, error);
                    worstHeading = Mathf.Max(worstHeading, headingError);
                }

                // Three axes each off by up to the resolution, plus single
                // precision at half a kilometre.
                var allowed = grid.Resolution * Mathf.Sqrt(3f) * 1.05f + range * 4e-6f;
                text.AppendLine("range ±" + range + " m, cell " + grid.Cell.ToString("0.000") + " m, resolution " +
                                (grid.Resolution * 100f).ToString("0.0") + " cm per axis");
                text.AppendLine("decoded " + Samples.Length + " positions: worst error " + (worst * 100f).ToString("0.0") +
                                " cm (allowed " + (allowed * 100f).ToString("0.0") + "), worst heading error " + worstHeading.ToString("0.00") + "°");
                if (worst > allowed) problems.Add("The decoded position is off by " + worst + " m.");
                if (worstHeading > 1f) problems.Add("The decoded heading is off by " + worstHeading + "°.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }

            return text + (problems.Count == 0 ? "ok" : "FAILED\n  " + string.Join("\n  ", problems));
        }

        private static void Wiring(GameObject instance, WorldGrid grid, List<string> problems)
        {
            var origin = AssetDatabase.LoadAssetAtPath<GameObject>(SyncedAnchor.OriginPrefab);
            var world = Find(instance, "World");
            foreach (var constraint in world.GetComponents<VRCConstraintBase>())
            {
                if (constraint.Sources.Count != 1 || constraint.Sources[0].SourceTransform != origin.transform)
                    problems.Add("World's " + constraint.GetType().Name + " does not point at the world-origin prefab.");
            }

            if (world.GetComponents<VRCConstraintBase>().Length != 2)
                problems.Add("World needs a parent constraint and a scale constraint.");

            Weight(instance, "Coarse Proxy", grid.CoarseOriginWeight, problems);
            Weight(instance, "Fine Proxy", grid.FineOriginWeight, problems);

            foreach (var contact in instance.GetComponentsInChildren<ContactBase>(true))
            {
                if (!contact.localOnly) problems.Add(contact.name + " is not local-only; it would count for rank and run on every client.");
                if (contact.radius > 3f) problems.Add(contact.name + " is larger than VRChat allows a contact to be.");
            }

            var receivers = instance.GetComponentsInChildren<VRCContactReceiver>(true);
            var senders = instance.GetComponentsInChildren<VRCContactSender>(true);
            if (receivers.Length != 3 || senders.Length != 3)
                problems.Add("Expected three receivers and three senders, found " + receivers.Length + " and " + senders.Length + ".");
            foreach (var receiver in receivers)
            {
                if (senders.Count(sender => sender.collisionTags.Intersect(receiver.collisionTags).Any()) != 1)
                    problems.Add(receiver.name + " must pair with exactly one sender.");
            }

            // The farthest a reading can be from its receiver has to be within
            // the two spheres' combined radii, or the contact never fires.
            Reach(instance, "Coarse Receiver", "Coarse Proxy", WorldGrid.CoarseReach * Mathf.Sqrt(3f), problems);
            Reach(instance, "Fine Receiver", "Fine Proxy", WorldGrid.FineReach * Mathf.Sqrt(3f), problems);
        }

        private static void Weight(GameObject instance, string name, float expected, List<string> problems)
        {
            var constraint = Find(instance, name).GetComponent<VRCPositionConstraint>();
            if (constraint == null || constraint.Sources.Count != 2)
            {
                problems.Add(name + " needs a position constraint with two sources.");
                return;
            }

            if (Mathf.Abs(constraint.Sources[0].Weight - expected) > 1e-3f || Mathf.Abs(constraint.Sources[1].Weight - 1f) > 1e-6f)
                problems.Add(name + " blends with weights " + constraint.Sources[0].Weight + " and " + constraint.Sources[1].Weight +
                             "; expected " + expected + " and 1.");
        }

        private static void Reach(GameObject instance, string receiver, string sender, float farthest, List<string> problems)
        {
            var reach = Find(instance, receiver).GetComponent<VRCContactReceiver>().radius +
                        Find(instance, sender).GetComponent<VRCContactSender>().radius;
            if (reach <= farthest)
                problems.Add(sender + " can be " + farthest + " m from " + receiver + ", beyond their reach of " + reach + " m.");
        }

        private static Transform Find(GameObject root, string name)
        {
            var found = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(transform => transform.name == name);
            if (found == null) throw new InvalidOperationException("The prefab has no object named \"" + name + "\".");
            return found;
        }
    }
}
