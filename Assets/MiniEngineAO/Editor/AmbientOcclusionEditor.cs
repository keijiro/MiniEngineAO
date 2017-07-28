//#define SHOW_DETAILED_PROPS

using UnityEngine;
using UnityEditor;

namespace MiniEngineAO
{
    [CanEditMultipleObjects]
    [CustomEditor(typeof(AmbientOcclusion))]
    public class AmbientOcclusionEditor : Editor
    {
        SerializedProperty _intensity;
        SerializedProperty _thickness;

        #if SHOW_DETAILED_PROPS
        SerializedProperty _noiseFilterTolerance;
        SerializedProperty _blurTolerance;
        SerializedProperty _upsampleTolerance;
        #endif

        SerializedProperty _debug;

        static internal class Labels
        {
            public static readonly GUIContent intensity = new GUIContent(
                "Intensity", "The degree of darkness added by ambient occlusion."
            );

            public static readonly GUIContent thickness = new GUIContent(
                "Thickness", "The value modifies thickness of occluders. " +
                "This increases dark areas but also introduces dark halo around objects."
            );

            public static readonly GUIContent debug = new GUIContent(
                "Debug", "Visualizes ambient occlusion to assist debugging."
            );

            #if SHOW_DETAILED_PROPS
            public static readonly GUIContent blur = new GUIContent("Blur");
            public static readonly GUIContent denoise = new GUIContent("Denoise");
            public static readonly GUIContent filterTolerance = new GUIContent("Filter Tolerance");
            public static readonly GUIContent upsample = new GUIContent("Upsample");
            #endif
        }

        void OnEnable()
        {
            _intensity = serializedObject.FindProperty("_intensity");
            _thickness = serializedObject.FindProperty("_thickness");

            #if SHOW_DETAILED_PROPS
            _noiseFilterTolerance = serializedObject.FindProperty("_noiseFilterTolerance");
            _blurTolerance = serializedObject.FindProperty("_blurTolerance");
            _upsampleTolerance = serializedObject.FindProperty("_upsampleTolerance");
            #endif

            _debug = serializedObject.FindProperty("_debug");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.PropertyField(_intensity, Labels.intensity);
            EditorGUILayout.PropertyField(_thickness, Labels.thickness);

            #if SHOW_DETAILED_PROPS
            EditorGUILayout.LabelField(Labels.filterTolerance);
            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_noiseFilterTolerance, Labels.denoise);
            EditorGUILayout.PropertyField(_blurTolerance, Labels.blur);
            EditorGUILayout.PropertyField(_upsampleTolerance, Labels.upsample);
            EditorGUI.indentLevel--;
            #endif

            #if SHOW_DETAILED_PROPS
            EditorGUILayout.PropertyField(_debug, Labels.debug);
            #else
            EditorGUI.BeginChangeCheck();
            EditorGUI.showMixedValue = _debug.hasMultipleDifferentValues;
            var debug = EditorGUILayout.Toggle(Labels.debug, _debug.intValue > 0);
            EditorGUI.showMixedValue = false;
            if (EditorGUI.EndChangeCheck())
                _debug.intValue = debug ? 17 : 0; // 17 == AO result buffer
            #endif

            serializedObject.ApplyModifiedProperties();
        }
    }
}
