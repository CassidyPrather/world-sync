using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using VRC.SDKBase;
using Wirenook.WorldSync.Core;

namespace Wirenook.WorldSync.Editor
{
    /// <summary>
    /// Runs an anchor bus in the editor, as far as the editor can: two copies
    /// of the generated animator, one playing the wearer's client and one a
    /// viewer's, with the three things only VRChat provides stood in for.
    ///
    ///   Parameter drivers. The SDK's driver behaviours do nothing outside
    ///   the client, so each animator is watched for the states it enters
    ///   and their drivers' sets and copies are carried out here, on the
    ///   client they would run on.
    ///
    ///   Contacts. The wearer's two readings are worked out from where the
    ///   cursor is said to be, by the same arithmetic the rig's constraints
    ///   are weighted to perform.
    ///
    ///   The network. Every so often the synced parameters are copied from
    ///   wearer to viewer, floats rounded to the eight bits they travel in.
    ///   How often is varied, and so is whether the viewer's floats jump to
    ///   new values or ease towards them.
    ///
    /// What this proves is the generated state machines and their timing:
    /// that placing a slot stores the right numbers, that a viewer who hears
    /// the broadcast ends up with every slot in the right place, and that
    /// clearing reaches them too. It cannot prove that VRChat's contacts,
    /// constraints and drivers behave as stood in for here.
    /// </summary>
    public static class AnchorBusCheck
    {
        /// <summary>Seconds one frame of a <see cref="Client"/> lasts.</summary>
        public const float Step = 0.02f;
        private static readonly string[] Axes = { "X", "Y", "Z" };

        private static readonly Vector3[] Places =
        {
            new Vector3(3.2f, 1.5f, -7.8f),
            new Vector3(-120.4f, 36.7f, 88.9f),
            new Vector3(0.011f, -0.02f, 0.03f),
            new Vector3(400.25f, -80.5f, -333.3f),
            new Vector3(-511f, 250f, 511f),
            new Vector3(12.75f, 0f, 44.1f),
            new Vector3(-0.6f, 2.2f, 0.9f),
        };

