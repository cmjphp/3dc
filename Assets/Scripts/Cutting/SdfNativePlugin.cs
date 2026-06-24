using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke bindings for the sdf_island_remover native plugin (Zig).
/// The plugin maintains a CPU-side SDF grid, applies cuts, and runs
/// connected-component (island) analysis on a background thread.
/// </summary>
public static class SdfNativePlugin
{
    // Bump the filename when the native implementation changes so an open
    // Unity editor does not keep calling the already-loaded dylib image.
    private const string DllName = "sdf_island_remover_v8";

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_plugin_init(
        int w, int h, int d,
        int sampleW, int sampleH, int sampleD,
        float voxelSize,
        float gridMinX, float gridMinY, float gridMinZ,
        float localSizeX, float localSizeY, float localSizeZ);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_plugin_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_plugin_reset();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_cut_capsule(
        float startX, float startY, float startZ,
        float endX, float endY, float endZ,
        float radius,
        int minX, int maxX,
        int minY, int maxY,
        int minZ, int maxZ);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_cut_profile_cutter(
        float startX, float startY, float startZ,
        float endX, float endY, float endZ,
        float axisX, float axisY, float axisZ,
        float radius, float height, float updateBand,
        int minX, int maxX,
        int minY, int maxY,
        int minZ, int maxZ);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_check_connectivity();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_is_connectivity_ready();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_connectivity_result();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_consume_connectivity_result();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_apply_removal();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_get_data(float[] buffer, int length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_sample_count();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_is_ready();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_set_profile_segment_count(int count);
}
