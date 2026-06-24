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
        private static Dictionary<ItemId, ProjectWindowInterface.StatusBadge> iconCache;
        private static float rightEdge;

        public static void Initialize()
        {
            iconCache = new Dictionary<ItemId, ProjectWindowInterface.StatusBadge>();
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

            if (!iconCache.TryGetValue(instanceID, out ProjectWindowInterface.StatusBadge badge))
            {
                string guid = null;
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

                    if (string.IsNullOrEmpty(scenePath))
                    {
                        iconCache.Add(instanceID, default);
                        return;
                    }

                    guid = AssetDatabase.AssetPathToGUID(scenePath);
                    rightEdge = selectionRect.x;
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

                if (guid != null)
                {
                    badge = ProjectWindowInterface.GetBadgeForAssetGUID(guid);
                }

                iconCache.Add(instanceID, badge);
            }

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

            ProjectWindowInterface.DrawBadge(r, badge);
        }
    }
}