        /// <returns>A report ending in "ok" or listing what failed.</returns>
        public static string Run(string prefabPath, string controllerPath, string prefix, int slots,
            string place, string clear, string broadcast, float range = 512f)
        {
            var grid = new WorldGrid(range);
            var problems = new List<string>();
            var text = new StringBuilder();

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (prefab == null || controller == null) return "FAILED\n  missing " + prefabPath + " or " + controllerPath;

            Wiring(prefab, grid, problems);

            // Three axes each off by up to the resolution, plus single
            // precision at half a kilometre.
            var allowed = grid.Resolution * Mathf.Sqrt(3f) * 1.05f + range * 4e-6f;
            var turn = slots * (AnchorBus.LoadSeconds + AnchorBus.AirSeconds + AnchorBus.GapSeconds);
            text.AppendLine(slots + " slots, range ±" + range + " m, resolution " + (grid.Resolution * 100f).ToString("0.0") +
                            " cm per axis, one turn of the broadcast " + turn.ToString("0.0") + " s");

            // VRChat does not say whether copying a float into an integer
            // parameter rounds or truncates; the scheme has to work for both.
            foreach (var truncate in new[] { false, true })
            {
                using (var wearer = new Client(prefab, controller, prefix, true, truncate))
                {
                    var placed = new Vector3[slots];
                    var worstPlaced = 0f;
                    for (var slot = 1; slot <= slots; slot++)
                    {
                        placed[slot - 1] = Places[(slot - 1) % Places.Length];
                        wearer.Cursor = placed[slot - 1];
                        // A tap on a menu button.
                        wearer.Animator.SetInteger(place, slot);
                        wearer.Run(0.2f, grid);
                        wearer.Animator.SetInteger(place, 0);
                        wearer.Run(2f * AnchorBus.SettleSeconds + 0.6f, grid);
                        if (!wearer.Animator.GetBool(wearer.Bus + "/" + slot + "/Set"))
                            problems.Add("Slot " + slot + " was not marked as placed on the wearer's client.");
                        worstPlaced = Mathf.Max(worstPlaced, (wearer.Pose(slot).position - placed[slot - 1]).magnitude);
                    }

                    if (worstPlaced > range * 4e-6f + 1e-3f)
                        problems.Add("The wearer's own copy of a slot is off by " + worstPlaced + " m.");
                    text.AppendLine((truncate ? "truncating" : "rounding") + ": placed " + slots + " slots, the wearer's copies within " +
                                    (worstPlaced * 100f).ToString("0.00") + " cm");

                    // A bus that carries values: each slot is given one of its
                    // own, which a viewer has to end up with too. Eight bits
                    // of a synced float is steps of 1/127.
                    var valued = controller.parameters.Any(parameter => parameter.name == prefix + "/TxValue");
                    float Wanted(int slot) => 0.1f + 0.8f * slot / slots;
                    bool Valued(Client client, int slot, float wanted) =>
                        Mathf.Abs(client.Animator.GetFloat(prefix + "/" + slot + "/Value") - wanted) <= 1f / 127f;
                    if (valued)
                        for (var slot = 1; slot <= slots; slot++) wearer.Animator.SetFloat(prefix + "/" + slot + "/Value", Wanted(slot));

                    wearer.Animator.SetBool(broadcast, true);
                    Client last = null;
                    foreach (var (interval, eased, turns, required) in new[]
                             {
                                 (0.1f, false, 2f, true), (0.1f, true, 2f, true), (0.45f, false, 3f, true), (1f, false, 12f, false),
                             })
                    {
                        last?.Dispose();
                        // Someone who arrives after everything was placed.
                        var viewer = new Client(prefab, controller, prefix, false, truncate);
                        last = viewer;
                        var complete = -1f;
                        var sinceSync = interval;
                        var elapsed = 0f;
                        while (elapsed < turns * turn + 1f)
                        {
                            wearer.Advance(grid);
                            sinceSync += Step;
                            if (sinceSync >= interval)
                            {
                                sinceSync = 0f;
                                viewer.Receive(wearer);
                            }

                            viewer.Ease(eased);
                            viewer.Advance(grid);
                            elapsed += Step;
                            if (complete < 0f && Enumerable.Range(1, slots).All(slot =>
                                    viewer.Animator.GetBool(viewer.Bus + "/" + slot + "/Set") &&
                                    (viewer.Pose(slot).position - placed[slot - 1]).magnitude <= allowed &&
                                    (!valued || Valued(viewer, slot, Wanted(slot)))))
                                complete = elapsed;
                        }

                        var worst = Enumerable.Range(1, slots).Max(slot => (viewer.Pose(slot).position - placed[slot - 1]).magnitude);
                        var label = "  an update every " + interval + " s" + (eased ? ", floats eased" : "") + ": ";
                        if (complete >= 0f && worst <= allowed)
                        {
                            text.AppendLine(label + "whole layout after " + complete.ToString("0.0") + " s, worst error " +
                                            (worst * 100f).ToString("0.0") + " cm (allowed " + (allowed * 100f).ToString("0.0") + ")");
                        }
                        else
                        {
                            var astray = valued ? Enumerable.Range(1, slots).Count(slot => !Valued(viewer, slot, Wanted(slot))) : 0;
                            text.AppendLine(label + "NOT the whole layout after " + elapsed.ToString("0.0") + " s (worst error " + worst.ToString("0.000") + " m" +
                                            (valued ? ", " + astray + " values astray" : "") + ")");
                            if (required) problems.Add("A viewer updated every " + interval + " s did not get the layout.");
                        }
                    }

                    if (valued)
                    {
                        // The wearer turns a dial: the slot is read out
                        // ahead of its turn, however long the round is, and
                        // only once.
                        var turned = slots;
                        wearer.Animator.SetFloat(prefix + "/" + turned + "/Value", 0.05f);
                        var heard = -1f;
                        var aired = 0;
                        var wasOnAir = false;
                        var sinceUpdate = 0f;
                        for (var elapsed = 0f; elapsed < 6f; elapsed += Step)
                        {
                            wearer.Advance(grid);
                            sinceUpdate += Step;
                            if (sinceUpdate >= 0.1f)
                            {
                                sinceUpdate = 0f;
                                last.Receive(wearer);
                            }

                            last.Ease(false);
                            last.Advance(grid);
                            var onAir = Mathf.RoundToInt(wearer.Animator.GetFloat(prefix + "/Slot")) == turned;
                            if (onAir && !wasOnAir && elapsed < 3f) aired++;
                            wasOnAir = onAir;
                            if (heard < 0f && Valued(last, turned, 0.05f)) heard = elapsed;
                        }

                        if (heard < 0f || heard > 3f)
                            problems.Add("A value the wearer changed had not reached a viewer after " + (heard < 0f ? "6" : heard.ToString("0.0")) + " s.");
                        if (slots > 3 && aired > 1)
                            problems.Add("A value changed once was read out " + aired + " times in three seconds: the request was not let go of.");
                        text.AppendLine("  a value changed by the wearer: with the viewer after " + heard.ToString("0.0") + " s");
                    }

                    // Clearing has to reach a viewer who already has the layout.
                    wearer.Animator.SetBool(clear, true);
                    wearer.Run(0.3f, grid);
                    wearer.Animator.SetBool(clear, false);
                    var since = 0f;
                    for (var elapsed = 0f; elapsed < 1.5f * turn; elapsed += Step)
                    {
                        wearer.Advance(grid);
                        since += Step;
                        if (since >= 0.1f)
                        {
                            since = 0f;
                            last.Receive(wearer);
                        }

                        last.Ease(false);
                        last.Advance(grid);
                    }

                    var left = Enumerable.Range(1, slots).Count(slot => last.Animator.GetBool(last.Bus + "/" + slot + "/Set"));
                    if (left > 0) problems.Add("After a clear, a viewer still has " + left + " slots placed.");
                    text.AppendLine("  cleared: the viewer has " + left + " slots left");
                    last.Dispose();
                }
            }

            return text + (problems.Count == 0 ? "ok" : "FAILED\n  " + string.Join("\n  ", problems.Distinct()));
        }

