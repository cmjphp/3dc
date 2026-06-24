#if UNITY_EDITOR
using UnityEditor;

public sealed class CutterModelAssetPostprocessor : AssetPostprocessor
{
    private const string CutterResourceFolder = "Assets/Resources/Cutting/";

    private void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(CutterResourceFolder, System.StringComparison.Ordinal))
        {
            return;
        }

        if (assetImporter is not ModelImporter modelImporter)
        {
            return;
        }

        modelImporter.isReadable = true;
        modelImporter.importCameras = false;
        modelImporter.importLights = false;
    }
}
#endif
