using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke bindings for the C/C++ native SDF plugin.
/// The plugin owns the CPU-side SDF grid and exposes a C ABI for Unity.
/// OpenVDB-backed cutting will be implemented behind this ABI.
/// </summary>
public static class SdfNativePlugin
{
    // Bump the filename when the native implementation changes so an open
    // Unity editor does not keep calling the already-loaded dylib image.
    private const string DllName = "cutting_kernel_v45";
    public const string PluginName = DllName;

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_plugin_init(
        int w, int h, int d,
        int sampleW, int sampleH, int sampleD,
        float voxelSize,
        float gridMinX, float gridMinY, float gridMinZ,
        float localSizeX, float localSizeY, float localSizeZ,
        int blankShape,
        float blankInnerRadius);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_plugin_shutdown();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_plugin_reset();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_compute_backend_capabilities();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_set_preferred_compute_backend(int backend);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_active_compute_backend();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_cut_selected_cutter(
        float startX, float startY, float startZ,
        float endX, float endY, float endZ,
        float axisX, float axisY, float axisZ,
        float rightX, float rightY, float rightZ,
        float radius, float height, float updateBand,
        int minX, int maxX,
        int minY, int maxY,
        int minZ, int maxZ);

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
    public static extern void sdf_check_connectivity_region(
        int minX, int maxX,
        int minY, int maxY,
        int minZ, int maxZ,
        int padding);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_try_apply_connectivity_cleanup();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_last_connectivity_component_count();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_last_connectivity_keep_core_count();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_last_connectivity_removal_candidate_count();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_debug_connectivity_summary(
        out int solidSampleCount,
        out int coreSolidSampleCount,
        out int componentCount,
        out int keepCoreComponentCount,
        out int removalCandidateCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern float sdf_sample_point(float x, float y, float z);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_sample_points(float[] points, float[] values, int count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern float sdf_sample_cut_point(float x, float y, float z);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_sample_cut_points(float[] points, float[] values, int count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_measure_surface_ray(
        float originX,
        float originY,
        float originZ,
        float directionX,
        float directionY,
        float directionZ,
        float maxDistance,
        float precision,
        float maxStep,
        int maxIterations,
        out float hitDistance,
        out float hitX,
        out float hitY,
        out float hitZ,
        out float hitValue);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_measure_thickness_ray(
        float originX,
        float originY,
        float originZ,
        float directionX,
        float directionY,
        float directionZ,
        float maxDistance,
        float precision,
        float maxStep,
        int maxIterations,
        out float entryDistance,
        out float exitDistance,
        out float thickness,
        out float entryX,
        out float entryY,
        out float entryZ,
        out float exitX,
        out float exitY,
        out float exitZ);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_cut_operation_count();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_get_data(float[] buffer, int length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_region(
        float[] buffer,
        int length,
        int minX, int maxX,
        int minY, int maxY,
        int minZ, int maxZ);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_sample_count();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_is_ready();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_set_profile_segment_count(int count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_set_profile_radius_samples(float[] samples, int count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_openvdb_available();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_openvdb_active_voxel_count();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_set_blank_mesh(
        float[] vertices,
        int vertexCount,
        int[] triangleIndices,
        int indexCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_stl_bounds(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
        float scalePercentX,
        float scalePercentY,
        float scalePercentZ,
        float[] bounds6,
        out int triangleCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_set_blank_stl_file(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath,
        float scalePercentX,
        float scalePercentY,
        float scalePercentZ,
        float[] bounds6,
        out int triangleCount);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_clear_blank_mesh();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_set_cutter_mesh(
        float[] vertices,
        int vertexCount,
        int[] triangleIndices,
        int indexCount,
        float voxelSize,
        float halfWidth);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void sdf_clear_cutter_mesh();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int sdf_get_cutter_active_voxel_count();
}