        /// <summary>
        /// One client's copy of the avatar, with the client's work done for
        /// it. Public so that a feature built on a bus can play its own
        /// layers the same way: a wearer and a viewer, drivers carried out,
        /// the channel copied across.
        /// </summary>
        public sealed class Client : IDisposable
        {
            public readonly Animator Animator;
            public readonly string Bus;

            /// <summary>Where the bus's cursor is said to be: the readings the wearer's contacts would give are worked out from it.</summary>
            public Vector3 Cursor;

            /// <summary>
            /// Other synced parameters of the feature's, by name: each
            /// arrives whole, with the channel, on every
            /// <see cref="Receive"/>.
            /// </summary>
            public readonly List<string> AlsoSynced = new List<string>();

            /// <summary>What a driver that draws a number at random draws from. Seeded, so a check runs the same way twice.</summary>
            public System.Random Chance = new System.Random(20261126);

            /// <summary>This client's copy of the prefab.</summary>
            public GameObject Instance => _instance;

            private readonly GameObject _instance;
            private readonly bool _local;
            private readonly bool _truncate;
            private readonly Dictionary<string, AnimatorControllerParameterType> _types;
            private readonly List<Dictionary<int, AnimatorState>> _states = new List<Dictionary<int, AnimatorState>>();
            private readonly int[] _current;
            private readonly Dictionary<string, float> _arriving = new Dictionary<string, float>();

            public Client(GameObject prefab, AnimatorController controller, string bus, bool local, bool truncate)
            {
                Bus = bus;
                _local = local;
                _truncate = truncate;
                _instance = UnityEngine.Object.Instantiate(prefab);
                Animator = _instance.AddComponent<Animator>();
                Animator.runtimeAnimatorController = controller;
                Animator.Rebind();
                Animator.SetBool("IsLocal", local);
                _types = controller.parameters.ToDictionary(parameter => parameter.name, parameter => parameter.type);
                foreach (var layer in controller.layers)
                {
                    _states.Add(layer.stateMachine.states.ToDictionary(
                        state => Animator.StringToHash(layer.name + "." + state.state.name), state => state.state));
                }

                _current = new int[controller.layers.Length];
                Animator.Update(0f);
            }

