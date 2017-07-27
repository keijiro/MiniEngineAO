//#define SHOW_DETAILED_PROPS

using UnityEngine;
using UnityEditor;

namespace MiniEngineAO
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(AmbientOcclusion))]
    public class AmbientOcclusionEditor : Editor
    {
        SerializedProperty _strength;
        SerializedProperty _rejectionFalloff;

        #if SHOW_DETAILED_PROPS
        SerializedProperty _noiseFilterTolerance;
        SerializedProperty _blurTolerance;
        SerializedProperty _upsampleTolerance;
        SerializedProperty _debug;
        #endif

        static internal class Labels
        {
            #if SHOW_DETAILED_PROPS
            public static readonly GUIContent blur = new GUIContent("Blur");
            public static readonly GUIContent denoise = new GUIContent("Denoise");
            public static readonly GUIContent filterTolerance = new GUIContent("Filter Tolerance");
            public static readonly GUIContent upsample = new GUIContent("Upsample");
            #endif
        }

        void OnEnable()
        {
            _strength = serializedObject.FindProperty("_strength");
            _rejectionFalloff = serializedObject.FindProperty("_rejectionFalloff");

            #if SHOW_DETAILED_PROPS
            _noiseFilterTolerance = serializedObject.FindProperty("_noiseFilterTolerance");
            _blurTolerance = serializedObject.FindProperty("_blurTolerance");
            _upsampleTolerance = serializedObject.FindProperty("_upsampleTolerance");
            _debug = serializedObject.FindProperty("_debug");
            #endif
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_strength);
            EditorGUILayout.PropertyField(_rejectionFalloff);

            #if SHOW_DETAILED_PROPS
            EditorGUILayout.LabelField(Labels.filterTolerance);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_noiseFilterTolerance, Labels.denoise);
            EditorGUILayout.PropertyField(_blurTolerance, Labels.blur);
            EditorGUILayout.PropertyField(_upsampleTolerance, Labels.upsample);
            EditorGUI.indentLevel--;
            EditorGUILayout.PropertyField(_debug);
            #endif

            serializedObject.ApplyModifiedProperties();
        }
    }
}
