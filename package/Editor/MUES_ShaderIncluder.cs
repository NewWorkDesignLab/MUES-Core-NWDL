#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.Collections.Generic;

namespace MUES.Editor
{
    public static class MUES_ShaderIncluder
    {
        private static readonly string[] URPShaderNames = new string[]
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Unlit",
        };

        private static readonly string[] BRPShaderNames = new string[]
        {
            "Standard",
            "Standard (Specular setup)",
            "Unlit/Texture",
            "Unlit/Color",
        };

        private static readonly string[] CommonShaderNames = new string[]
        {
            "Sprites/Default",
            "UI/Default",
        };

        private static readonly string[] PrefabSearchPaths = new string[]
        {
            "Packages/com.j0nes-l.mues-core",
            "Assets/MUES-Core",
            "Assets/MUES"
        };

        /// <summary>
        /// Determines if the project is using URP.
        /// </summary>
        private static bool IsUsingURP()
        {
            var currentRP = GraphicsSettings.currentRenderPipeline;
            if (currentRP == null) return false;
            
            return currentRP.GetType().Name.Contains("Universal");
        }

        /// <summary>
        /// Gets the required shader names based on the current render pipeline.
        /// </summary>
        private static string[] GetRequiredShaderNames()
        {
            var shaderList = new List<string>();  
            shaderList.AddRange(CommonShaderNames);
            
            if (IsUsingURP())
            {
                shaderList.AddRange(URPShaderNames);
                Debug.Log("[MUES] Detected Universal Render Pipeline");
            }
            else
            {
                shaderList.AddRange(BRPShaderNames);
                Debug.Log("[MUES] Detected Built-in Render Pipeline");
            }
            
            return shaderList.ToArray();
        }

        [MenuItem("MUES/Shaders/Add Required Shaders")]
        public static void AddRequiredShaders()
        {
            var serializedObject = GetGraphicsSettingsObject();
            if (serializedObject == null) return;

            var arrayProp = serializedObject.FindProperty("m_AlwaysIncludedShaders");

            var existingShaders = new HashSet<string>();
            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                var shader = arrayProp.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (shader != null)
                    existingShaders.Add(shader.name);
            }

            int addedCount = 0;
            string[] requiredShaders = GetRequiredShaderNames();

            foreach (string shaderName in requiredShaders)
            {
                if (existingShaders.Contains(shaderName)) continue;

                Shader shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    Debug.LogWarning($"[MUES] Shader not found: {shaderName}");
                    continue;
                }

                AddShaderToArray(arrayProp, shader);
                existingShaders.Add(shaderName);
                addedCount++;
                Debug.Log($"[MUES] Added: {shaderName}");
            }

            var allPrefabGuids = new List<string>();
            foreach (string searchPath in PrefabSearchPaths)
            {
                if (!AssetDatabase.IsValidFolder(searchPath)) continue;

                string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { searchPath });
                allPrefabGuids.AddRange(guids);
                Debug.Log($"[MUES] Found {guids.Length} prefabs in: {searchPath}");
            }

            if (allPrefabGuids.Count == 0)
            {
                Debug.LogWarning("[MUES] No prefabs found in any search path. Searched: " + string.Join(", ", PrefabSearchPaths));
            }

            foreach (string guid in allPrefabGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var renderers = prefab.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    if (renderer.sharedMaterials == null) continue;

                    foreach (var material in renderer.sharedMaterials)
                    {
                        if (material == null || material.shader == null) continue;
                        if (existingShaders.Contains(material.shader.name)) continue;

                        string shaderName = material.shader.name;
                        if (shaderName.StartsWith("Shader Graphs/")) continue;
                        if (shaderName.StartsWith("glTF/")) continue;
                        if (shaderName.StartsWith("Hidden/")) continue;

                        AddShaderToArray(arrayProp, material.shader);
                        existingShaders.Add(shaderName);
                        addedCount++;
                        Debug.Log($"[MUES] Added: {shaderName} (from {prefab.name})");
                    }
                }
            }

            serializedObject.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            Debug.Log($"[MUES] Done! Added {addedCount} shaders.");
        }

        [MenuItem("MUES/Shaders/List Included Shaders")]
        public static void ListIncludedShaders()
        {
            var serializedObject = GetGraphicsSettingsObject();
            if (serializedObject == null) return;

            var arrayProp = serializedObject.FindProperty("m_AlwaysIncludedShaders");

            Debug.Log($"[MUES] Included Shaders ({arrayProp.arraySize}):");
            for (int i = 0; i < arrayProp.arraySize; i++)
            {
                var shader = arrayProp.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                Debug.Log(shader != null ? $"  [{i}] {shader.name}" : $"  [{i}] (null)");
            }
        }

        [MenuItem("MUES/Shaders/Clear All Shaders")]
        public static void ClearAllShaders()
        {
            if (!EditorUtility.DisplayDialog("Clear Shaders",
                "Remove ALL shaders from 'Always Included Shaders'?\n\nThis may fix build crashes.",
                "Yes", "Cancel"))
                return;

            var serializedObject = GetGraphicsSettingsObject();
            if (serializedObject == null) return;

            var arrayProp = serializedObject.FindProperty("m_AlwaysIncludedShaders");
            arrayProp.ClearArray();

            serializedObject.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            Debug.Log("[MUES] All shaders cleared.");
        }

        /// <summary>
        /// Returns a SerializedObject for the GraphicsSettings asset, or null if it cannot be loaded.
        /// </summary>
        private static SerializedObject GetGraphicsSettingsObject()
        {
            var graphicsSettings = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            if (graphicsSettings == null)
            {
                Debug.LogError("[MUES] Could not load GraphicsSettings.");
                return null;
            }
            return new SerializedObject(graphicsSettings);
        }

        /// <summary>
        /// Returns a SerializedObject for the GraphicsSettings asset, or null if it cannot be loaded.
        /// </summary>
        private static void AddShaderToArray(SerializedProperty arrayProp, Shader shader)
        {
            int index = arrayProp.arraySize;
            arrayProp.InsertArrayElementAtIndex(index);
            arrayProp.GetArrayElementAtIndex(index).objectReferenceValue = shader;
        }
    }
}
#endif