            public Transform Pose(int slot)
            {
                return _instance.GetComponentsInChildren<Transform>(true).First(transform => transform.name == "Slot " + slot);
            }

            public void Run(float seconds, WorldGrid grid)
            {
                for (var elapsed = 0f; elapsed < seconds; elapsed += Step) Advance(grid);
            }

            /// <summary>One frame: the contacts' readings, the animator, and the drivers of any state just entered.</summary>
            public void Advance(WorldGrid grid)
            {
                if (_local)
                {
                    for (var axis = 0; axis < 3; axis++)
                    {
                        // The fine reading is taken against the cell the
                        // animator has moved into place, as it is in the rig.
                        var cell = Mathf.RoundToInt(Animator.GetFloat(Bus + "/Cursor" + Axes[axis]));
                        Animator.SetFloat(Bus + "/Coarse_SenderLocalPosition" + Axes[axis], grid.CoarseReading(Cursor[axis]));
                        Animator.SetFloat(Bus + "/Fine_SenderLocalPosition" + Axes[axis], grid.Fine(Cursor[axis], cell));
                    }
                }

                Animator.Update(Step);
                for (var layer = 0; layer < _current.Length; layer++)
                {
                    var now = Animator.GetCurrentAnimatorStateInfo(layer).fullPathHash;
                    if (now == _current[layer]) continue;
                    _current[layer] = now;
                    if (!_states[layer].TryGetValue(now, out var state)) continue;
                    foreach (var driver in state.behaviours.OfType<VRCAvatarParameterDriver>())
                    {
                        if (driver.localOnly && !_local) continue;
                        foreach (var change in driver.parameters) Apply(change);
                    }
                }
            }

            private void Apply(VRC_AvatarParameterDriver.Parameter change)
            {
                switch (change.type)
                {
                    case VRC_AvatarParameterDriver.ChangeType.Set:
                        Write(change.name, change.value);
                        break;
                    case VRC_AvatarParameterDriver.ChangeType.Copy:
                        var value = Read(change.source);
                        if (change.convertRange)
                        {
                            value = change.destMin + (value - change.sourceMin) / (change.sourceMax - change.sourceMin) *
                                (change.destMax - change.destMin);
                        }

                        Write(change.name, value);
                        break;
                    case VRC_AvatarParameterDriver.ChangeType.Random:
                        // Whole numbers for an integer, both ends included, as VRChat draws them.
                        Write(change.name, _types[change.name] == AnimatorControllerParameterType.Int
                            ? Chance.Next(Mathf.RoundToInt(change.valueMin), Mathf.RoundToInt(change.valueMax) + 1)
                            : change.valueMin + (float)Chance.NextDouble() * (change.valueMax - change.valueMin));
                        break;
                    default:
                        throw new NotSupportedException("The check does not stand in for a driver's " + change.type + ".");
                }
            }

            private float Read(string name)
            {
                switch (_types[name])
                {
                    case AnimatorControllerParameterType.Bool: return Animator.GetBool(name) ? 1f : 0f;
                    case AnimatorControllerParameterType.Int: return Animator.GetInteger(name);
                    default: return Animator.GetFloat(name);
                }
            }

            private void Write(string name, float value)
            {
                switch (_types[name])
                {
                    case AnimatorControllerParameterType.Bool:
                        Animator.SetBool(name, value != 0f);
                        break;
                    case AnimatorControllerParameterType.Int:
                        Animator.SetInteger(name, _truncate ? Mathf.FloorToInt(value) : Mathf.RoundToInt(value));
                        break;
                    default:
                        Animator.SetFloat(name, value);
                        break;
                }
            }

