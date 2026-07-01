using System.IO;

public static class ImportedStlMeshLoader
{
    public static bool IsStlPath(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            string.Equals(Path.GetExtension(path), ".stl", System.StringComparison.OrdinalIgnoreCase);
    }
}
