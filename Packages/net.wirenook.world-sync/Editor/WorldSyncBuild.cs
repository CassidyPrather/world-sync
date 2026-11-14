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

        [MenuItem("Tools/Wirenook/World Sync/Rebuild")]
        public static void Rebuild()
        {
            const string parameter = "WN/Pin";
            FeaturePrefab.Edit(PinPrefab, root =>
            {
                // The pin is planted on the ground a step in front of the wearer.
                var carry = FeaturePrefab.Child(root, "Carry");
                carry.transform.localPosition = new Vector3(0f, 0f, 0.6f);

                var anchor = FeaturePrefab.Child(root, "Anchor");
                WorldAnchor.Configure(anchor, carry.transform);
                var model = RigidProp.Build(anchor, Package + "/Models/anchor-pin.fbx", Package + "/Generated");

                var fx = ControllerBuilder.Rebuild(Package + "/Generated/anchor-pin-fx.controller");
                WorldAnchor.DropLayer(fx, "Anchor Pin", parameter,
                    FeaturePrefab.PathTo(root, anchor.transform),
                    FeaturePrefab.PathTo(root, FeaturePrefab.Find(model, "AnchorPin")));
                fx.Save();

                FeaturePrefab.Merge(root, fx.Controller);
                FeaturePrefab.Parameters(root, FeaturePrefab.SyncedBool(parameter));
                FeaturePrefab.InstallMenu(root);
                FeaturePrefab.MenuToggle(root, "Drop Pin", "world-anchor", parameter);
                FeaturePrefab.Describe(root, "anchor-pin", "World pin", "world-anchor", "world pin", 10);
            });
            AssetDatabase.SaveAssets();
            Debug.Log("[world-sync] Rebuilt prefabs and animators.");
        }
    }
}