            /// <summary>An update arrives from the wearer: the channel, as it survives the network.</summary>
            public void Receive(Client wearer)
            {
                _arriving[Bus + "/Slot"] = Mathf.Round(wearer.Animator.GetFloat(Bus + "/Slot"));
                Animator.SetBool(Bus + "/TxSet", wearer.Animator.GetBool(Bus + "/TxSet"));
                foreach (var axis in Axes)
                {
                    _arriving[Bus + "/Tx" + axis] = Mathf.Round(wearer.Animator.GetFloat(Bus + "/Tx" + axis));
                    _arriving[Bus + "/TxFine" + axis] = WorldGrid.Synced(wearer.Animator.GetFloat(Bus + "/TxFine" + axis));
                }

                // A bus that carries values has eight more bits on the channel.
                if (_types.ContainsKey(Bus + "/TxValue"))
                    _arriving[Bus + "/TxValue"] = WorldGrid.Synced(wearer.Animator.GetFloat(Bus + "/TxValue"));

                foreach (var name in AlsoSynced) Write(name, wearer.Read(name));
            }

            /// <summary>Moves this client's synced parameters to what last arrived: at once, or a tenth of a second behind.</summary>
            public void Ease(bool eased)
            {
                foreach (var arriving in _arriving)
                {
                    // Whole numbers are never eased; only the fine floats may be.
                    var smooth = eased && (arriving.Key.Contains("/TxFine") || arriving.Key.EndsWith("/TxValue", StringComparison.Ordinal));
                    var held = Animator.GetFloat(arriving.Key);
                    Animator.SetFloat(arriving.Key, smooth ? held + (arriving.Value - held) * Mathf.Min(1f, Step / 0.1f) : arriving.Value);
                }
            }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(_instance);
            }
        }

        private static void Wiring(GameObject prefab, WorldGrid grid, List<string> problems)
        {
            var origin = AssetDatabase.LoadAssetAtPath<GameObject>(SyncedAnchor.OriginPrefab);
            var world = Find(prefab, "World");
            var frame = world.GetComponents<VRCConstraintBase>();
            if (frame.Length != 2 || frame.Any(constraint => constraint.Sources.Count != 1 || constraint.Sources[0].SourceTransform != origin.transform))
                problems.Add("World needs a parent constraint and a scale constraint, both on the world-origin prefab.");

            Weight(prefab, "Coarse Proxy", grid.CoarseOriginWeight, problems);
            Weight(prefab, "Fine Proxy", grid.FineOriginWeight, problems);

            var busContacts = world.GetComponentsInChildren<ContactBase>(true);
            foreach (var contact in busContacts)
            {
                if (!contact.localOnly) problems.Add(contact.name + " is not local-only; it would count for rank and run on every client.");
                if (contact.radius > 3f) problems.Add(contact.name + " is larger than VRChat allows a contact to be.");
            }

            if (busContacts.OfType<VRCContactReceiver>().Count() != 2 || busContacts.OfType<VRCContactSender>().Count() != 2)
                problems.Add("The bus needs exactly two receivers and two senders under World.");

            // The farthest a reading can be from its receiver has to be within
            // the two spheres' combined radii, or the contact never fires.
            Reach(prefab, "Coarse Receiver", "Coarse Proxy", WorldGrid.CoarseReach * Mathf.Sqrt(3f), problems);
            Reach(prefab, "Fine Receiver", "Fine Proxy", WorldGrid.FineReach * Mathf.Sqrt(3f), problems);
        }

        private static void Weight(GameObject prefab, string name, float expected, List<string> problems)
        {
            var constraint = Find(prefab, name).GetComponent<VRCPositionConstraint>();
            if (constraint == null || constraint.Sources.Count != 2)
            {
                problems.Add(name + " needs a position constraint with two sources.");
                return;
            }

            if (Mathf.Abs(constraint.Sources[0].Weight - expected) > 1e-3f || Mathf.Abs(constraint.Sources[1].Weight - 1f) > 1e-6f)
                problems.Add(name + " blends with weights " + constraint.Sources[0].Weight + " and " + constraint.Sources[1].Weight +
                             "; expected " + expected + " and 1.");
        }

        private static void Reach(GameObject prefab, string receiver, string sender, float farthest, List<string> problems)
        {
            var reach = Find(prefab, receiver).GetComponent<VRCContactReceiver>().radius +
                        Find(prefab, sender).GetComponent<VRCContactSender>().radius;
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
