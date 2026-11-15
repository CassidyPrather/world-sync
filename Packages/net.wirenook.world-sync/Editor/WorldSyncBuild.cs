using System.Linq;
using UnityEditor;
using UnityEngine;
using Wirenook.Assets.Editor;

namespace Wirenook.WorldSync.Editor
{
    /// <summary>Generates the package's sample prefab: a pin that marks a spot in the world.</summary>
    public static class WorldSyncBuild
    {
        public const string Package = "Packages/net.wirenook.world-sync";
        public const string PinPrefab = Package + "/Prefabs/Wirenook Anchor Pin.prefab";

        /// <summary>Checks the pin's synced anchor as far as the editor can; see <see cref="SyncedAnchorCheck"/>.</summary>
        [MenuItem("Tools/Wirenook/World Sync/Verify")]
        public static string Verify()
        {
            var report = SyncedAnchorCheck.Run(PinPrefab, Package + "/Generated/anchor-pin-fx.controller", "WN/Pin");
            Debug.Log("[world-sync] " + report);
            return report;
        }

        [MenuItem("Tools/Wirenook/World Sync/Rebuild")]
        public static void Rebuild()
        {
            const string parameter = "WN/Pin";
            SyncedAnchor.Prepare();
            FeaturePrefab.Edit(PinPrefab, root =>
            {
                // The pin is planted on the ground a step in front of the wearer.
                var carry = FeaturePrefab.Child(root, "Carry");
                carry.transform.localPosition = new Vector3(0f, 0f, 0.6f);

                var anchor = FeaturePrefab.Child(root, "Anchor");
                var rig = SyncedAnchor.Configure(root, anchor, carry.transform, parameter);
                var model = RigidProp.Build(anchor, Package + "/Models/anchor-pin.fbx", Package + "/Generated");

                var fx = ControllerBuilder.Rebuild(Package + "/Generated/anchor-pin-fx.controller");
                rig.DropLayers(fx, "Anchor Pin", parameter, FeaturePrefab.PathTo(root, FeaturePrefab.Find(model, "AnchorPin")));
                fx.Save();

                FeaturePrefab.Merge(root, fx.Controller);
                FeaturePrefab.Parameters(root, rig.Parameters().Prepend(FeaturePrefab.SyncedBool(parameter)).ToArray());
                FeaturePrefab.InstallMenu(root);
                FeaturePrefab.MenuToggle(root, "Drop Pin", "world-anchor", parameter);
                FeaturePrefab.Describe(root, "anchor-pin", "World pin", "world-anchor", "world pin", 10);
            });
            AssetDatabase.SaveAssets();
            Debug.Log("[world-sync] Rebuilt prefabs and animators.");
        }
    }
}
