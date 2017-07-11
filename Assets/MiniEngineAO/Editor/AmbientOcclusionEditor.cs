using UnityEngine;
using UnityEditor;

namespace MiniEngineAO
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(AmbientOcclusion))]
    public class AmbientOcclusionEditor : Editor
    {
        SerializedProperty _noiseFilterTolerance;
        SerializedProperty _blurTolerance;
        SerializedProperty _upsampleTolerance;
        SerializedProperty _rejectionFalloff;
        SerializedProperty _accentuation;

        static internal class Labels
        {
            public static readonly GUIContent filterTolerance = new GUIContent("Filter Tolerance");
            public static readonly GUIContent denoise = new GUIContent("Denoise");
            public static readonly GUIContent blur = new GUIContent("Blur");
            public static readonly GUIContent upsample = new GUIContent("Upsample");
        }

        void OnEnable()
        {
            _noiseFilterTolerance = serializedObject.FindProperty("_noiseFilterTolerance");
            _blurTolerance = serializedObject.FindProperty("_blurTolerance");
            _upsampleTolerance = serializedObject.FindProperty("_upsampleTolerance");
            _rejectionFalloff = serializedObject.FindProperty("_rejectionFalloff");
            _accentuation = serializedObject.FindProperty("_accentuation");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField(Labels.filterTolerance);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_noiseFilterTolerance, Labels.denoise);
            EditorGUILayout.PropertyField(_blurTolerance, Labels.blur);
            EditorGUILayout.PropertyField(_upsampleTolerance, Labels.upsample);
            EditorGUI.indentLevel--;

            EditorGUILayout.PropertyField(_rejectionFalloff);
            EditorGUILayout.PropertyField(_accentuation);

            if (EditorGUI.EndChangeCheck())
                foreach (AmbientOcclusion ao in targets) ao.RequestRebuildCommandBuffers();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
