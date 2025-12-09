#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace MUES.Editor
{
    [InitializeOnLoad]
    public static class MUES_LayerSetup
    {
        static MUES_LayerSetup()
        {
            EnsureLayer("Wall");
            EnsureLayer("Floor");
        }

        private static void EnsureLayer(string layerName)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogError("[MUES_LayerSetup] TagManager.asset not found.");
                return;
            }

            var tagManager = new SerializedObject(assets[0]);
            var layersProp = tagManager.FindProperty("layers");

            for (int i = 0; i < layersProp.arraySize; i++)
            {
                var sp = layersProp.GetArrayElementAtIndex(i);
                if (sp != null && sp.stringValue == layerName)
                    return;
            }

            for (int i = 8; i < layersProp.arraySize; i++)
            {
                var sp = layersProp.GetArrayElementAtIndex(i);
                if (sp != null && string.IsNullOrEmpty(sp.stringValue))
                {
                    sp.stringValue = layerName;
                    tagManager.ApplyModifiedProperties();
                    AssetDatabase.SaveAssets();
                    Debug.Log($"[MUES_LayerSetup] Layer '{layerName}' created at index {i}.");
                    return;
                }
            }

           ConsoleMessage.Send(true, "[MUES_LayerSetup] No free layer slot available for 'Wall'. " + "Please add it manually in Project Settings > Tags and Layers.", Color.red);
        }
    }
}
#endif
