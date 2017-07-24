using UnityEngine;
using UnityEditor;

public class PackageTool
{
    [MenuItem("Package/Update Package")]
    static void UpdatePackage()
    {
        AssetDatabase.ExportPackage("Assets/MiniEngineAO", "MiniEngineAO.unitypackage", ExportPackageOptions.Recurse);
    }
}

