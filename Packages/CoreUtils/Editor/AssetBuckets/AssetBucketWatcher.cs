using System;
using System.Collections.Generic;
using System.Linq;
using CoreUtils.Editor;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreUtils.AssetBuckets {
    public static class AssetBucketWatcher {
        private static bool s_WillSaveAssets;
        private static BaseAssetBucket[] s_Buckets;
        private static string[] s_LastBuckets;

        private static IEnumerable<BaseAssetBucket> Buckets {
            get {
                string[] newBuckets = AssetDatabase.FindAssets($"t:{nameof(BaseAssetBucket)}");

                if (s_LastBuckets == null || !newBuckets.IsEqual(s_LastBuckets)) {
                    s_LastBuckets = newBuckets;
                    s_Buckets = newBuckets
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .Select(AssetDatabase.LoadAssetAtPath<BaseAssetBucket>)
                        .ToArray();
                }

                return s_Buckets;
            }
        }

        [InitializeOnLoadMethod]
        public static void Init() {
            AssetImportTracker.DelayedAssetsChanged -= OnDatabaseChanged;
            AssetImportTracker.DelayedAssetsChanged += OnDatabaseChanged;
        }

        private static void OnDatabaseChanged(AssetChanges changes) {
            if (CoreUtilsSettings.DisableAssetBucketScanning) {
                return;
            }

            HashSet<string> changedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            changes.Imported.ForEach(f => AddPath(f, changedDirectories));
            changes.Deleted.ForEach(f => AddPath(f, changedDirectories));
            changes.MovedFrom.ForEach(f => AddPath(f, changedDirectories));
            changes.MovedTo.ForEach(f => AddPath(f, changedDirectories));

            Buckets.ForEach(b => FindReferencesIfChanged(b, changes, changedDirectories));
        }

        private static void FindReferencesIfChanged(BaseAssetBucket bucket, AssetChanges changes, HashSet<string> changedDirectories) {
            // Skip if bucket is null.
            if (!bucket) {
                return;
            }

            // Skip if bucket is updated manually.
            if (bucket.ManualUpdate) {
                return;
            }

            // Skip if no source paths are in the changed directories.
            string[] sourcePaths = bucket.EDITOR_Sources.Where(o => o).Select(AssetDatabase.GetAssetPath).ToArray();

            if (changedDirectories != null && !changedDirectories.Any(bucket.EDITOR_IsValidDirectory)) {
                return;
            }

            // Skip if all imported files already exist in the bucket or can't be added to the bucket.
            if (changes.MovedFrom.Length == 0 && changes.MovedTo.Length == 0 && changes.Deleted.Length == 0 && changes.Imported.Length < 50) {
                if (!changes.Imported.Any(bucket.EDITOR_IsMissing)) {
                    return;
                }
            }

            FindReferences(bucket, sourcePaths, true);
        }

        public static bool FindReferences(BaseAssetBucket bucket, string[] sourcePaths = null, bool skipIfUnchanged = false) {
            sourcePaths = sourcePaths ?? bucket.EDITOR_Sources.Where(o => o).Select(AssetDatabase.GetAssetPath).ToArray();

            bucket.EDITOR_Clear();

            if (sourcePaths.Length == 0) {
                return false;
            }

            string filter = bucket.AssetSearchType.IsSubclassOf(typeof(Component)) ? "t:GameObject" : "t:" + bucket.AssetSearchType.Name;

            string[] newPaths = AssetDatabase
                .FindAssets(filter, sourcePaths)
                .OrderBy(guid => guid)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => CanBeType(p, bucket.AssetSearchType))
                .ToArray();

            List<Object> newObjects = newPaths
                .Select(p => AssetDatabase.LoadAssetAtPath(p, bucket.AssetSearchType))
                .Where(o => o && bucket.EDITOR_CanAdd(o))
                .ToList();

            // Skip if the new object list is the same as the existing bucket objects.
            if (skipIfUnchanged && BucketIsUnchanged(bucket, newObjects)) {
                return false;
            }

            bucket.EDITOR_ForceAdd(new HashSet<Object>(newObjects));
            bucket.EDITOR_Sort(AssetGuidSorter);
            EditorUtility.SetDirty(bucket);
            SaveAssetsDelayed();
            Debug.Log($"<color=#6699cc>AssetBuckets</color>: Updated {bucket.name}", bucket);
            return true;
        }

        private static bool BucketIsUnchanged(BaseAssetBucket bucket, List<Object> newObjects) {
            if (newObjects.Count != bucket.AssetRefs.Count) {
                return false;
            }

            for (int i = 0; i < bucket.AssetRefs.Count; i++) {
                if (newObjects[i] != bucket.AssetRefs[i].Asset) {
                    return false;
                }
            }

            return true;
        }

        private static void AddPath(string filePath, HashSet<string> changePaths) {
            string path = GetParentDirectory(filePath);

            while (!path.IsNullOrEmpty() && !changePaths.Contains(path)) {
                changePaths.Add(path);
                path = GetParentDirectory(path);
            }
        }

        private static string GetParentDirectory(string path) {
            int index = path.LastIndexOf('/');

            if (index <= 0) {
                return null;
            }

            return path.Substring(0, index);
        }

        private static void SaveAssetsDelayed() {
            if (s_WillSaveAssets) {
                return;
            }

            s_WillSaveAssets = true;
            EditorApplication.delayCall += SaveAssets;
        }

        private static void SaveAssets() {
            if (s_WillSaveAssets) {
                s_WillSaveAssets = false;
                AssetDatabase.SaveAssets();
            }
        }

        private static bool CanBeType(string path, Type testType) {
            Type assetType = AssetDatabase.GetMainAssetTypeAtPath(path);

            if (assetType == null) {
                return false;
            }

            if (assetType == testType || assetType.IsSubclassOf(testType)) {
                return true;
            }

            return assetType == typeof(GameObject) && typeof(Component).IsAssignableFrom(testType);
        }

        private static int AssetGuidSorter(Object a, Object b) {
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a, out string aGuid, out long _) &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(b, out string bGuid, out long _)) {
                return string.CompareOrdinal(aGuid, bGuid);
            }

            return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
