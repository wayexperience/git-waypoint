using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

// Unity 6000.5 replaced the int instance-id based hierarchy hooks with EntityId-based ones and made the
// old int overloads hard errors. Alias the item id so the same code compiles on both new and older Unity.
#if UNITY_6000_5_OR_NEWER
using ItemId = UnityEngine.EntityId;
#else
using ItemId = System.Int32;
#endif

namespace Unity.VersionControl.Git.UI
{
    public static class HierarchyWindowInterface
    {
        // Cache only the (stable for the item's lifetime) instanceID -> guid resolution; the badge itself is
        // resolved LIVE each draw, so lock/status/identity/outdated changes show immediately instead of going
        // stale until the next domain reload.
        private static Dictionary<ItemId, string> guidCache;
        private static float rightEdge;

        public static void Initialize()
        {
            guidCache = new Dictionary<ItemId, string>();
#if UNITY_6000_5_OR_NEWER
            EditorApplication.hierarchyWindowItemByEntityIdOnGUI += OnHierarchyItemTryToDrawStatusIcon;
#else
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItemTryToDrawStatusIcon;
#endif
        }

        private static void OnHierarchyItemTryToDrawStatusIcon(ItemId instanceID, Rect selectionRect)
        {
            if (!ApplicationConfiguration.HierarchyIconsEnabled)
                return;

            if (!guidCache.TryGetValue(instanceID, out string guid))
            {
                guid = string.Empty;
#if UNITY_6000_5_OR_NEWER
                GameObject hierarchyGO = EditorUtility.EntityIdToObject(instanceID) as GameObject;
#else
                GameObject hierarchyGO = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
#endif
                if (!hierarchyGO)
                {
                    // if no Object has been returned by the lookup, then it is possible that it is a Scene
                    string scenePath = "";
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        Scene scene = SceneManager.GetSceneAt(i);
                        // int->ItemId is flagged for future removal on new Unity, but the scene handle
                        // is only available as an int; harmless no-op cast on older Unity.
#pragma warning disable 618
                        if ((ItemId)scene.GetHashCode() == instanceID)
#pragma warning restore 618
                        {
                            scenePath = scene.path;
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(scenePath))
                    {
                        guid = AssetDatabase.AssetPathToGUID(scenePath);
                        rightEdge = selectionRect.x;
                    }
                }
                else
                {
                    if (PrefabUtility.GetNearestPrefabInstanceRoot(hierarchyGO) == hierarchyGO)
                    {
                        GameObject prefab = PrefabUtility.GetCorrespondingObjectFromOriginalSource(hierarchyGO);
                        if (prefab)
                        {
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(prefab, out guid, out long _);
                        }
                    }
                }

                guidCache.Add(instanceID, guid ?? string.Empty);
            }

            if (string.IsNullOrEmpty(guid))
                return;

            // Resolve the badge live (cheap, same as the project window) so it never goes stale.
            ProjectWindowInterface.StatusBadge badge = ProjectWindowInterface.GetBadgeForAssetGUID(guid);
            if (!badge.HasValue)
                return;

            // place the icon to the right of the list:
            Rect r = new Rect(selectionRect);
            r.width = 18;

            if (ApplicationConfiguration.HierarchyIconsAlignment == ApplicationConfiguration.HierarchyIconAlignment.Right)
            {
                r.x = ApplicationConfiguration.HierarchyIconsIndented ? selectionRect.width + 6 : selectionRect.xMax - 40;
                r.x -= ApplicationConfiguration.HierarchyIconsOffsetRight - 22f;
            }
            else
            {
                r.x = rightEdge - r.width - 22f;
                r.x += ApplicationConfiguration.HierarchyIconsOffsetLeft;
            }

            // Keep it a square badge, vertically centred and a touch smaller than the row, to match the
            // project window and avoid touching the next row.
            float size = Mathf.Min(r.width, r.height) - 3f;
            r = new Rect(r.x, r.y + (r.height - size) * 0.5f, size, size);

            ProjectWindowInterface.DrawBadge(r, badge);
        }
    }
}
