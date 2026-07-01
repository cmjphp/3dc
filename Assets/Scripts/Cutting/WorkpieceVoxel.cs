using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

public enum WorkpieceSurfaceMode
{
    BlockyVoxels,
    SmoothSdf
}

public enum WorkpieceBlankShape
{
    Box,
    Cylinder,
    Tube,
    HalfTube,
    ImportedMesh
}

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public sealed class WorkpieceVoxel : MonoBehaviour
{
    public const int MaxCutterProfileRadiusSamples = 32;
    public const int MaxCutterAngularProfileAxialSamples = 32;
    public const int MaxCutterAngularProfileAngleSamples = 64;
    public const int MaxCutterAngularProfileSamples =
        MaxCutterAngularProfileAxialSamples * MaxCutterAngularProfileAngleSamples;

    [Header("Voxel Grid")]
    [Min(1)] public int width = 116;
    [Min(1)] public int height = 50;
    [Min(1)] public int depth = 77;
    [Min(0.001f)] public float voxelSize = 0.025f;
    [Min(100000)] public int maxAllocatedSdfSamples = 64000000;
    public WorkpieceBlankShape blankShape = WorkpieceBlankShape.Box;
    [Min(0f)] public float blankInnerRadius = 20f;
    public GameObject importedBlankMeshRoot;
    public Vector3 importedBlankScalePercent = Vector3.one * 100f;

    [Header("Surface")]
    public WorkpieceSurfaceMode surfaceMode = WorkpieceSurfaceMode.SmoothSdf;
    public bool asyncSmoothMeshRebuild = true;
    [Min(100)] public int smoothRebuildCellsPerFrame = 3000;

    [Header("Chunked Smooth Mesh")]
    public bool useChunkedSmoothMesh = true;
    [Min(2)] public int chunkSize = 8;
    [Min(1)] public int chunkRebuildsPerFrame = 6;
    [Min(0)] public int dirtyChunkNeighborShell = 1;
    public bool rebuildCoreChunksImmediately = true;
    [Min(1)] public int immediateChunkRebuildLimit = 12;

    [Header("Native Detached Material Cleanup")]
    public bool removeDetachedParts = true;
    public bool automaticDetachedCleanup = true;
    [Min(0.01f)] public float detachedCleanupInterval = 0.08f;

    [Header("GPU SDF Display Buffer")]
    [FormerlySerializedAs("useGpuSdfCutting")]
    public bool useGpuSdfDisplayBuffer;
    [FormerlySerializedAs("sdfCutCompute")]
    public ComputeShader sdfDisplayBufferCompute;
    [Min(2)] public int profileSegmentCount = 6;
    public bool preferNativeMeshCutter;

    [Header("GPU Surface Rendering")]
    public bool useGpuSurfaceRendering = true;
    public bool useExpandedDisplayBounds;
    public Vector3 expandedDisplaySize = Vector3.one;
    public Vector3 expandedDisplayCenter;
    public ComputeShader gpuSurfaceCompute;
    public Material gpuSurfaceMaterial;
    [Min(10000)] public int maxGpuTriangles = 1000000;
    [Min(16)] public int gpuRaymarchMaxSteps = 192;
    [Range(0.25f, 2f)] public float gpuRaymarchStepScale = 0.75f;
    public bool useGpuVisualCutPreview;
    [Min(16)] public int maxGpuVisualCutOperations = 512;
    public Color gpuSurfaceColor = new Color(0.72f, 0.76f, 0.78f, 1f);

    [Header("Native Cut Detail Display")]
    public bool useNativeCutDetailDisplay;
    [Min(0.001f)] public float nativeCutDetailVoxelSize = 0.05f;
    [Min(4096)] public int maxNativeCutDetailSamples = 350000;
    [Min(1)] public int maxNativeCutDetailTiles = 6;
    [Min(4096)] public int maxNativeCutDetailCachedSamples = 1200000;
    [Min(0.01f)] public float nativeCutDetailUpdateInterval = 0.06f;
    [Min(0f)] public float nativeCutDetailPaddingMm = 0.35f;
    [Min(1024)] public int nativeCutDetailBatchSize = 32768;

    [Header("Runtime")]
    public bool initializeOnStart = true;
    public bool updateCollider;

    [Header("Diagnostics")]
    public bool logNativeCutDiagnostics;

    private const float IsoLevel = 0f;
    private const float NativeCutDetailAirValue = -1000000f;
    private const float MaxImportedBlankAxisMm = 1000f;
    private const float NativeConnectivityIdleDelaySeconds = 0.25f;
    private float SdfChangeEpsilon
    {
        get { return voxelSize * 0.0001f; }
    }

    private static readonly int[,] CubeCorners =
    {
        { 0, 0, 0 },
        { 1, 0, 0 },
        { 1, 0, 1 },
        { 0, 0, 1 },
        { 0, 1, 0 },
        { 1, 1, 0 },
        { 1, 1, 1 },
        { 0, 1, 1 }
    };

    private static readonly int[,] Tetrahedra =
    {
        { 0, 5, 1, 6 },
        { 0, 1, 2, 6 },
        { 0, 2, 3, 6 },
        { 0, 3, 7, 6 },
        { 0, 7, 4, 6 },
        { 0, 4, 5, 6 }
    };

    private bool[,,] voxels;
    private float[,,] sdfSamples;
    private MeshFilter meshFilter;
    private MeshCollider meshCollider;
    private MeshRenderer meshRenderer;
    private Mesh runtimeMesh;
    private Coroutine rebuildCoroutine;
    private Coroutine chunkRebuildCoroutine;
    private bool rebuildRequestedWhileRunning;
    private VoxelChunk[] chunks;
    private bool[] chunkQueued;
    private int[] immediateChunkMarks;
    private int immediateChunkMarkVersion;
    private readonly Queue<int> dirtyChunkQueue = new Queue<int>(128);
    private ComputeBuffer sdfSampleBuffer;
    private ComputeBuffer sdfRegionUploadBuffer;
    private int sdfRegionUploadBufferCapacity;
    private float[] sdfLinearSamples;
    private float[] sdfRegionSamples;
    private int sdfCopyRegionKernel = -1;
    private bool sdfGpuReady;
    private bool sdfGpuUnavailable;
    private bool gpuSurfaceUnavailable;
    private Material runtimeGpuSurfaceMaterial;
    private Mesh gpuProxyMesh;
    private Vector3 cachedGpuProxySize;
    private Vector3 cachedGpuProxyCenter;
    private bool gpuSurfaceReady;
    private ComputeBuffer gpuVisualCutBuffer;
    private int gpuVisualCutBufferCapacity;
    private bool gpuVisualCutsDirty;
    private ComputeBuffer nativeCutDetailBuffer;
    private int nativeCutDetailBufferCapacity;
    private ComputeBuffer nativeCutDetailTileBuffer;
    private int nativeCutDetailTileBufferCapacity;
    private float[] nativeCutDetailValues;
    private float[] nativeCutDetailPackedValues;
    private float[] nativeCutDetailBatchPoints;
    private float[] nativeCutDetailBatchValues;
    private Vector4[] nativeCutDetailTileData;
    private bool nativeCutDetailReady;
    private Coroutine nativeCutDetailCoroutine;
    private int nativeCutDetailRequestVersion;
    private float nextNativeCutDetailUpdateTime;
    private ComputeBuffer cutterAngularProfileMinBuffer;
    private ComputeBuffer cutterAngularProfileMaxBuffer;
    private int chunkCountX;
    private int chunkCountY;
    private int chunkCountZ;
    private int cachedChunkSize;
    private int cachedCellWidth;
    private int cachedCellHeight;
    private int cachedCellDepth;
    private bool sdfNativeReady;
    private float nextNativeConnectivityCheckTime;
    private bool nativeConnectivityDirty;
    private bool nativeConnectivityInFlight;
    private bool nativeConnectivityStalled;
    private float nativeConnectivityCheckStartTime;
    private float nextNativeConnectivityStallLogTime;
    private float lastNativeConnectivityDirtyTime;
    private int nativeConnectivityCheckSerial;
    private bool nativeConnectivityHasRegion;
    private int nativeConnectivityMinX;
    private int nativeConnectivityMaxX;
    private int nativeConnectivityMinY;
    private int nativeConnectivityMaxY;
    private int nativeConnectivityMinZ;
    private int nativeConnectivityMaxZ;
    private float[] nativeCutterMeshVertices;
    private int[] nativeCutterMeshTriangleIndices;
    private int nativeCutterMeshVertexCount;
    private int nativeCutterMeshIndexCount;
    private Vector3 nativeCutterMeshLocalMin;
    private Vector3 nativeCutterMeshLocalMax;
    private float[] nativeBlankMeshVertices;
    private int[] nativeBlankMeshTriangleIndices;
    private int nativeBlankMeshVertexCount;
    private int nativeBlankMeshIndexCount;
    private Vector3 nativeBlankMeshLocalMin;
    private Vector3 nativeBlankMeshLocalMax;
    [SerializeField] private int nativeBlankMeshTriangleCount;
    [SerializeField] private int nativeCutterActiveVoxelCount;
    [SerializeField] private int nativeOpenVdbActiveVoxelCount;
    private float nextNativeNoChangeLogTime;
    private float nextNativePositiveCutLogTime;
    private float nextNativeNoOverlapLogTime;

    private readonly List<Vector3> vertices = new List<Vector3>(65536);
    private readonly List<int> triangles = new List<int>(65536);
    private readonly List<Vector3> normals = new List<Vector3>(65536);
    private readonly List<Vector2> uvs = new List<Vector2>(65536);
    private readonly Vector3[] tetraPositions = new Vector3[4];
    private readonly float[] tetraValues = new float[4];
    private readonly bool[] tetraInside = new bool[4];
    private readonly List<GpuVisualCutOperation> gpuVisualCutOperations = new List<GpuVisualCutOperation>(64);
    private readonly List<NativeCutDetailTile> nativeCutDetailTiles = new List<NativeCutDetailTile>(8);
    private readonly float[] cutterProfileRadiusSamples = new float[MaxCutterProfileRadiusSamples];
    private readonly float[] cutterAngularProfileMinRadiusSamples = new float[MaxCutterAngularProfileSamples];
    private readonly float[] cutterAngularProfileMaxRadiusSamples = new float[MaxCutterAngularProfileSamples];
    private readonly float[] nativeCutDetailDummy = { NativeCutDetailAirValue };
    private readonly Vector4[] nativeCutDetailTileDummy = { Vector4.zero, Vector4.zero };
    private int cutterProfileRadiusSampleCount;
    private int cutterAngularProfileAxialSampleCount;
    private int cutterAngularProfileAngleSampleCount;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GpuVisualCutOperation
    {
        public Vector4 startRadius;
        public Vector4 endHeight;
        public Vector4 axis;
        public Vector4 profileMeta;
        public Vector4 profile0;
        public Vector4 profile1;
        public Vector4 profile2;
        public Vector4 profile3;
        public Vector4 profile4;
        public Vector4 profile5;
        public Vector4 profile6;
        public Vector4 profile7;
    }

    private sealed class NativeCutDetailTile
    {
        public Vector3 min;
        public float step;
        public Vector3Int size;
        public float[] values;
        public int sampleCount;
    }

    private sealed class VoxelChunk
    {
        public int index;
        public int chunkX;
        public int chunkY;
        public int chunkZ;
        public int startX;
        public int endX;
        public int startY;
        public int endY;
        public int startZ;
        public int endZ;
        public Mesh mesh;
        public MeshFilter meshFilter;
        public MeshRenderer meshRenderer;
        public MeshCollider meshCollider;
        public GameObject gameObject;
    }

    public bool IsInitialized
    {
        get
        {
            if (surfaceMode == WorkpieceSurfaceMode.BlockyVoxels)
            {
                return voxels != null;
            }

            return sdfSamples != null || (UsesGpuSurfaceRendering && sdfGpuReady);
        }
    }

    public bool IsNativeSdfReady
    {
        get
        {
            if (surfaceMode != WorkpieceSurfaceMode.SmoothSdf || !sdfNativeReady)
            {
                return false;
            }

            try
            {
                return SdfNativePlugin.sdf_is_ready() != 0;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Native SDF readiness check failed: {ex.Message}", this);
                sdfNativeReady = false;
                return false;
            }
        }
    }

    public Vector3 LocalSize
    {
        get { return new Vector3(width, height, depth) * voxelSize; }
    }

    public Bounds LocalBounds
    {
        get { return new Bounds(DisplayCenter, DisplaySize); }
    }

    public Vector3 DisplaySize
    {
        get
        {
            if (!useExpandedDisplayBounds)
            {
                return LocalSize;
            }

            return new Vector3(
                Mathf.Max(LocalSize.x, expandedDisplaySize.x),
                Mathf.Max(LocalSize.y, expandedDisplaySize.y),
                Mathf.Max(LocalSize.z, expandedDisplaySize.z));
        }
    }

    public Vector3 DisplayCenter
    {
        get { return useExpandedDisplayBounds ? expandedDisplayCenter : Vector3.zero; }
    }

    private int SampleWidth
    {
        get { return width + 3; }
    }

    private int SampleHeight
    {
        get { return height + 3; }
    }

    private int SampleDepth
    {
        get { return depth + 3; }
    }

    private int CellWidth
    {
        get { return SampleWidth - 1; }
    }

    private int CellHeight
    {
        get { return SampleHeight - 1; }
    }

    private int CellDepth
    {
        get { return SampleDepth - 1; }
    }

    private int SdfSampleCount
    {
        get { return SampleWidth * SampleHeight * SampleDepth; }
    }

    private long SdfSampleCountLong
    {
        get { return (long)SampleWidth * SampleHeight * SampleDepth; }
    }

    private bool UsesChunkedSmoothMesh
    {
        get { return surfaceMode == WorkpieceSurfaceMode.SmoothSdf && useChunkedSmoothMesh && !UsesGpuSurfaceRendering; }
    }

    private bool UsesGpuSurfaceRendering
    {
        get { return surfaceMode == WorkpieceSurfaceMode.SmoothSdf && useGpuSurfaceRendering; }
    }

    private bool UsesGpuSdfBuffer
    {
        get { return useGpuSdfDisplayBuffer || UsesGpuSurfaceRendering; }
    }

    private bool HasAngularCutterProfile
    {
        get
        {
            return cutterAngularProfileAxialSampleCount >= 2 &&
                   cutterAngularProfileAngleSampleCount >= 3;
        }
    }

    private bool HasNativeMeshCutter
    {
        get { return sdfNativeReady && nativeCutterActiveVoxelCount > 0; }
    }

    public bool TrySampleNativeSdfWorld(Vector3 worldPoint, out float value)
    {
        return TrySampleNativeSdfLocal(transform.InverseTransformPoint(worldPoint), out value);
    }

    public bool TrySampleNativeSdfLocal(Vector3 localPoint, out float value)
    {
        value = 0f;
        if (!sdfNativeReady)
        {
            return false;
        }

        try
        {
            value = SdfNativePlugin.sdf_sample_point(localPoint.x, localPoint.y, localPoint.z);
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native SDF sample failed: {ex.Message}", this);
            sdfNativeReady = false;
            return false;
        }
    }

    private void Awake()
    {
        EnsureComponents();
    }

    private void Start()
    {
        if (initializeOnStart)
        {
            ResetWorkpiece();
        }
    }

    private void LateUpdate()
    {
        PollNativeConnectivity();
        DrawGpuSurface();
    }

    private void OnDestroy()
    {
        ShutdownNativeSdfPlugin();
        if (Application.isPlaying && runtimeMesh != null)
        {
            Destroy(runtimeMesh);
        }

        DestroyChunks();
        ReleaseGpuSdfResources();
        ReleaseGpuSurfaceResources();
        ReleaseCutterAngularProfileBuffers();
    }

    private void OnValidate()
    {
        width = Mathf.Max(1, width);
        height = Mathf.Max(1, height);
        depth = Mathf.Max(1, depth);
        voxelSize = Mathf.Max(0.001f, voxelSize);
        maxAllocatedSdfSamples = Mathf.Max(100000, maxAllocatedSdfSamples);
        blankInnerRadius = Mathf.Max(0f, blankInnerRadius);
        smoothRebuildCellsPerFrame = Mathf.Max(100, smoothRebuildCellsPerFrame);
        chunkSize = Mathf.Max(2, chunkSize);
        chunkRebuildsPerFrame = Mathf.Max(1, chunkRebuildsPerFrame);
        immediateChunkRebuildLimit = Mathf.Max(1, immediateChunkRebuildLimit);
        detachedCleanupInterval = Mathf.Max(0.01f, detachedCleanupInterval);
        maxGpuTriangles = Mathf.Max(10000, maxGpuTriangles);
        gpuRaymarchMaxSteps = Mathf.Max(16, gpuRaymarchMaxSteps);
        gpuRaymarchStepScale = Mathf.Clamp(gpuRaymarchStepScale, 0.25f, 2f);
        maxGpuVisualCutOperations = Mathf.Max(16, maxGpuVisualCutOperations);
        nativeCutDetailVoxelSize = Mathf.Max(0.001f, nativeCutDetailVoxelSize);
        maxNativeCutDetailSamples = Mathf.Max(4096, maxNativeCutDetailSamples);
        maxNativeCutDetailTiles = Mathf.Max(1, maxNativeCutDetailTiles);
        maxNativeCutDetailCachedSamples = Mathf.Max(maxNativeCutDetailSamples, maxNativeCutDetailCachedSamples);
        nativeCutDetailUpdateInterval = Mathf.Max(0.01f, nativeCutDetailUpdateInterval);
        nativeCutDetailPaddingMm = Mathf.Max(0f, nativeCutDetailPaddingMm);
        nativeCutDetailBatchSize = Mathf.Max(1024, nativeCutDetailBatchSize);
        profileSegmentCount = Mathf.Max(2, profileSegmentCount);
        expandedDisplaySize = new Vector3(
            Mathf.Max(0.001f, expandedDisplaySize.x),
            Mathf.Max(0.001f, expandedDisplaySize.y),
            Mathf.Max(0.001f, expandedDisplaySize.z));
    }

    public void SetProfileSegmentCount(int segmentCount)
    {
        profileSegmentCount = Mathf.Max(2, segmentCount);
        if (!sdfNativeReady)
        {
            return;
        }

        try
        {
            SdfNativePlugin.sdf_set_profile_segment_count(profileSegmentCount);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native SDF profile segment update failed: {ex.Message}", this);
            sdfNativeReady = false;
        }
    }

    public void SetProfileRadiusSamples(float[] normalizedRadiusSamples, int sampleCount)
    {
        ClearGpuVisualCuts();
        cutterProfileRadiusSampleCount = 0;
        if (normalizedRadiusSamples == null || sampleCount < 2)
        {
            UploadNativeProfileRadiusSamples();
            return;
        }

        int count = Mathf.Clamp(sampleCount, 2, MaxCutterProfileRadiusSamples);
        for (int i = 0; i < MaxCutterProfileRadiusSamples; i++)
        {
            cutterProfileRadiusSamples[i] = 0f;
        }

        float maxSample = 0f;
        for (int i = 0; i < count; i++)
        {
            float sample = normalizedRadiusSamples[i];
            if (float.IsNaN(sample) || float.IsInfinity(sample))
            {
                sample = 0f;
            }

            sample = Mathf.Clamp(sample, 0f, 2f);
            cutterProfileRadiusSamples[i] = sample;
            maxSample = Mathf.Max(maxSample, sample);
        }

        if (maxSample <= 0.000001f)
        {
            UploadNativeProfileRadiusSamples();
            return;
        }

        cutterProfileRadiusSampleCount = count;
        UploadNativeProfileRadiusSamples();
    }

    private void UploadNativeProfileRadiusSamples()
    {
        if (!sdfNativeReady)
        {
            return;
        }

        try
        {
            SdfNativePlugin.sdf_set_profile_radius_samples(
                cutterProfileRadiusSamples,
                cutterProfileRadiusSampleCount);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native SDF profile radius update failed: {ex.Message}", this);
            sdfNativeReady = false;
        }
    }

    public void SetAngularProfileRadiusSamples(
        float[] normalizedMinRadiusSamples,
        float[] normalizedMaxRadiusSamples,
        int axialSampleCount,
        int angleSampleCount)
    {
        ClearGpuVisualCuts();
        cutterAngularProfileAxialSampleCount = 0;
        cutterAngularProfileAngleSampleCount = 0;
        ReleaseCutterAngularProfileBuffers();
        for (int i = 0; i < MaxCutterAngularProfileSamples; i++)
        {
            cutterAngularProfileMinRadiusSamples[i] = 0f;
            cutterAngularProfileMaxRadiusSamples[i] = 0f;
        }

        if (normalizedMinRadiusSamples == null ||
            normalizedMaxRadiusSamples == null ||
            axialSampleCount < 2 ||
            angleSampleCount < 3)
        {
            return;
        }

        int axialCount = Mathf.Clamp(axialSampleCount, 2, MaxCutterAngularProfileAxialSamples);
        int angleCount = Mathf.Clamp(angleSampleCount, 3, MaxCutterAngularProfileAngleSamples);
        int sampleCount = axialCount * angleCount;
        if (normalizedMinRadiusSamples.Length < sampleCount || normalizedMaxRadiusSamples.Length < sampleCount)
        {
            return;
        }

        bool hasSolidSample = false;
        for (int i = 0; i < sampleCount; i++)
        {
            float minRadius = normalizedMinRadiusSamples[i];
            float maxRadius = normalizedMaxRadiusSamples[i];
            if (float.IsNaN(minRadius) || float.IsInfinity(minRadius))
            {
                minRadius = 0f;
            }

            if (float.IsNaN(maxRadius) || float.IsInfinity(maxRadius))
            {
                maxRadius = 0f;
            }

            minRadius = Mathf.Clamp(minRadius, 0f, 2f);
            maxRadius = Mathf.Clamp(maxRadius, 0f, 2f);
            if (maxRadius < minRadius)
            {
                maxRadius = minRadius;
            }

            cutterAngularProfileMinRadiusSamples[i] = minRadius;
            cutterAngularProfileMaxRadiusSamples[i] = maxRadius;
            hasSolidSample |= maxRadius > 0.000001f;
        }

        if (!hasSolidSample)
        {
            return;
        }

        cutterAngularProfileAxialSampleCount = axialCount;
        cutterAngularProfileAngleSampleCount = angleCount;
        UploadCutterAngularProfileBuffers();
    }

    public void SetNativeCutterMesh(
        float[] vertices,
        int vertexCount,
        int[] triangleIndices,
        int indexCount)
    {
        if (vertices == null ||
            triangleIndices == null ||
            vertexCount <= 0 ||
            indexCount < 3 ||
            vertices.Length < vertexCount * 3 ||
            triangleIndices.Length < indexCount)
        {
            ClearNativeCutterMesh();
            return;
        }

        nativeCutterMeshVertexCount = vertexCount;
        nativeCutterMeshIndexCount = indexCount;
        nativeCutterMeshVertices = new float[vertexCount * 3];
        nativeCutterMeshTriangleIndices = new int[indexCount];
        System.Array.Copy(vertices, nativeCutterMeshVertices, nativeCutterMeshVertices.Length);
        System.Array.Copy(triangleIndices, nativeCutterMeshTriangleIndices, nativeCutterMeshTriangleIndices.Length);

        nativeCutterMeshLocalMin = new Vector3(vertices[0], vertices[1], vertices[2]);
        nativeCutterMeshLocalMax = nativeCutterMeshLocalMin;
        for (int i = 1; i < vertexCount; i++)
        {
            Vector3 vertex = new Vector3(
                vertices[i * 3 + 0],
                vertices[i * 3 + 1],
                vertices[i * 3 + 2]);
            nativeCutterMeshLocalMin = Vector3.Min(nativeCutterMeshLocalMin, vertex);
            nativeCutterMeshLocalMax = Vector3.Max(nativeCutterMeshLocalMax, vertex);
        }

        UploadNativeCutterMesh();
        ClearGpuVisualCuts();
    }

    public void ClearNativeCutterMesh()
    {
        nativeCutterMeshVertices = null;
        nativeCutterMeshTriangleIndices = null;
        nativeCutterMeshVertexCount = 0;
        nativeCutterMeshIndexCount = 0;
        nativeCutterMeshLocalMin = Vector3.zero;
        nativeCutterMeshLocalMax = Vector3.zero;
        nativeCutterActiveVoxelCount = 0;
        ClearGpuVisualCuts();

        if (!sdfNativeReady)
        {
            return;
        }

        try
        {
            SdfNativePlugin.sdf_clear_cutter_mesh();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native cutter mesh clear failed: {ex.Message}", this);
            sdfNativeReady = false;
        }
    }

    public bool RefreshImportedBlankMeshFromSource()
    {
        if (blankShape != WorkpieceBlankShape.ImportedMesh)
        {
            return true;
        }

        if (importedBlankMeshRoot == null ||
            !TryExtractImportedBlankMesh(
                importedBlankMeshRoot,
                out float[] vertices,
                out int vertexCount,
                out int[] triangleIndices,
                out int indexCount))
        {
            ClearNativeBlankMesh();
            Debug.LogWarning(
                "Imported blank mesh extraction failed. Assign a readable third-party mesh root before using ImportedMesh blank shape.",
                this);
            return false;
        }

        return SetNativeBlankMesh(vertices, vertexCount, triangleIndices, indexCount);
    }

    public bool SetNativeBlankMesh(
        float[] vertices,
        int vertexCount,
        int[] triangleIndices,
        int indexCount)
    {
        if (vertices == null ||
            triangleIndices == null ||
            vertexCount <= 0 ||
            indexCount < 3 ||
            vertices.Length < vertexCount * 3 ||
            triangleIndices.Length < indexCount)
        {
            ClearNativeBlankMesh();
            return false;
        }

        Vector3 min = new Vector3(vertices[0], vertices[1], vertices[2]);
        Vector3 max = min;
        for (int i = 1; i < vertexCount; i++)
        {
            Vector3 vertex = new Vector3(
                vertices[i * 3 + 0],
                vertices[i * 3 + 1],
                vertices[i * 3 + 2]);
            min = Vector3.Min(min, vertex);
            max = Vector3.Max(max, vertex);
        }

        if (!ValidateImportedBlankMeshBounds(min, max, out string reason))
        {
            ClearNativeBlankMesh();
            Debug.LogWarning($"Imported blank mesh rejected: {reason}", this);
            return false;
        }

        nativeBlankMeshVertexCount = vertexCount;
        nativeBlankMeshIndexCount = indexCount;
        nativeBlankMeshVertices = new float[vertexCount * 3];
        nativeBlankMeshTriangleIndices = new int[indexCount];
        System.Array.Copy(vertices, nativeBlankMeshVertices, nativeBlankMeshVertices.Length);
        System.Array.Copy(triangleIndices, nativeBlankMeshTriangleIndices, nativeBlankMeshTriangleIndices.Length);
        nativeBlankMeshLocalMin = min;
        nativeBlankMeshLocalMax = max;
        nativeBlankMeshTriangleCount = indexCount / 3;

        if (sdfNativeReady && blankShape == WorkpieceBlankShape.ImportedMesh)
        {
            return UploadNativeBlankMesh();
        }

        return true;
    }

    public void ClearNativeBlankMesh()
    {
        bool invalidatesImportedBlank = blankShape == WorkpieceBlankShape.ImportedMesh;
        nativeBlankMeshVertices = null;
        nativeBlankMeshTriangleIndices = null;
        nativeBlankMeshVertexCount = 0;
        nativeBlankMeshIndexCount = 0;
        nativeBlankMeshLocalMin = Vector3.zero;
        nativeBlankMeshLocalMax = Vector3.zero;
        nativeBlankMeshTriangleCount = 0;

        if (!sdfNativeReady)
        {
            return;
        }

        try
        {
            SdfNativePlugin.sdf_clear_blank_mesh();
            if (invalidatesImportedBlank)
            {
                sdfNativeReady = false;
                nativeOpenVdbActiveVoxelCount = 0;
                ClearGpuVisualCuts();
                ClearNativeCutDetailDisplay(false);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native blank mesh clear failed: {ex.Message}", this);
            sdfNativeReady = false;
        }
    }

    private bool ValidateImportedBlankMeshBounds(Vector3 min, Vector3 max, out string reason)
    {
        Vector3 size = max - min;
        const float axisTolerance = 0.001f;
        float envelopeTolerance = Mathf.Max(voxelSize, 0.001f);
        if (size.x > MaxImportedBlankAxisMm + axisTolerance ||
            size.y > MaxImportedBlankAxisMm + axisTolerance ||
            size.z > MaxImportedBlankAxisMm + axisTolerance)
        {
            reason =
                $"axis size {size.x:0.###}x{size.y:0.###}x{size.z:0.###}mm exceeds the 1000mm imported-model limit.";
            return false;
        }

        Vector3 stockMin = GetLocalMin();
        Vector3 stockMax = -stockMin;
        if (min.x < stockMin.x - envelopeTolerance ||
            min.y < stockMin.y - envelopeTolerance ||
            min.z < stockMin.z - envelopeTolerance ||
            max.x > stockMax.x + envelopeTolerance ||
            max.y > stockMax.y + envelopeTolerance ||
            max.z > stockMax.z + envelopeTolerance)
        {
            reason =
                $"bounds {min}..{max} exceed the user-defined stock envelope {stockMin}..{stockMax}.";
            return false;
        }

        reason = null;
        return true;
    }

    private bool TryExtractImportedBlankMesh(
        GameObject sourceRoot,
        out float[] vertices,
        out int vertexCount,
        out int[] triangleIndices,
        out int indexCount)
    {
        vertices = null;
        triangleIndices = null;
        vertexCount = 0;
        indexCount = 0;

        if (sourceRoot == null)
        {
            return false;
        }

        List<Vector3> vertexList = new List<Vector3>(4096);
        List<int> indexList = new List<int>(8192);

        MeshFilter[] filters = sourceRoot.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            AccumulateImportedBlankMesh(sourceRoot.transform, filters[i].transform, filters[i].sharedMesh, vertexList, indexList);
        }

        SkinnedMeshRenderer[] skinnedRenderers = sourceRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinnedRenderers.Length; i++)
        {
            AccumulateImportedBlankMesh(
                sourceRoot.transform,
                skinnedRenderers[i].transform,
                skinnedRenderers[i].sharedMesh,
                vertexList,
                indexList);
        }

        if (vertexList.Count <= 0 || indexList.Count < 3)
        {
            return false;
        }

        CenterAndScaleImportedBlankVertices(vertexList);
        vertexCount = vertexList.Count;
        indexCount = indexList.Count;
        vertices = new float[vertexCount * 3];
        for (int i = 0; i < vertexList.Count; i++)
        {
            Vector3 vertex = vertexList[i];
            int offset = i * 3;
            vertices[offset] = vertex.x;
            vertices[offset + 1] = vertex.y;
            vertices[offset + 2] = vertex.z;
        }

        triangleIndices = indexList.ToArray();
        return true;
    }

    private void AccumulateImportedBlankMesh(
        Transform rootTransform,
        Transform meshTransform,
        Mesh mesh,
        List<Vector3> vertices,
        List<int> triangleIndices)
    {
        if (rootTransform == null || meshTransform == null || mesh == null || !mesh.isReadable)
        {
            return;
        }

        Vector3[] meshVertices = mesh.vertices;
        int baseIndex = vertices.Count;
        for (int i = 0; i < meshVertices.Length; i++)
        {
            vertices.Add(rootTransform.InverseTransformPoint(meshTransform.TransformPoint(meshVertices[i])));
        }

        for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
        {
            if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
            {
                continue;
            }

            int[] indices = mesh.GetIndices(subMesh);
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = indices[i];
                int b = indices[i + 1];
                int c = indices[i + 2];
                if (a < 0 || b < 0 || c < 0 ||
                    a >= meshVertices.Length || b >= meshVertices.Length || c >= meshVertices.Length)
                {
                    continue;
                }

                triangleIndices.Add(baseIndex + a);
                triangleIndices.Add(baseIndex + b);
                triangleIndices.Add(baseIndex + c);
            }
        }
    }

    private void CenterAndScaleImportedBlankVertices(List<Vector3> vertices)
    {
        if (vertices == null || vertices.Count == 0)
        {
            return;
        }

        Vector3 min = vertices[0];
        Vector3 max = vertices[0];
        for (int i = 1; i < vertices.Count; i++)
        {
            min = Vector3.Min(min, vertices[i]);
            max = Vector3.Max(max, vertices[i]);
        }

        Vector3 center = (min + max) * 0.5f;
        Vector3 scale = new Vector3(
            Mathf.Clamp(importedBlankScalePercent.x, 0.001f, 1000f),
            Mathf.Clamp(importedBlankScalePercent.y, 0.001f, 1000f),
            Mathf.Clamp(importedBlankScalePercent.z, 0.001f, 1000f)) * 0.01f;
        for (int i = 0; i < vertices.Count; i++)
        {
            vertices[i] = Vector3.Scale(vertices[i] - center, scale);
        }
    }

    private void UploadCutterAngularProfileBuffers()
    {
        int sampleCount = Mathf.Max(1, MaxCutterAngularProfileSamples);
        if (cutterAngularProfileMinBuffer == null || cutterAngularProfileMinBuffer.count != sampleCount)
        {
            ReleaseCutterAngularProfileBuffers();
            cutterAngularProfileMinBuffer = new ComputeBuffer(sampleCount, sizeof(float));
            cutterAngularProfileMaxBuffer = new ComputeBuffer(sampleCount, sizeof(float));
        }

        cutterAngularProfileMinBuffer.SetData(cutterAngularProfileMinRadiusSamples);
        cutterAngularProfileMaxBuffer.SetData(cutterAngularProfileMaxRadiusSamples);
    }

    private bool EnsureCutterAngularProfileBuffers()
    {
        if (!HasAngularCutterProfile)
        {
            return false;
        }

        if (cutterAngularProfileMinBuffer == null ||
            cutterAngularProfileMaxBuffer == null ||
            cutterAngularProfileMinBuffer.count != MaxCutterAngularProfileSamples ||
            cutterAngularProfileMaxBuffer.count != MaxCutterAngularProfileSamples)
        {
            UploadCutterAngularProfileBuffers();
        }

        return cutterAngularProfileMinBuffer != null && cutterAngularProfileMaxBuffer != null;
    }

    private void ReleaseCutterAngularProfileBuffers()
    {
        if (cutterAngularProfileMinBuffer != null)
        {
            cutterAngularProfileMinBuffer.Release();
            cutterAngularProfileMinBuffer = null;
        }

        if (cutterAngularProfileMaxBuffer != null)
        {
            cutterAngularProfileMaxBuffer.Release();
            cutterAngularProfileMaxBuffer = null;
        }
    }

    private void EnforceSdfAllocationBudget()
    {
        long sampleCount = SdfSampleCountLong;
        int safeMaxSamples = Mathf.Max(100000, maxAllocatedSdfSamples);
        if (sampleCount <= safeMaxSamples)
        {
            return;
        }

        Vector3 currentSize = LocalSize;
        float relaxation = Mathf.Pow(sampleCount / (float)safeMaxSamples, 1f / 3f);
        float adjustedVoxelSize = Mathf.Max(0.001f, voxelSize * relaxation);

        width = Mathf.Max(1, Mathf.CeilToInt(currentSize.x / adjustedVoxelSize));
        height = Mathf.Max(1, Mathf.CeilToInt(currentSize.y / adjustedVoxelSize));
        depth = Mathf.Max(1, Mathf.CeilToInt(currentSize.z / adjustedVoxelSize));
        voxelSize = adjustedVoxelSize;

        Debug.LogWarning(
            $"Dense SDF allocation exceeded budget ({sampleCount:n0} samples). " +
            $"Adjusted voxelSize to {voxelSize:0.####} and grid to {width}x{height}x{depth}. " +
            "Use sparse bricks/local windows for 0.01mm over long travel.",
            this);
    }

    public void ResetWorkpiece()
    {
        EnsureComponents();
        CancelAsyncRebuild();
        EnforceSdfAllocationBudget();
        sdfGpuUnavailable = false;
        gpuSurfaceUnavailable = false;
        ReleaseGpuSurfaceResources();
        ReleaseGpuSdfResources();
        gpuVisualCutOperations.Clear();
        gpuVisualCutsDirty = true;
        if (surfaceMode == WorkpieceSurfaceMode.BlockyVoxels)
        {
            InitializeBlockVoxels();
            sdfSamples = null;
        }
        else
        {
            voxels = null;
            InitializeSdfSamples();
        }
        InitializeNativeSdfPlugin();
        InitializeGpuSdfResources();

        if (UsesGpuSurfaceRendering)
        {
            DestroyChunks();
            ClearParentMesh();
            EnsureGpuSurfaceResources();
            RebuildGpuSurface();
        }
        else if (UsesChunkedSmoothMesh)
        {
            EnsureChunks();
            ClearParentMesh();
            if (Application.isPlaying && asyncSmoothMeshRebuild)
            {
                QueueAllChunks();
                RebuildQueuedChunksBudgetImmediate(chunkRebuildsPerFrame);
                if (chunkRebuildCoroutine == null && dirtyChunkQueue.Count > 0)
                {
                    chunkRebuildCoroutine = StartCoroutine(RebuildDirtyChunksCoroutine());
                }
            }
            else
            {
                RebuildAllChunksImmediate();
            }
        }
        else
        {
            DestroyChunks();
            if (Application.isPlaying &&
                asyncSmoothMeshRebuild &&
                surfaceMode == WorkpieceSurfaceMode.SmoothSdf &&
                SdfSampleCountLong > smoothRebuildCellsPerFrame * 8L)
            {
                ClearParentMesh();
                rebuildCoroutine = StartCoroutine(RebuildSmoothMeshCoroutine());
            }
            else
            {
                RebuildMeshImmediate();
            }
        }
    }

    public bool CutSweptProfileCutter(Vector3 worldStart, Vector3 worldEnd, Vector3 worldAxis, float worldRadius, float worldHeight, bool rebuildMesh)
    {
        int changedSampleCount;
        return CutSweptProfileCutter(
            worldStart,
            worldEnd,
            worldAxis,
            Vector3.zero,
            worldRadius,
            worldHeight,
            rebuildMesh,
            out changedSampleCount);
    }

    public bool CutSphere(Vector3 worldCenter, float worldRadius, bool rebuildMesh)
    {
        return CutSweptSphere(worldCenter, worldCenter, worldRadius, rebuildMesh);
    }

    public bool CutSweptSphere(Vector3 worldStart, Vector3 worldEnd, float worldRadius, bool rebuildMesh)
    {
        int changedSampleCount;
        return CutSweptSphere(worldStart, worldEnd, worldRadius, rebuildMesh, out changedSampleCount);
    }

    public bool CutSweptSphere(
        Vector3 worldStart,
        Vector3 worldEnd,
        float worldRadius,
        bool rebuildMesh,
        out int changedSampleCount)
    {
        changedSampleCount = 0;

        if (worldRadius <= 0f)
        {
            return false;
        }

        if (!IsInitialized)
        {
            ResetWorkpiece();
        }

        bool changed = surfaceMode == WorkpieceSurfaceMode.SmoothSdf
            ? CutNativeCapsuleSdf(worldStart, worldEnd, worldRadius, out changedSampleCount)
            : false;

        if (!changed)
        {
            return false;
        }

        if (rebuildMesh)
        {
            RebuildMesh();
        }

        return true;
    }

    public bool CutSweptProfileCutter(Vector3 worldStart, Vector3 worldEnd, Vector3 worldAxis, float worldRadius, float worldHeight, bool rebuildMesh, out int changedSampleCount)
    {
        return CutSweptProfileCutter(
            worldStart,
            worldEnd,
            worldAxis,
            Vector3.zero,
            worldRadius,
            worldHeight,
            rebuildMesh,
            out changedSampleCount);
    }

    public bool CutSweptProfileCutter(
        Vector3 worldStart,
        Vector3 worldEnd,
        Vector3 worldAxis,
        Vector3 worldRight,
        float worldRadius,
        float worldHeight,
        bool rebuildMesh)
    {
        int changedSampleCount;
        return CutSweptProfileCutter(
            worldStart,
            worldEnd,
            worldAxis,
            worldRight,
            worldRadius,
            worldHeight,
            rebuildMesh,
            out changedSampleCount);
    }

    public bool CutSweptProfileCutter(
        Vector3 worldStart,
        Vector3 worldEnd,
        Vector3 worldAxis,
        Vector3 worldRight,
        float worldRadius,
        float worldHeight,
        bool rebuildMesh,
        out int changedSampleCount)
    {
        changedSampleCount = 0;

        if (worldRadius <= 0f || worldHeight <= 0f || worldAxis.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        if (!IsInitialized)
        {
            ResetWorkpiece();
        }

        bool changed = surfaceMode == WorkpieceSurfaceMode.SmoothSdf
            ? CutSelectedNativeCutterSdf(
                worldStart,
                worldEnd,
                worldAxis,
                worldRight,
                worldRadius,
                worldHeight,
                out changedSampleCount)
            : false;

        if (!changed)
        {
            return false;
        }

        if (rebuildMesh)
        {
            RebuildMesh();
        }

        return true;
    }

    public bool IsSolid(int x, int y, int z)
    {
        if (surfaceMode == WorkpieceSurfaceMode.SmoothSdf && sdfSamples != null)
        {
            if (!IsInside(x, y, z))
            {
                return false;
            }

            Vector3 localCenter = GetVoxelCenterLocal(x, y, z, GetLocalMin());
            return SampleSdf(localCenter) <= IsoLevel;
        }

        if (voxels == null || !IsInside(x, y, z))
        {
            return false;
        }

        return voxels[x, y, z];
    }

    public void RebuildMesh()
    {
        CancelAsyncRebuild();

        if (UsesGpuSurfaceRendering)
        {
            RebuildGpuSurface();
            return;
        }

        if (UsesChunkedSmoothMesh)
        {
            EnsureChunks();
            RebuildQueuedChunksImmediate();
            ClearParentMesh();
            return;
        }

        DestroyChunks();
        RebuildMeshImmediate();
    }

    public void ClearDisplayOverlays()
    {
        ClearGpuVisualCuts();
        CancelNativeCutDetailRebuild();
        ClearNativeCutDetailDisplay(false);
    }

    public void RequestRebuildMesh()
    {
        if (!IsInitialized)
        {
            return;
        }

        if (UsesGpuSurfaceRendering)
        {
            RebuildGpuSurface();
            return;
        }

        if (UsesChunkedSmoothMesh)
        {
            EnsureChunks();
            ClearParentMesh();

            if (!Application.isPlaying || !asyncSmoothMeshRebuild)
            {
                RebuildQueuedChunksImmediate();
                return;
            }

            if (chunkRebuildCoroutine == null && dirtyChunkQueue.Count > 0)
            {
                chunkRebuildCoroutine = StartCoroutine(RebuildDirtyChunksCoroutine());
            }

            return;
        }

        DestroyChunks();
        if (!Application.isPlaying || !asyncSmoothMeshRebuild || surfaceMode != WorkpieceSurfaceMode.SmoothSdf)
        {
            RebuildMesh();
            return;
        }

        if (rebuildCoroutine != null)
        {
            rebuildRequestedWhileRunning = true;
            return;
        }

        rebuildCoroutine = StartCoroutine(RebuildSmoothMeshCoroutine());
    }

    [ContextMenu("Cleanup Detached Material Now")]
    public void CleanupDetachedMaterialNow()
    {
        if (!IsInitialized)
        {
            ResetWorkpiece();
        }

        if (surfaceMode != WorkpieceSurfaceMode.SmoothSdf || !sdfNativeReady)
        {
            return;
        }

        SdfNativePlugin.sdf_check_connectivity();
        int timeout = 500;
        int removed = SdfNativePlugin.sdf_try_apply_connectivity_cleanup();
        while (removed < 0 && timeout > 0)
        {
            System.Threading.Thread.Sleep(1);
            removed = SdfNativePlugin.sdf_try_apply_connectivity_cleanup();
            timeout--;
        }

        if (removed < 0)
        {
            return;
        }

        int componentCount = SdfNativePlugin.sdf_get_last_connectivity_component_count();
        int keepCoreCount = SdfNativePlugin.sdf_get_last_connectivity_keep_core_count();
        int removalCandidateCount = SdfNativePlugin.sdf_get_last_connectivity_removal_candidate_count();
        if (removed > 0)
        {
            ClearGpuVisualCuts();
            ClearNativeCutDetailDisplay(false);
            UploadSdfFromNative();
            RequestRebuildMesh();
        }

        Debug.Log(
            $"DETACHED_CLEANUP_MANUAL_RESULT components={componentCount} " +
            $"keepCore={keepCoreCount} removalCandidates={removalCandidateCount} removed={removed} " +
            NativeConnectivitySummary("manual-cleanup"),
            this);

        nativeConnectivityInFlight = false;
        nativeConnectivityDirty = removed > 0;
        nextNativeConnectivityCheckTime = nativeConnectivityDirty && Application.isPlaying
            ? Time.time + Mathf.Max(0.01f, detachedCleanupInterval)
            : 0f;
    }

    [ContextMenu("Log Native Material Diagnostics")]
    public void LogNativeMaterialDiagnostics()
    {
        if (!IsInitialized)
        {
            ResetWorkpiece();
        }

        Debug.Log(NativeConnectivitySummary("manual-diagnostic"), this);
    }

    private string NativeConnectivitySummary(string label)
    {
        if (!sdfNativeReady)
        {
            return $"NATIVE_MATERIAL_DIAGNOSTIC label={label} plugin={SdfNativePlugin.PluginName} ready=false";
        }

        try
        {
            int ok = SdfNativePlugin.sdf_debug_connectivity_summary(
                out int solidSamples,
                out int coreSolidSamples,
                out int components,
                out int keepCore,
                out int removalCandidates);
            return ok != 0
                ? $"NATIVE_MATERIAL_DIAGNOSTIC label={label} plugin={SdfNativePlugin.PluginName} " +
                  $"solid={solidSamples} coreSolid={coreSolidSamples} components={components} " +
                  $"keepCore={keepCore} removalCandidates={removalCandidates}"
                : $"NATIVE_MATERIAL_DIAGNOSTIC label={label} plugin={SdfNativePlugin.PluginName} ready=false";
        }
        catch (System.Exception ex)
        {
            return $"NATIVE_MATERIAL_DIAGNOSTIC label={label} plugin={SdfNativePlugin.PluginName} error={ex.Message}";
        }
    }

    private void RebuildMeshImmediate()
    {
        EnsureComponents();

        if (!IsInitialized)
        {
            return;
        }

        vertices.Clear();
        triangles.Clear();
        normals.Clear();
        uvs.Clear();

        if (surfaceMode == WorkpieceSurfaceMode.SmoothSdf)
        {
            BuildSmoothSdfMesh();
        }
        else
        {
            BuildBlockyMesh();
        }

        ApplyMeshData();
    }

    private IEnumerator RebuildSmoothMeshCoroutine()
    {
        do
        {
            rebuildRequestedWhileRunning = false;

            vertices.Clear();
            triangles.Clear();
            normals.Clear();
            uvs.Clear();

            yield return BuildSmoothSdfMeshCoroutine();
            ApplyMeshData();
        }
        while (rebuildRequestedWhileRunning);

        rebuildCoroutine = null;
    }

    private void ApplyMeshData()
    {
        runtimeMesh.Clear(false);
        runtimeMesh.indexFormat = IndexFormat.UInt32;
        runtimeMesh.SetVertices(vertices);
        runtimeMesh.SetTriangles(triangles, 0);
        runtimeMesh.SetNormals(normals);
        runtimeMesh.SetUVs(0, uvs);
        runtimeMesh.RecalculateBounds();

        meshFilter.sharedMesh = runtimeMesh;
        if (meshRenderer != null && !UsesGpuSurfaceRendering && !UsesChunkedSmoothMesh)
        {
            meshRenderer.enabled = true;
        }

        if (updateCollider)
        {
            if (meshCollider == null)
            {
                meshCollider = GetComponent<MeshCollider>();
                if (meshCollider == null)
                {
                    meshCollider = gameObject.AddComponent<MeshCollider>();
                }
            }

            meshCollider.sharedMesh = null;
            meshCollider.sharedMesh = runtimeMesh;
        }
        else if (meshCollider != null)
        {
            meshCollider.sharedMesh = null;
        }
    }

    private void CancelAsyncRebuild()
    {
        if (rebuildCoroutine != null)
        {
            StopCoroutine(rebuildCoroutine);
            rebuildCoroutine = null;
        }

        if (chunkRebuildCoroutine != null)
        {
            StopCoroutine(chunkRebuildCoroutine);
            chunkRebuildCoroutine = null;
        }

        rebuildRequestedWhileRunning = false;
    }

    private void EnsureChunks()
    {
        EnsureComponents();

        bool chunksValid = chunks != null
            && cachedChunkSize == chunkSize
            && cachedCellWidth == CellWidth
            && cachedCellHeight == CellHeight
            && cachedCellDepth == CellDepth;

        if (chunksValid)
        {
            SyncChunkMaterials();
            return;
        }

        DestroyChunks();

        cachedChunkSize = chunkSize;
        cachedCellWidth = CellWidth;
        cachedCellHeight = CellHeight;
        cachedCellDepth = CellDepth;

        chunkCountX = Mathf.CeilToInt(CellWidth / (float)chunkSize);
        chunkCountY = Mathf.CeilToInt(CellHeight / (float)chunkSize);
        chunkCountZ = Mathf.CeilToInt(CellDepth / (float)chunkSize);

        int totalChunks = chunkCountX * chunkCountY * chunkCountZ;
        chunks = new VoxelChunk[totalChunks];
        chunkQueued = new bool[totalChunks];
        immediateChunkMarks = new int[totalChunks];

        for (int cz = 0; cz < chunkCountZ; cz++)
        {
            for (int cy = 0; cy < chunkCountY; cy++)
            {
                for (int cx = 0; cx < chunkCountX; cx++)
                {
                    int index = GetChunkIndex(cx, cy, cz);
                    GameObject chunkObject = new GameObject("Smooth Chunk " + cx + " " + cy + " " + cz);
                    chunkObject.transform.SetParent(transform, false);

                    MeshFilter chunkFilter = chunkObject.AddComponent<MeshFilter>();
                    MeshRenderer chunkRenderer = chunkObject.AddComponent<MeshRenderer>();
                    chunkRenderer.sharedMaterial = meshRenderer != null ? meshRenderer.sharedMaterial : null;

                    Mesh chunkMesh = new Mesh
                    {
                        name = "Runtime Smooth Chunk " + index,
                        indexFormat = IndexFormat.UInt32
                    };
                    chunkMesh.MarkDynamic();
                    chunkFilter.sharedMesh = chunkMesh;

                    chunks[index] = new VoxelChunk
                    {
                        index = index,
                        chunkX = cx,
                        chunkY = cy,
                        chunkZ = cz,
                        startX = cx * chunkSize,
                        endX = Mathf.Min((cx + 1) * chunkSize, CellWidth),
                        startY = cy * chunkSize,
                        endY = Mathf.Min((cy + 1) * chunkSize, CellHeight),
                        startZ = cz * chunkSize,
                        endZ = Mathf.Min((cz + 1) * chunkSize, CellDepth),
                        mesh = chunkMesh,
                        meshFilter = chunkFilter,
                        meshRenderer = chunkRenderer,
                        gameObject = chunkObject
                    };
                }
            }
        }
    }

    private void RebuildAllChunksImmediate()
    {
        EnsureChunks();
        dirtyChunkQueue.Clear();

        if (chunkQueued != null)
        {
            for (int i = 0; i < chunkQueued.Length; i++)
            {
                chunkQueued[i] = false;
            }
        }

        for (int i = 0; i < chunks.Length; i++)
        {
            BuildChunkMesh(chunks[i]);
        }
    }

    private void QueueAllChunks()
    {
        EnsureChunks();
        dirtyChunkQueue.Clear();

        if (chunkQueued == null)
        {
            return;
        }

        for (int i = 0; i < chunkQueued.Length; i++)
        {
            chunkQueued[i] = true;
            dirtyChunkQueue.Enqueue(i);
        }
    }

    private void RebuildQueuedChunksImmediate()
    {
        EnsureChunks();

        while (dirtyChunkQueue.Count > 0)
        {
            int index = dirtyChunkQueue.Dequeue();
            if (!chunkQueued[index])
            {
                continue;
            }

            BuildChunkMesh(chunks[index]);
            chunkQueued[index] = false;
        }
    }

    private int RebuildQueuedChunksBudgetImmediate(int budget)
    {
        EnsureChunks();

        int rebuilt = 0;
        int safety = dirtyChunkQueue.Count;
        while (dirtyChunkQueue.Count > 0 && rebuilt < budget && safety > 0)
        {
            safety--;

            int index = dirtyChunkQueue.Dequeue();
            if (!chunkQueued[index])
            {
                continue;
            }

            BuildChunkMesh(chunks[index]);
            chunkQueued[index] = false;
            rebuilt++;
        }

        return rebuilt;
    }

    private IEnumerator RebuildDirtyChunksCoroutine()
    {
        while (dirtyChunkQueue.Count > 0)
        {
            int rebuiltThisFrame = 0;
            while (dirtyChunkQueue.Count > 0 && rebuiltThisFrame < chunkRebuildsPerFrame)
            {
                int index = dirtyChunkQueue.Dequeue();
                if (!chunkQueued[index])
                {
                    continue;
                }

                BuildChunkMesh(chunks[index]);
                chunkQueued[index] = false;
                rebuiltThisFrame++;
            }

            yield return null;
        }

        chunkRebuildCoroutine = null;
    }

    private void BuildChunkMesh(VoxelChunk chunk)
    {
        vertices.Clear();
        triangles.Clear();
        normals.Clear();
        uvs.Clear();

        BuildSmoothSdfMeshRange(chunk.startX, chunk.endX, chunk.startY, chunk.endY, chunk.startZ, chunk.endZ);

        chunk.mesh.Clear(false);
        chunk.mesh.indexFormat = IndexFormat.UInt32;
        chunk.mesh.SetVertices(vertices);
        chunk.mesh.SetTriangles(triangles, 0);
        chunk.mesh.SetNormals(normals);
        chunk.mesh.SetUVs(0, uvs);
        chunk.mesh.RecalculateBounds();
        chunk.meshFilter.sharedMesh = chunk.mesh;

        if (chunk.meshRenderer != null && meshRenderer != null)
        {
            chunk.meshRenderer.sharedMaterial = meshRenderer.sharedMaterial;
        }

        if (updateCollider)
        {
            if (chunk.meshCollider == null)
            {
                chunk.meshCollider = chunk.gameObject.GetComponent<MeshCollider>();
                if (chunk.meshCollider == null)
                {
                    chunk.meshCollider = chunk.gameObject.AddComponent<MeshCollider>();
                }
            }

            chunk.meshCollider.sharedMesh = null;
            chunk.meshCollider.sharedMesh = chunk.mesh;
        }
        else if (chunk.meshCollider != null)
        {
            chunk.meshCollider.sharedMesh = null;
        }
    }

    private void MarkDirtyCells(int minX, int maxXExclusive, int minY, int maxYExclusive, int minZ, int maxZExclusive, Vector3 priorityLocalPoint)
    {
        EnsureChunks();

        int minChunkX = Mathf.Clamp(minX / chunkSize, 0, chunkCountX - 1);
        int maxChunkX = Mathf.Clamp((Mathf.Max(minX, maxXExclusive - 1)) / chunkSize, 0, chunkCountX - 1);
        int minChunkY = Mathf.Clamp(minY / chunkSize, 0, chunkCountY - 1);
        int maxChunkY = Mathf.Clamp((Mathf.Max(minY, maxYExclusive - 1)) / chunkSize, 0, chunkCountY - 1);
        int minChunkZ = Mathf.Clamp(minZ / chunkSize, 0, chunkCountZ - 1);
        int maxChunkZ = Mathf.Clamp((Mathf.Max(minZ, maxZExclusive - 1)) / chunkSize, 0, chunkCountZ - 1);

        int rebuiltImmediately = rebuildCoreChunksImmediately
            ? RebuildChunksNearPointImmediately(priorityLocalPoint, minChunkX, maxChunkX, minChunkY, maxChunkY, minChunkZ, maxChunkZ)
            : 0;

        for (int z = minChunkZ; z <= maxChunkZ; z++)
        {
            for (int y = minChunkY; y <= maxChunkY; y++)
            {
                for (int x = minChunkX; x <= maxChunkX; x++)
                {
                    int index = GetChunkIndex(x, y, z);
                    VoxelChunk chunk = chunks[index];

                    if (rebuildCoreChunksImmediately && IsChunkMarkedImmediate(index))
                    {
                        QueueDirtyChunkNeighborhood(chunk, false);
                        continue;
                    }

                    if (rebuildCoreChunksImmediately && rebuiltImmediately < immediateChunkRebuildLimit && !chunkQueued[index])
                    {
                        BuildChunkMesh(chunk);
                        MarkChunkImmediate(index);
                        rebuiltImmediately++;
                    }
                    else
                    {
                        QueueDirtyChunk(index);
                    }

                    QueueDirtyChunkNeighborhood(chunk, false);
                }
            }
        }

        if (Application.isPlaying && asyncSmoothMeshRebuild)
        {
            RebuildQueuedChunksBudgetImmediate(chunkRebuildsPerFrame);
            if (chunkRebuildCoroutine == null && dirtyChunkQueue.Count > 0)
            {
                chunkRebuildCoroutine = StartCoroutine(RebuildDirtyChunksCoroutine());
            }
        }
    }

    private int RebuildChunksNearPointImmediately(Vector3 localPoint, int minChunkX, int maxChunkX, int minChunkY, int maxChunkY, int minChunkZ, int maxChunkZ)
    {
        BeginImmediateChunkMarkPass();

        Vector3 grid = (localPoint - GetLocalMin()) / voxelSize;
        int centerChunkX = Mathf.Clamp(Mathf.FloorToInt(grid.x / chunkSize), minChunkX, maxChunkX);
        int centerChunkY = Mathf.Clamp(Mathf.FloorToInt(grid.y / chunkSize), minChunkY, maxChunkY);
        int centerChunkZ = Mathf.Clamp(Mathf.FloorToInt(grid.z / chunkSize), minChunkZ, maxChunkZ);

        int rebuilt = 0;
        int maxShell = Mathf.Max(maxChunkX - minChunkX, Mathf.Max(maxChunkY - minChunkY, maxChunkZ - minChunkZ));

        for (int shell = 0; shell <= maxShell && rebuilt < immediateChunkRebuildLimit; shell++)
        {
            for (int z = centerChunkZ - shell; z <= centerChunkZ + shell && rebuilt < immediateChunkRebuildLimit; z++)
            {
                if (z < minChunkZ || z > maxChunkZ)
                {
                    continue;
                }

                for (int y = centerChunkY - shell; y <= centerChunkY + shell && rebuilt < immediateChunkRebuildLimit; y++)
                {
                    if (y < minChunkY || y > maxChunkY)
                    {
                        continue;
                    }

                    for (int x = centerChunkX - shell; x <= centerChunkX + shell && rebuilt < immediateChunkRebuildLimit; x++)
                    {
                        if (x < minChunkX || x > maxChunkX)
                        {
                            continue;
                        }

                        if (Mathf.Max(Mathf.Abs(x - centerChunkX), Mathf.Abs(y - centerChunkY), Mathf.Abs(z - centerChunkZ)) != shell)
                        {
                            continue;
                        }

                        int index = GetChunkIndex(x, y, z);
                        BuildChunkMesh(chunks[index]);
                        if (chunkQueued != null)
                        {
                            chunkQueued[index] = false;
                        }

                        MarkChunkImmediate(index);
                        rebuilt++;
                    }
                }
            }
        }

        return rebuilt;
    }

    private void BeginImmediateChunkMarkPass()
    {
        if (immediateChunkMarks == null)
        {
            return;
        }

        immediateChunkMarkVersion++;
        if (immediateChunkMarkVersion == int.MaxValue)
        {
            immediateChunkMarkVersion = 1;
            for (int i = 0; i < immediateChunkMarks.Length; i++)
            {
                immediateChunkMarks[i] = 0;
            }
        }
    }

    private void MarkChunkImmediate(int index)
    {
        if (immediateChunkMarks == null || index < 0 || index >= immediateChunkMarks.Length)
        {
            return;
        }

        immediateChunkMarks[index] = immediateChunkMarkVersion;
    }

    private bool IsChunkMarkedImmediate(int index)
    {
        return immediateChunkMarks != null
            && index >= 0
            && index < immediateChunkMarks.Length
            && immediateChunkMarks[index] == immediateChunkMarkVersion;
    }

    private void QueueDirtyChunkNeighborhood(VoxelChunk centerChunk, bool includeCenter)
    {
        int shell = Mathf.Max(0, dirtyChunkNeighborShell);

        for (int z = centerChunk.chunkZ - shell; z <= centerChunk.chunkZ + shell; z++)
        {
            if (z < 0 || z >= chunkCountZ)
            {
                continue;
            }

            for (int y = centerChunk.chunkY - shell; y <= centerChunk.chunkY + shell; y++)
            {
                if (y < 0 || y >= chunkCountY)
                {
                    continue;
                }

                for (int x = centerChunk.chunkX - shell; x <= centerChunk.chunkX + shell; x++)
                {
                    if (x < 0 || x >= chunkCountX)
                    {
                        continue;
                    }

                    if (!includeCenter && x == centerChunk.chunkX && y == centerChunk.chunkY && z == centerChunk.chunkZ)
                    {
                        continue;
                    }

                    QueueDirtyChunk(GetChunkIndex(x, y, z));
                }
            }
        }
    }

    private void QueueDirtyChunk(int index)
    {
        if (chunkQueued == null || index < 0 || index >= chunkQueued.Length || chunkQueued[index])
        {
            return;
        }

        chunkQueued[index] = true;
        dirtyChunkQueue.Enqueue(index);
    }

    private void ClearParentMesh()
    {
        EnsureComponents();

        if (runtimeMesh != null)
        {
            runtimeMesh.Clear(false);
            meshFilter.sharedMesh = runtimeMesh;
        }

        if (meshRenderer != null)
        {
            meshRenderer.enabled = false;
        }

        if (meshCollider != null)
        {
            meshCollider.sharedMesh = null;
        }
    }

    private void SyncChunkMaterials()
    {
        if (chunks == null || meshRenderer == null)
        {
            return;
        }

        for (int i = 0; i < chunks.Length; i++)
        {
            if (chunks[i].meshRenderer != null)
            {
                chunks[i].meshRenderer.sharedMaterial = meshRenderer.sharedMaterial;
            }
        }
    }

    private void DestroyChunks()
    {
        if (chunkRebuildCoroutine != null)
        {
            StopCoroutine(chunkRebuildCoroutine);
            chunkRebuildCoroutine = null;
        }

        dirtyChunkQueue.Clear();
        chunkQueued = null;
        immediateChunkMarks = null;
        immediateChunkMarkVersion = 0;

        if (chunks != null)
        {
            for (int i = 0; i < chunks.Length; i++)
            {
                if (chunks[i] == null)
                {
                    continue;
                }

                DestroyRuntimeObject(chunks[i].mesh);
                DestroyRuntimeObject(chunks[i].gameObject);
            }
        }

        chunks = null;
        chunkCountX = 0;
        chunkCountY = 0;
        chunkCountZ = 0;
        cachedChunkSize = 0;
        cachedCellWidth = 0;
        cachedCellHeight = 0;
        cachedCellDepth = 0;
    }

    private int GetChunkIndex(int x, int y, int z)
    {
        return x + chunkCountX * (y + chunkCountY * z);
    }

    private static void DestroyRuntimeObject(Object target)
    {
        if (target == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(target);
        }
        else
        {
            DestroyImmediate(target);
        }
    }

    private void InitializeBlockVoxels()
    {
        voxels = new bool[width, height, depth];

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    voxels[x, y, z] = true;
                }
            }
        }
    }

    private void InitializeSdfSamples()
    {
        sdfSamples = new float[SampleWidth, SampleHeight, SampleDepth];
    }

    private void InitializeGpuSdfResources()
    {
        if (!UsesGpuSdfBuffer || surfaceMode != WorkpieceSurfaceMode.SmoothSdf)
        {
            return;
        }

        EnsureGpuSdfResources();
    }

    private bool EnsureGpuSdfResources()
    {
        if (sdfGpuUnavailable || !UsesGpuSdfBuffer || surfaceMode != WorkpieceSurfaceMode.SmoothSdf)
        {
            return false;
        }

        if (sdfSamples == null)
        {
            return false;
        }

        if (!SystemInfo.supportsComputeShaders)
        {
            return false;
        }

        ComputeShader shader = GetSdfDisplayBufferCompute();
        if (shader == null)
        {
            return false;
        }

        int sampleCount = SdfSampleCount;
        if (sdfGpuReady && sdfSampleBuffer != null && sdfSampleBuffer.count == sampleCount)
        {
            return true;
        }

        ReleaseGpuSdfResources();

        try
        {
            sdfDisplayBufferCompute = shader;
            sdfCopyRegionKernel = sdfDisplayBufferCompute.FindKernel("CopySdfRegion");

            sdfSampleBuffer = new ComputeBuffer(sampleCount, sizeof(float));
            EnsureSdfLinearSamples();
            CopySdfSamplesToLinear();
            sdfSampleBuffer.SetData(sdfLinearSamples);

            sdfGpuReady = true;
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"GPU SDF display buffer disabled: {exception.Message}", this);
            sdfGpuUnavailable = true;
            ReleaseGpuSdfResources();
            return false;
        }
    }

    private ComputeShader GetSdfDisplayBufferCompute()
    {
        if (sdfDisplayBufferCompute != null)
        {
            return sdfDisplayBufferCompute;
        }

        sdfDisplayBufferCompute = Resources.Load<ComputeShader>("Cutting/WorkpieceSdfDisplayBuffer");
        return sdfDisplayBufferCompute;
    }

    private void ReleaseGpuSdfResources()
    {
        if (sdfSampleBuffer != null)
        {
            sdfSampleBuffer.Release();
            sdfSampleBuffer = null;
        }

        if (sdfRegionUploadBuffer != null)
        {
            sdfRegionUploadBuffer.Release();
            sdfRegionUploadBuffer = null;
        }

        sdfRegionUploadBufferCapacity = 0;
        sdfGpuReady = false;
        sdfCopyRegionKernel = -1;
    }

    private void UploadSdfSamplesToGpu()
    {
        if (!sdfGpuReady || sdfSampleBuffer == null || sdfSamples == null)
        {
            return;
        }

        EnsureSdfLinearSamples();
        CopySdfSamplesToLinear();
        sdfSampleBuffer.SetData(sdfLinearSamples);
    }

    private void EnsureSdfLinearSamples()
    {
        int sampleCount = SdfSampleCount;
        if (sdfLinearSamples == null || sdfLinearSamples.Length != sampleCount)
        {
            sdfLinearSamples = new float[sampleCount];
        }
    }

    private void EnsureSdfRegionSamples(int sampleCount)
    {
        if (sdfRegionSamples == null || sdfRegionSamples.Length < sampleCount)
        {
            sdfRegionSamples = new float[sampleCount];
        }
    }

    private bool EnsureSdfRegionUploadBuffer(int sampleCount)
    {
        if (!sdfGpuReady || sdfSampleBuffer == null || sdfCopyRegionKernel < 0 || sampleCount <= 0)
        {
            return false;
        }

        if (sdfRegionUploadBuffer != null && sdfRegionUploadBufferCapacity >= sampleCount)
        {
            return true;
        }

        if (sdfRegionUploadBuffer != null)
        {
            sdfRegionUploadBuffer.Release();
            sdfRegionUploadBuffer = null;
        }

        sdfRegionUploadBufferCapacity = Mathf.NextPowerOfTwo(Mathf.Max(1, sampleCount));
        sdfRegionUploadBuffer = new ComputeBuffer(sdfRegionUploadBufferCapacity, sizeof(float));
        return true;
    }

    private void CopySdfSamplesToLinear()
    {
        for (int z = 0; z < SampleDepth; z++)
        {
            for (int y = 0; y < SampleHeight; y++)
            {
                for (int x = 0; x < SampleWidth; x++)
                {
                    sdfLinearSamples[GetSdfSampleIndex(x, y, z)] = sdfSamples[x, y, z];
                }
            }
        }
    }

    private void CopyLinearToSdfSamples()
    {
        for (int z = 0; z < SampleDepth; z++)
        {
            for (int y = 0; y < SampleHeight; y++)
            {
                for (int x = 0; x < SampleWidth; x++)
                {
                    sdfSamples[x, y, z] = sdfLinearSamples[GetSdfSampleIndex(x, y, z)];
                }
            }
        }
    }

    private int GetSdfSampleIndex(int x, int y, int z)
    {
        return x + SampleWidth * (y + SampleHeight * z);
    }

    private bool UploadSdfRegionFromNative(int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        if (!sdfNativeReady)
        {
            return false;
        }

        minX = ClampIndex(minX, SampleWidth);
        maxX = ClampIndex(maxX, SampleWidth);
        minY = ClampIndex(minY, SampleHeight);
        maxY = ClampIndex(maxY, SampleHeight);
        minZ = ClampIndex(minZ, SampleDepth);
        maxZ = ClampIndex(maxZ, SampleDepth);

        if (maxX < minX || maxY < minY || maxZ < minZ)
        {
            return false;
        }

        int countX = maxX - minX + 1;
        int countY = maxY - minY + 1;
        int countZ = maxZ - minZ + 1;
        int sampleCount = countX * countY * countZ;
        if (sampleCount <= 0)
        {
            return false;
        }

        try
        {
            EnsureSdfRegionSamples(sampleCount);
            int written = SdfNativePlugin.sdf_get_region(
                sdfRegionSamples,
                sampleCount,
                minX,
                maxX,
                minY,
                maxY,
                minZ,
                maxZ);
            if (written != sampleCount)
            {
                Debug.LogWarning(
                    $"Native SDF region upload returned {written} samples, expected {sampleCount}.",
                    this);
                return false;
            }

            if (sdfSamples != null)
            {
                int src = 0;
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        for (int x = minX; x <= maxX; x++)
                        {
                            sdfSamples[x, y, z] = sdfRegionSamples[src];
                            src++;
                        }
                    }
                }
            }

            if (sdfSampleBuffer != null)
            {
                if (EnsureSdfRegionUploadBuffer(sampleCount))
                {
                    sdfRegionUploadBuffer.SetData(sdfRegionSamples, 0, 0, sampleCount);
                    sdfDisplayBufferCompute.SetBuffer(sdfCopyRegionKernel, "_SdfSamples", sdfSampleBuffer);
                    sdfDisplayBufferCompute.SetBuffer(sdfCopyRegionKernel, "_SdfRegionSamples", sdfRegionUploadBuffer);
                    sdfDisplayBufferCompute.SetInts("_SampleSize", SampleWidth, SampleHeight, SampleDepth);
                    sdfDisplayBufferCompute.SetInts("_RegionMin", minX, minY, minZ);
                    sdfDisplayBufferCompute.SetInts("_RegionSize", countX, countY, countZ);

                    int threadGroupsX = Mathf.CeilToInt(countX / 8f);
                    int threadGroupsY = Mathf.CeilToInt(countY / 8f);
                    int threadGroupsZ = Mathf.CeilToInt(countZ / 4f);
                    sdfDisplayBufferCompute.Dispatch(sdfCopyRegionKernel, threadGroupsX, threadGroupsY, threadGroupsZ);
                }
                else
                {
                    int src = 0;
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        for (int y = minY; y <= maxY; y++)
                        {
                            int dst = GetSdfSampleIndex(minX, y, z);
                            sdfSampleBuffer.SetData(sdfRegionSamples, src, dst, countX);
                            src += countX;
                        }
                    }
                }
            }

            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native SDF region upload failed: {ex.Message}", this);
            return false;
        }
    }

    private bool EnsureGpuSurfaceResources()
    {
        if (gpuSurfaceUnavailable || !UsesGpuSurfaceRendering || !EnsureGpuSdfResources())
        {
            return false;
        }

        if (gpuSurfaceReady &&
            gpuProxyMesh != null &&
            cachedGpuProxySize == DisplaySize &&
            cachedGpuProxyCenter == DisplayCenter)
        {
            EnsureGpuSurfaceMaterial();
            return runtimeGpuSurfaceMaterial != null;
        }

        ReleaseGpuSurfaceResources();

        EnsureGpuSurfaceMaterial();
        EnsureGpuProxyMesh();
        gpuSurfaceReady = runtimeGpuSurfaceMaterial != null && gpuProxyMesh != null;
        return gpuSurfaceReady;
    }

    private void EnsureGpuSurfaceMaterial()
    {
        if (runtimeGpuSurfaceMaterial != null)
        {
            return;
        }

        if (gpuSurfaceMaterial != null)
        {
            runtimeGpuSurfaceMaterial = gpuSurfaceMaterial;
            return;
        }

        Shader shader = Shader.Find("Hidden/Cutting/GpuSdfWorkpiece");
        if (shader == null)
        {
            shader = Resources.Load<Shader>("Cutting/GpuSdfWorkpiece");
        }

        if (shader != null)
        {
            runtimeGpuSurfaceMaterial = new Material(shader)
            {
                name = "Runtime GPU SDF Workpiece Material"
            };
        }
    }

    private void ReleaseGpuSurfaceResources()
    {
        CancelNativeCutDetailRebuild();

        if (runtimeGpuSurfaceMaterial != null && runtimeGpuSurfaceMaterial != gpuSurfaceMaterial)
        {
            DestroyRuntimeObject(runtimeGpuSurfaceMaterial);
        }

        if (gpuProxyMesh != null)
        {
            DestroyRuntimeObject(gpuProxyMesh);
        }

        runtimeGpuSurfaceMaterial = null;
        gpuProxyMesh = null;
        cachedGpuProxySize = Vector3.zero;
        cachedGpuProxyCenter = Vector3.zero;
        gpuSurfaceReady = false;

        if (gpuVisualCutBuffer != null)
        {
            gpuVisualCutBuffer.Release();
            gpuVisualCutBuffer = null;
        }

        if (nativeCutDetailBuffer != null)
        {
            nativeCutDetailBuffer.Release();
            nativeCutDetailBuffer = null;
        }

        if (nativeCutDetailTileBuffer != null)
        {
            nativeCutDetailTileBuffer.Release();
            nativeCutDetailTileBuffer = null;
        }

        gpuVisualCutBufferCapacity = 0;
        gpuVisualCutsDirty = true;
        nativeCutDetailBufferCapacity = 0;
        nativeCutDetailTileBufferCapacity = 0;
        nativeCutDetailTiles.Clear();
        nativeCutDetailReady = false;
    }

    private void RebuildGpuSurface()
    {
        EnsureGpuSurfaceResources();
    }

    private void EnsureGpuProxyMesh()
    {
        if (gpuProxyMesh != null && cachedGpuProxySize == DisplaySize && cachedGpuProxyCenter == DisplayCenter)
        {
            return;
        }

        if (gpuProxyMesh != null)
        {
            DestroyRuntimeObject(gpuProxyMesh);
        }

        gpuProxyMesh = BuildGpuProxyMesh(DisplaySize, DisplayCenter);
        cachedGpuProxySize = DisplaySize;
        cachedGpuProxyCenter = DisplayCenter;
    }

    private static Mesh BuildGpuProxyMesh(Vector3 size, Vector3 center)
    {
        Vector3 half = size * 0.5f;
        Vector3[] meshVertices =
        {
            center + new Vector3(-half.x, -half.y, -half.z),
            center + new Vector3(half.x, -half.y, -half.z),
            center + new Vector3(half.x, half.y, -half.z),
            center + new Vector3(-half.x, half.y, -half.z),
            center + new Vector3(-half.x, -half.y, half.z),
            center + new Vector3(half.x, -half.y, half.z),
            center + new Vector3(half.x, half.y, half.z),
            center + new Vector3(-half.x, half.y, half.z)
        };

        int[] meshTriangles =
        {
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            3, 7, 6, 3, 6, 2,
            1, 2, 6, 1, 6, 5,
            0, 4, 7, 0, 7, 3
        };

        Mesh mesh = new Mesh
        {
            name = "Runtime GPU SDF Proxy"
        };
        mesh.SetVertices(meshVertices);
        mesh.SetTriangles(meshTriangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private void DrawGpuSurface()
    {
        if (!UsesGpuSurfaceRendering || !gpuSurfaceReady || sdfSampleBuffer == null || gpuProxyMesh == null)
        {
            return;
        }

        EnsureGpuSurfaceMaterial();
        if (runtimeGpuSurfaceMaterial == null)
        {
            return;
        }

        runtimeGpuSurfaceMaterial.SetBuffer("_SdfSamples", sdfSampleBuffer);
        runtimeGpuSurfaceMaterial.SetVector("_SampleSize", new Vector4(SampleWidth, SampleHeight, SampleDepth, 0f));
        runtimeGpuSurfaceMaterial.SetVector("_GridMin", GetLocalMin());
        runtimeGpuSurfaceMaterial.SetVector("_LocalSize", LocalSize);
        runtimeGpuSurfaceMaterial.SetVector("_DisplaySize", DisplaySize);
        runtimeGpuSurfaceMaterial.SetVector("_DisplayCenter", DisplayCenter);
        EnsureGpuVisualCutBuffer();
        runtimeGpuSurfaceMaterial.SetBuffer("_VisualCutOperations", gpuVisualCutBuffer);
        runtimeGpuSurfaceMaterial.SetInt("_VisualCutOperationCount", 0);
        EnsureNativeCutDetailBuffer();
        EnsureNativeCutDetailTileBuffer();
        runtimeGpuSurfaceMaterial.SetBuffer("_NativeCutDetailSamples", nativeCutDetailBuffer);
        runtimeGpuSurfaceMaterial.SetBuffer("_NativeCutDetailTiles", nativeCutDetailTileBuffer);
        runtimeGpuSurfaceMaterial.SetInt("_NativeCutDetailEnabled", 0);
        runtimeGpuSurfaceMaterial.SetInt("_NativeCutDetailTileCount", 0);
        runtimeGpuSurfaceMaterial.SetInt("_ProfileSegmentCount", profileSegmentCount);
        runtimeGpuSurfaceMaterial.SetInt("_AngularProfileAxialSampleCount", cutterAngularProfileAxialSampleCount);
        runtimeGpuSurfaceMaterial.SetInt("_AngularProfileAngleSampleCount", cutterAngularProfileAngleSampleCount);
        if (EnsureCutterAngularProfileBuffers())
        {
            runtimeGpuSurfaceMaterial.SetBuffer("_AngularProfileMinRadiusSamples", cutterAngularProfileMinBuffer);
            runtimeGpuSurfaceMaterial.SetBuffer("_AngularProfileMaxRadiusSamples", cutterAngularProfileMaxBuffer);
        }
        runtimeGpuSurfaceMaterial.SetFloat("_VoxelSize", voxelSize);
        runtimeGpuSurfaceMaterial.SetFloat("_IsoLevel", IsoLevel);
        runtimeGpuSurfaceMaterial.SetFloat("_MaxSteps", Mathf.Max(16, gpuRaymarchMaxSteps));
        runtimeGpuSurfaceMaterial.SetFloat("_StepScale", Mathf.Clamp(gpuRaymarchStepScale, 0.25f, 2f));
        runtimeGpuSurfaceMaterial.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
        runtimeGpuSurfaceMaterial.SetMatrix("_WorldToLocal", transform.worldToLocalMatrix);
        runtimeGpuSurfaceMaterial.SetColor("_BaseColor", gpuSurfaceColor);
        runtimeGpuSurfaceMaterial.SetVector("_LightDirection", new Vector4(0.35f, 0.8f, -0.45f, 0f));

        Graphics.DrawMesh(
            gpuProxyMesh,
            transform.localToWorldMatrix,
            runtimeGpuSurfaceMaterial,
            gameObject.layer,
            null,
            0,
            null,
            ShadowCastingMode.On,
            true);
    }

    private Bounds GetGpuSurfaceWorldBounds()
    {
        Vector3 scale = transform.lossyScale;
        Vector3 worldSize = new Vector3(
            Mathf.Abs(DisplaySize.x * scale.x),
            Mathf.Abs(DisplaySize.y * scale.y),
            Mathf.Abs(DisplaySize.z * scale.z));

        Bounds bounds = new Bounds(transform.TransformPoint(DisplayCenter), worldSize);
        bounds.Expand(voxelSize * 4f);
        return bounds;
    }

    private bool CutSelectedNativeCutterSdf(
        Vector3 worldStart,
        Vector3 worldEnd,
        Vector3 worldAxis,
        Vector3 worldRight,
        float worldRadius,
        float worldHeight,
        out int changedSampleCount)
    {
        changedSampleCount = 0;

        Vector3 localStart = transform.InverseTransformPoint(worldStart);
        Vector3 localEnd = transform.InverseTransformPoint(worldEnd);
        Vector3 localAxis = transform.InverseTransformDirection(worldAxis).normalized;
        Vector3 localRight = ResolveLocalCutterRight(localAxis, transform.InverseTransformDirection(worldRight));
        float localRadius = WorldDistanceToLocalDistance(worldRadius);
        float localHeight = WorldDistanceToLocalDistance(worldHeight);

        float updateBand = voxelSize * 2f;
        float affectedRadius = localRadius + updateBand;
        bool useMeshCutter = HasNativeMeshCutter &&
            (preferNativeMeshCutter || cutterProfileRadiusSampleCount < 2);

        Vector3 localMin;
        Vector3 localMax;
        if (useMeshCutter)
        {
            GetNativeCutterSweepBounds(localStart, localEnd, localAxis, localRight, updateBand, out localMin, out localMax);
        }
        else
        {
            Vector3 startTop = localStart + localAxis * localHeight;
            Vector3 endTop = localEnd + localAxis * localHeight;
            localMin = Vector3.Min(Vector3.Min(localStart, localEnd), Vector3.Min(startTop, endTop)) - Vector3.one * affectedRadius;
            localMax = Vector3.Max(Vector3.Max(localStart, localEnd), Vector3.Max(startTop, endTop)) + Vector3.one * affectedRadius;
        }

        Vector3 detailMin = GetLocalMin() - Vector3.one * updateBand;
        Vector3 detailMax = -GetLocalMin() + Vector3.one * updateBand;
        if (!AabbOverlaps(localMin, localMax, detailMin, detailMax))
        {
            if (logNativeCutDiagnostics && Application.isPlaying && Time.time >= nextNativeNoOverlapLogTime)
            {
                nextNativeNoOverlapLogTime = Time.time + 1f;
                Debug.Log(
                    $"Native selected cutter skipped: sweep bounds outside workpiece. " +
                    $"sweepMin={localMin}, sweepMax={localMax}, workpieceMin={detailMin}, workpieceMax={detailMax}.",
                    this);
            }
            return false;
        }

        Vector3 min = GetLocalMin();
        int minX = ClampIndex(Mathf.FloorToInt((localMin.x - min.x) / voxelSize) + 1, SampleWidth);
        int maxX = ClampIndex(Mathf.CeilToInt((localMax.x - min.x) / voxelSize) + 1, SampleWidth);
        int minY = ClampIndex(Mathf.FloorToInt((localMin.y - min.y) / voxelSize) + 1, SampleHeight);
        int maxY = ClampIndex(Mathf.CeilToInt((localMax.y - min.y) / voxelSize) + 1, SampleHeight);
        int minZ = ClampIndex(Mathf.FloorToInt((localMin.z - min.z) / voxelSize) + 1, SampleDepth);
        int maxZ = ClampIndex(Mathf.CeilToInt((localMax.z - min.z) / voxelSize) + 1, SampleDepth);

        changedSampleCount = useMeshCutter
            ? NativeCutSelectedCutter(
                localStart,
                localEnd,
                localAxis,
                localRight,
                localRadius,
                localHeight,
                updateBand,
                minX,
                maxX,
                minY,
                maxY,
                minZ,
                maxZ)
            : NativeCutProfileCutter(
                localStart,
                localEnd,
                localAxis,
                localRadius,
                localHeight,
                updateBand,
                minX,
                maxX,
                minY,
                maxY,
                minZ,
                maxZ);
        if (changedSampleCount <= 0)
        {
            return false;
        }

        if (!UploadSdfRegionFromNative(minX, maxX, minY, maxY, minZ, maxZ))
        {
            UploadSdfFromNative();
        }

        ClearGpuVisualCuts();
        ClearNativeCutDetailDisplay(false);
        if (UsesChunkedSmoothMesh)
        {
            MarkDirtyCells(
                Mathf.Max(0, minX - 1),
                Mathf.Min(CellWidth, maxX + 1),
                Mathf.Max(0, minY - 1),
                Mathf.Min(CellHeight, maxY + 1),
                Mathf.Max(0, minZ - 1),
                Mathf.Min(CellDepth, maxZ + 1),
                localEnd);
        }
        return true;
    }

    private bool CutNativeCapsuleSdf(
        Vector3 worldStart,
        Vector3 worldEnd,
        float worldRadius,
        out int changedSampleCount)
    {
        changedSampleCount = 0;

        Vector3 localStart = transform.InverseTransformPoint(worldStart);
        Vector3 localEnd = transform.InverseTransformPoint(worldEnd);
        float localRadius = WorldDistanceToLocalDistance(worldRadius);
        float updateBand = voxelSize * 2f;
        float affectedRadius = localRadius + updateBand;

        Vector3 localMin = Vector3.Min(localStart, localEnd) - Vector3.one * affectedRadius;
        Vector3 localMax = Vector3.Max(localStart, localEnd) + Vector3.one * affectedRadius;
        Vector3 detailMin = GetLocalMin() - Vector3.one * updateBand;
        Vector3 detailMax = -GetLocalMin() + Vector3.one * updateBand;
        if (!AabbOverlaps(localMin, localMax, detailMin, detailMax))
        {
            if (logNativeCutDiagnostics && Application.isPlaying && Time.time >= nextNativeNoOverlapLogTime)
            {
                nextNativeNoOverlapLogTime = Time.time + 1f;
                Debug.Log(
                    $"Native capsule skipped: sweep bounds outside workpiece. " +
                    $"start={FormatVector(localStart)}, end={FormatVector(localEnd)}, radius={localRadius:0.####}mm, " +
                    $"sweepMin={FormatVector(localMin)}, sweepMax={FormatVector(localMax)}, " +
                    $"workpieceMin={FormatVector(detailMin)}, workpieceMax={FormatVector(detailMax)}.",
                    this);
            }
            return false;
        }

        Vector3 min = GetLocalMin();
        int minX = ClampIndex(Mathf.FloorToInt((localMin.x - min.x) / voxelSize) + 1, SampleWidth);
        int maxX = ClampIndex(Mathf.CeilToInt((localMax.x - min.x) / voxelSize) + 1, SampleWidth);
        int minY = ClampIndex(Mathf.FloorToInt((localMin.y - min.y) / voxelSize) + 1, SampleHeight);
        int maxY = ClampIndex(Mathf.CeilToInt((localMax.y - min.y) / voxelSize) + 1, SampleHeight);
        int minZ = ClampIndex(Mathf.FloorToInt((localMin.z - min.z) / voxelSize) + 1, SampleDepth);
        int maxZ = ClampIndex(Mathf.CeilToInt((localMax.z - min.z) / voxelSize) + 1, SampleDepth);

        try
        {
            changedSampleCount = SdfNativePlugin.sdf_cut_capsule(
                localStart.x,
                localStart.y,
                localStart.z,
                localEnd.x,
                localEnd.y,
                localEnd.z,
                localRadius,
                minX,
                maxX,
                minY,
                maxY,
                minZ,
                maxZ);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native capsule cut failed: {ex.Message}", this);
            sdfNativeReady = false;
            return false;
        }

        if (changedSampleCount <= 0)
        {
            if (logNativeCutDiagnostics && Application.isPlaying && Time.time >= nextNativeNoChangeLogTime)
            {
                nextNativeNoChangeLogTime = Time.time + 1f;
                Debug.Log(
                    $"Native capsule changed 0 samples: start={FormatVector(localStart)}, " +
                    $"end={FormatVector(localEnd)}, radius={localRadius:0.####}mm, voxelSize={voxelSize:0.####}mm, " +
                    $"sampleBounds=({minX}-{maxX}, {minY}-{maxY}, {minZ}-{maxZ}).",
                    this);
            }
            return false;
        }

        MarkNativeConnectivityDirty(minX, maxX, minY, maxY, minZ, maxZ);

        if (!UploadSdfRegionFromNative(minX, maxX, minY, maxY, minZ, maxZ))
        {
            UploadSdfFromNative();
        }

        ClearNativeCutDetailDisplay(false);
        if (UsesChunkedSmoothMesh)
        {
            MarkDirtyCells(
                Mathf.Max(0, minX - 1),
                Mathf.Min(CellWidth, maxX + 1),
                Mathf.Max(0, minY - 1),
                Mathf.Min(CellHeight, maxY + 1),
                Mathf.Max(0, minZ - 1),
                Mathf.Min(CellDepth, maxZ + 1),
                localEnd);
        }

        return true;
    }

    private void AddGpuVisualCut(
        Vector3 localStart,
        Vector3 localEnd,
        Vector3 localAxis,
        Vector3 localRight,
        float localRadius,
        float localHeight)
    {
        if (!useGpuVisualCutPreview || !UsesGpuSurfaceRendering || localRadius <= 0f || localHeight <= 0f)
        {
            return;
        }

        Vector3 axis = localAxis.sqrMagnitude > 0.000001f ? localAxis.normalized : Vector3.up;
        Vector3 right = ResolveLocalCutterRight(axis, localRight);
        GpuVisualCutOperation operation = new GpuVisualCutOperation
        {
            startRadius = new Vector4(localStart.x, localStart.y, localStart.z, localRadius),
            endHeight = new Vector4(localEnd.x, localEnd.y, localEnd.z, localHeight),
            axis = new Vector4(axis.x, axis.y, axis.z, 0f),
            profileMeta = new Vector4(cutterProfileRadiusSampleCount, right.x, right.y, right.z),
            profile0 = PackProfileRadiusSamples(0),
            profile1 = PackProfileRadiusSamples(4),
            profile2 = PackProfileRadiusSamples(8),
            profile3 = PackProfileRadiusSamples(12),
            profile4 = PackProfileRadiusSamples(16),
            profile5 = PackProfileRadiusSamples(20),
            profile6 = PackProfileRadiusSamples(24),
            profile7 = PackProfileRadiusSamples(28)
        };

        if (gpuVisualCutOperations.Count > 0 &&
            TryMergeGpuVisualCut(gpuVisualCutOperations.Count - 1, operation, out bool geometryChanged))
        {
            if (geometryChanged)
            {
                gpuVisualCutsDirty = true;
            }

            return;
        }

        gpuVisualCutOperations.Add(operation);
        int maxOperations = Mathf.Max(1, maxGpuVisualCutOperations);
        while (gpuVisualCutOperations.Count > maxOperations)
        {
            gpuVisualCutOperations.RemoveAt(0);
        }

        gpuVisualCutsDirty = true;
    }

    private void ClearGpuVisualCuts()
    {
        if (gpuVisualCutOperations.Count == 0)
        {
            return;
        }

        gpuVisualCutOperations.Clear();
        gpuVisualCutsDirty = true;
    }

    private Vector4 PackProfileRadiusSamples(int startIndex)
    {
        return new Vector4(
            GetCurrentProfileRadiusSample(startIndex),
            GetCurrentProfileRadiusSample(startIndex + 1),
            GetCurrentProfileRadiusSample(startIndex + 2),
            GetCurrentProfileRadiusSample(startIndex + 3));
    }

    private float GetCurrentProfileRadiusSample(int index)
    {
        return index >= 0 && index < cutterProfileRadiusSampleCount
            ? cutterProfileRadiusSamples[index]
            : 0f;
    }

    private static Vector3 ResolveLocalCutterRight(Vector3 localAxis, Vector3 requestedRight)
    {
        Vector3 axis = localAxis.sqrMagnitude > 0.000001f ? localAxis.normalized : Vector3.up;
        Vector3 right = requestedRight - axis * Vector3.Dot(requestedRight, axis);
        if (right.sqrMagnitude > 0.000001f)
        {
            return right.normalized;
        }

        Vector3 fallback = Vector3.Cross(axis, Vector3.forward);
        if (fallback.sqrMagnitude <= 0.000001f)
        {
            fallback = Vector3.Cross(axis, Vector3.right);
        }

        return fallback.sqrMagnitude > 0.000001f ? fallback.normalized : Vector3.right;
    }

    private void GetNativeCutterSweepBounds(
        Vector3 localStart,
        Vector3 localEnd,
        Vector3 localAxis,
        Vector3 localRight,
        float padding,
        out Vector3 boundsMin,
        out Vector3 boundsMax)
    {
        Vector3 axis = localAxis.sqrMagnitude > 0.000001f ? localAxis.normalized : Vector3.up;
        Vector3 right = ResolveLocalCutterRight(axis, localRight);
        Vector3 forward = Vector3.Cross(right, axis);
        if (forward.sqrMagnitude <= 0.000001f)
        {
            forward = Vector3.Cross(axis, right);
        }
        forward = forward.sqrMagnitude > 0.000001f ? forward.normalized : Vector3.forward;

        boundsMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        boundsMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        for (int xi = 0; xi < 2; xi++)
        {
            float x = xi == 0 ? nativeCutterMeshLocalMin.x : nativeCutterMeshLocalMax.x;
            for (int yi = 0; yi < 2; yi++)
            {
                float y = yi == 0 ? nativeCutterMeshLocalMin.y : nativeCutterMeshLocalMax.y;
                for (int zi = 0; zi < 2; zi++)
                {
                    float z = zi == 0 ? nativeCutterMeshLocalMin.z : nativeCutterMeshLocalMax.z;
                    Vector3 offset = right * x + axis * y + forward * z;
                    Vector3 startPoint = localStart + offset;
                    Vector3 endPoint = localEnd + offset;
                    boundsMin = Vector3.Min(boundsMin, Vector3.Min(startPoint, endPoint));
                    boundsMax = Vector3.Max(boundsMax, Vector3.Max(startPoint, endPoint));
                }
            }
        }

        Vector3 expand = Vector3.one * Mathf.Max(0f, padding);
        boundsMin -= expand;
        boundsMax += expand;
    }

    private bool TryMergeGpuVisualCut(int index, GpuVisualCutOperation next, out bool geometryChanged)
    {
        geometryChanged = false;
        GpuVisualCutOperation previous = gpuVisualCutOperations[index];
        Vector3 previousStart = new Vector3(previous.startRadius.x, previous.startRadius.y, previous.startRadius.z);
        Vector3 previousEnd = new Vector3(previous.endHeight.x, previous.endHeight.y, previous.endHeight.z);
        Vector3 nextStart = new Vector3(next.startRadius.x, next.startRadius.y, next.startRadius.z);
        Vector3 nextEnd = new Vector3(next.endHeight.x, next.endHeight.y, next.endHeight.z);
        Vector3 previousAxis = new Vector3(previous.axis.x, previous.axis.y, previous.axis.z).normalized;
        Vector3 nextAxis = new Vector3(next.axis.x, next.axis.y, next.axis.z).normalized;
        float mergeDistance = voxelSize * 2f;

        if ((previousEnd - nextStart).sqrMagnitude > mergeDistance * mergeDistance ||
            Vector3.Dot(previousAxis, nextAxis) < 0.9999f ||
            previous.startRadius.w != next.startRadius.w ||
            previous.endHeight.w != next.endHeight.w ||
            !GpuVisualProfilesMatch(previous, next))
        {
            return false;
        }

        Vector3 previousDirection = previousEnd - previousStart;
        Vector3 nextDirection = nextEnd - nextStart;
        float geometryScale = Mathf.Max(
            voxelSize,
            Mathf.Max(Mathf.Abs(previous.startRadius.w), Mathf.Abs(previous.endHeight.w)));
        float directionEpsilon = geometryScale * 0.000001f;
        float directionEpsilonSqr = directionEpsilon * directionEpsilon;
        if (previousDirection.sqrMagnitude > directionEpsilonSqr &&
            nextDirection.sqrMagnitude > directionEpsilonSqr &&
            Vector3.Dot(previousDirection.normalized, nextDirection.normalized) < 0.999f)
        {
            return false;
        }

        if ((previousEnd - nextEnd).sqrMagnitude <= directionEpsilonSqr)
        {
            return true;
        }

        previous.endHeight = new Vector4(nextEnd.x, nextEnd.y, nextEnd.z, previous.endHeight.w);
        gpuVisualCutOperations[index] = previous;
        geometryChanged = true;
        return true;
    }

    private static bool GpuVisualProfilesMatch(GpuVisualCutOperation a, GpuVisualCutOperation b)
    {
        return a.profileMeta == b.profileMeta
            && a.profile0 == b.profile0
            && a.profile1 == b.profile1
            && a.profile2 == b.profile2
            && a.profile3 == b.profile3
            && a.profile4 == b.profile4
            && a.profile5 == b.profile5
            && a.profile6 == b.profile6
            && a.profile7 == b.profile7;
    }

    private void EnsureGpuVisualCutBuffer()
    {
        int requiredCapacity = Mathf.NextPowerOfTwo(Mathf.Max(1, gpuVisualCutOperations.Count));
        if (gpuVisualCutBuffer == null || gpuVisualCutBufferCapacity < requiredCapacity)
        {
            if (gpuVisualCutBuffer != null)
            {
                gpuVisualCutBuffer.Release();
            }

            gpuVisualCutBufferCapacity = requiredCapacity;
            gpuVisualCutBuffer = new ComputeBuffer(gpuVisualCutBufferCapacity, sizeof(float) * 48);
            gpuVisualCutsDirty = true;
        }

        if (!gpuVisualCutsDirty)
        {
            return;
        }

        if (gpuVisualCutOperations.Count > 0)
        {
            gpuVisualCutBuffer.SetData(gpuVisualCutOperations, 0, 0, gpuVisualCutOperations.Count);
        }
        else
        {
            gpuVisualCutBuffer.SetData(new GpuVisualCutOperation[1]);
        }

        gpuVisualCutsDirty = false;
    }

    private void RequestNativeCutDetailDisplay(Vector3 localBoundsMin, Vector3 localBoundsMax)
    {
        if (!useNativeCutDetailDisplay || !UsesGpuSurfaceRendering || !sdfNativeReady)
        {
            ClearNativeCutDetailDisplay(false);
            return;
        }

        if (!Application.isPlaying || !isActiveAndEnabled)
        {
            return;
        }

        if (Time.time < nextNativeCutDetailUpdateTime)
        {
            return;
        }

        nextNativeCutDetailUpdateTime = Time.time + Mathf.Max(0.01f, nativeCutDetailUpdateInterval);
        CancelNativeCutDetailRebuild();
        RemoveNativeCutDetailTilesOverlapping(localBoundsMin, localBoundsMax);
        nativeCutDetailRequestVersion++;
        nativeCutDetailCoroutine = StartCoroutine(
            RebuildNativeCutDetailDisplayCoroutine(
                localBoundsMin,
                localBoundsMax,
                nativeCutDetailRequestVersion));
    }

    private IEnumerator RebuildNativeCutDetailDisplayCoroutine(
        Vector3 localBoundsMin,
        Vector3 localBoundsMax,
        int requestVersion)
    {
        Vector3 stockMin = GetLocalMin();
        Vector3 stockMax = -stockMin;
        float padding = Mathf.Max(0f, nativeCutDetailPaddingMm);
        Vector3 min = Vector3.Max(localBoundsMin - Vector3.one * padding, stockMin);
        Vector3 max = Vector3.Min(localBoundsMax + Vector3.one * padding, stockMax);
        Vector3 size = max - min;
        float requestedStep = Mathf.Max(0.001f, nativeCutDetailVoxelSize);
        if (size.x <= requestedStep || size.y <= requestedStep || size.z <= requestedStep)
        {
            nativeCutDetailCoroutine = null;
            yield break;
        }

        int cellX = Mathf.Max(1, Mathf.CeilToInt(size.x / requestedStep));
        int cellY = Mathf.Max(1, Mathf.CeilToInt(size.y / requestedStep));
        int cellZ = Mathf.Max(1, Mathf.CeilToInt(size.z / requestedStep));
        long sampleCount = (long)(cellX + 1) * (cellY + 1) * (cellZ + 1);
        int sampleBudget = Mathf.Max(4096, maxNativeCutDetailSamples);
        float step = requestedStep;
        if (sampleCount > sampleBudget)
        {
            float relaxation = Mathf.Pow(sampleCount / (float)sampleBudget, 1f / 3f);
            step = requestedStep * relaxation;
            cellX = Mathf.Max(1, Mathf.CeilToInt(size.x / step));
            cellY = Mathf.Max(1, Mathf.CeilToInt(size.y / step));
            cellZ = Mathf.Max(1, Mathf.CeilToInt(size.z / step));
            sampleCount = (long)(cellX + 1) * (cellY + 1) * (cellZ + 1);
        }

        if (sampleCount <= 0 || sampleCount > int.MaxValue)
        {
            nativeCutDetailCoroutine = null;
            yield break;
        }

        int countX = cellX + 1;
        int countY = cellY + 1;
        int countZ = cellZ + 1;
        int totalSamples = (int)sampleCount;
        if (nativeCutDetailValues == null || nativeCutDetailValues.Length < totalSamples)
        {
            nativeCutDetailValues = new float[totalSamples];
        }

        int batchCapacity = Mathf.Min(Mathf.Max(1024, nativeCutDetailBatchSize), totalSamples);
        if (nativeCutDetailBatchPoints == null || nativeCutDetailBatchPoints.Length < batchCapacity * 3)
        {
            nativeCutDetailBatchPoints = new float[batchCapacity * 3];
        }

        if (nativeCutDetailBatchValues == null || nativeCutDetailBatchValues.Length < batchCapacity)
        {
            nativeCutDetailBatchValues = new float[batchCapacity];
        }

        int globalIndex = 0;
        int batchCount = 0;
        for (int z = 0; z < countZ; z++)
        {
            float pz = min.z + z * step;
            for (int y = 0; y < countY; y++)
            {
                float py = min.y + y * step;
                for (int x = 0; x < countX; x++)
                {
                    float px = min.x + x * step;
                    int batchOffset = batchCount * 3;
                    nativeCutDetailBatchPoints[batchOffset + 0] = px;
                    nativeCutDetailBatchPoints[batchOffset + 1] = py;
                    nativeCutDetailBatchPoints[batchOffset + 2] = pz;
                    batchCount++;

                    if (batchCount >= batchCapacity)
                    {
                        if (!SampleNativeCutDetailBatch(globalIndex, batchCount))
                        {
                            nativeCutDetailCoroutine = null;
                            yield break;
                        }

                        globalIndex += batchCount;
                        batchCount = 0;
                        if (requestVersion != nativeCutDetailRequestVersion)
                        {
                            yield break;
                        }

                        yield return null;
                    }
                }
            }
        }

        if (batchCount > 0 && !SampleNativeCutDetailBatch(globalIndex, batchCount))
        {
            nativeCutDetailCoroutine = null;
            yield break;
        }

        if (requestVersion != nativeCutDetailRequestVersion)
        {
            yield break;
        }

        UploadNativeCutDetailDisplay(min, step, new Vector3Int(countX, countY, countZ), totalSamples);
        nativeCutDetailCoroutine = null;
    }

    private bool SampleNativeCutDetailBatch(int destinationStart, int count)
    {
        try
        {
            int sampled = SdfNativePlugin.sdf_sample_cut_points(
                nativeCutDetailBatchPoints,
                nativeCutDetailBatchValues,
                count);
            if (sampled != count)
            {
                ClearNativeCutDetailDisplay(false);
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                float value = nativeCutDetailBatchValues[i];
                nativeCutDetailValues[destinationStart + i] =
                    float.IsNaN(value) || float.IsInfinity(value)
                        ? NativeCutDetailAirValue
                        : Mathf.Clamp(value, NativeCutDetailAirValue, 1000000f);
            }

            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native cut detail sampling failed: {ex.Message}", this);
            sdfNativeReady = false;
            ClearNativeCutDetailDisplay(false);
            return false;
        }
    }

    private void UploadNativeCutDetailDisplay(Vector3 min, float step, Vector3Int size, int sampleCount)
    {
        if (sampleCount <= 0 || nativeCutDetailValues == null)
        {
            return;
        }

        float[] values = new float[sampleCount];
        System.Array.Copy(nativeCutDetailValues, values, sampleCount);
        nativeCutDetailTiles.Add(new NativeCutDetailTile
        {
            min = min,
            step = Mathf.Max(0.000001f, step),
            size = size,
            values = values,
            sampleCount = sampleCount
        });
        TrimNativeCutDetailTiles();
        UploadNativeCutDetailCache();
    }

    private void EnsureNativeCutDetailBuffer(int requiredCapacity = 1)
    {
        int safeCapacity = Mathf.Max(1, requiredCapacity);
        if (nativeCutDetailBuffer == null || nativeCutDetailBufferCapacity < safeCapacity)
        {
            if (nativeCutDetailBuffer != null)
            {
                nativeCutDetailBuffer.Release();
            }

            nativeCutDetailBufferCapacity = safeCapacity;
            nativeCutDetailBuffer = new ComputeBuffer(nativeCutDetailBufferCapacity, sizeof(float));
            nativeCutDetailBuffer.SetData(nativeCutDetailDummy);
        }
    }

    private void EnsureNativeCutDetailTileBuffer(int requiredTileVectorCount = 2)
    {
        int safeCapacity = Mathf.Max(2, requiredTileVectorCount);
        if (nativeCutDetailTileBuffer == null || nativeCutDetailTileBufferCapacity < safeCapacity)
        {
            if (nativeCutDetailTileBuffer != null)
            {
                nativeCutDetailTileBuffer.Release();
            }

            nativeCutDetailTileBufferCapacity = safeCapacity;
            nativeCutDetailTileBuffer = new ComputeBuffer(nativeCutDetailTileBufferCapacity, sizeof(float) * 4);
            nativeCutDetailTileBuffer.SetData(nativeCutDetailTileDummy);
        }
    }

    private void UploadNativeCutDetailCache()
    {
        int tileCount = nativeCutDetailTiles.Count;
        int sampleCount = CountNativeCutDetailSamples();
        nativeCutDetailReady = tileCount > 0 && sampleCount > 0;
        EnsureNativeCutDetailBuffer(Mathf.Max(1, sampleCount));
        EnsureNativeCutDetailTileBuffer(Mathf.Max(2, tileCount * 2));

        if (!nativeCutDetailReady)
        {
            nativeCutDetailBuffer.SetData(nativeCutDetailDummy);
            nativeCutDetailTileBuffer.SetData(nativeCutDetailTileDummy);
            return;
        }

        if (nativeCutDetailPackedValues == null || nativeCutDetailPackedValues.Length < sampleCount)
        {
            nativeCutDetailPackedValues = new float[sampleCount];
        }

        int tileVectorCount = tileCount * 2;
        if (nativeCutDetailTileData == null || nativeCutDetailTileData.Length < tileVectorCount)
        {
            nativeCutDetailTileData = new Vector4[tileVectorCount];
        }

        int sampleOffset = 0;
        for (int i = 0; i < tileCount; i++)
        {
            NativeCutDetailTile tile = nativeCutDetailTiles[i];
            System.Array.Copy(tile.values, 0, nativeCutDetailPackedValues, sampleOffset, tile.sampleCount);
            nativeCutDetailTileData[i * 2] = new Vector4(
                tile.min.x,
                tile.min.y,
                tile.min.z,
                tile.step);
            nativeCutDetailTileData[i * 2 + 1] = new Vector4(
                tile.size.x,
                tile.size.y,
                tile.size.z,
                sampleOffset);
            sampleOffset += tile.sampleCount;
        }

        nativeCutDetailBuffer.SetData(nativeCutDetailPackedValues, 0, 0, sampleCount);
        nativeCutDetailTileBuffer.SetData(nativeCutDetailTileData, 0, 0, tileVectorCount);
    }

    private void TrimNativeCutDetailTiles()
    {
        int maxTiles = Mathf.Max(1, maxNativeCutDetailTiles);
        int maxSamples = Mathf.Max(4096, maxNativeCutDetailCachedSamples);
        while (nativeCutDetailTiles.Count > maxTiles ||
               (nativeCutDetailTiles.Count > 0 && CountNativeCutDetailSamples() > maxSamples))
        {
            nativeCutDetailTiles.RemoveAt(0);
        }
    }

    private int CountNativeCutDetailSamples()
    {
        int count = 0;
        for (int i = 0; i < nativeCutDetailTiles.Count; i++)
        {
            count += Mathf.Max(0, nativeCutDetailTiles[i].sampleCount);
        }

        return count;
    }

    private void RemoveNativeCutDetailTilesOverlapping(Vector3 localBoundsMin, Vector3 localBoundsMax)
    {
        bool removed = false;
        for (int i = nativeCutDetailTiles.Count - 1; i >= 0; i--)
        {
            if (NativeCutDetailTileOverlaps(nativeCutDetailTiles[i], localBoundsMin, localBoundsMax))
            {
                nativeCutDetailTiles.RemoveAt(i);
                removed = true;
            }
        }

        if (removed)
        {
            UploadNativeCutDetailCache();
        }
    }

    private static bool NativeCutDetailTileOverlaps(NativeCutDetailTile tile, Vector3 boundsMin, Vector3 boundsMax)
    {
        Vector3 tileMax = tile.min + new Vector3(
            Mathf.Max(0, tile.size.x - 1),
            Mathf.Max(0, tile.size.y - 1),
            Mathf.Max(0, tile.size.z - 1)) * tile.step;
        return AabbOverlaps(tile.min, tileMax, boundsMin, boundsMax);
    }

    private void ClearNativeCutDetailDisplay(bool releaseBuffer)
    {
        nativeCutDetailReady = false;
        nativeCutDetailTiles.Clear();
        if (releaseBuffer)
        {
            if (nativeCutDetailBuffer != null)
            {
                nativeCutDetailBuffer.Release();
                nativeCutDetailBuffer = null;
            }

            nativeCutDetailBufferCapacity = 0;
            if (nativeCutDetailTileBuffer != null)
            {
                nativeCutDetailTileBuffer.Release();
                nativeCutDetailTileBuffer = null;
            }

            nativeCutDetailTileBufferCapacity = 0;
        }
        else
        {
            if (nativeCutDetailBuffer != null)
            {
                nativeCutDetailBuffer.SetData(nativeCutDetailDummy);
            }

            if (nativeCutDetailTileBuffer != null)
            {
                nativeCutDetailTileBuffer.SetData(nativeCutDetailTileDummy);
            }
        }
    }

    private void CancelNativeCutDetailRebuild()
    {
        nativeCutDetailRequestVersion++;
        if (nativeCutDetailCoroutine != null)
        {
            StopCoroutine(nativeCutDetailCoroutine);
            nativeCutDetailCoroutine = null;
        }
    }

    private static bool AabbOverlaps(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB)
    {
        return minA.x <= maxB.x && maxA.x >= minB.x &&
               minA.y <= maxB.y && maxA.y >= minB.y &&
               minA.z <= maxB.z && maxA.z >= minB.z;
    }

    // ========================================================================
    // Native SDF plugin integration
    // ========================================================================

    private void InitializeNativeSdfPlugin()
    {
        sdfNativeReady = false;
        nextNativeConnectivityCheckTime = 0f;
        nativeConnectivityDirty = false;
        nativeConnectivityInFlight = false;
        nativeConnectivityStalled = false;
        nativeConnectivityCheckStartTime = 0f;
        nextNativeConnectivityStallLogTime = 0f;
        lastNativeConnectivityDirtyTime = 0f;
        nativeConnectivityHasRegion = false;
        nativeOpenVdbActiveVoxelCount = 0;

        if (surfaceMode != WorkpieceSurfaceMode.SmoothSdf)
        {
            return;
        }

        if (blankShape == WorkpieceBlankShape.ImportedMesh && !RefreshImportedBlankMeshFromSource())
        {
            ReleaseGpuSdfResources();
            ReleaseGpuSurfaceResources();
            sdfSamples = null;
            Debug.LogWarning("Native SDF init aborted because the imported blank mesh is missing or outside the allowed envelope.", this);
            return;
        }

        try
        {
            Vector3 min = GetLocalMin();
            Vector3 size = LocalSize;
            int nativeBlankShape = ResolveNativeBlankShape(blankShape);
            SdfNativePlugin.sdf_plugin_init(
                width, height, depth,
                SampleWidth, SampleHeight, SampleDepth,
                voxelSize,
                min.x, min.y, min.z,
                size.x, size.y, size.z,
                nativeBlankShape,
                ResolveBlankInnerRadiusLocal());
            SdfNativePlugin.sdf_set_profile_segment_count(profileSegmentCount);
            sdfNativeReady = true;
            UploadNativeProfileRadiusSamples();
            nativeOpenVdbActiveVoxelCount = Mathf.Max(0, SdfNativePlugin.sdf_openvdb_active_voxel_count());
            if (blankShape == WorkpieceBlankShape.ImportedMesh)
            {
                if (!UploadNativeBlankMesh())
                {
                    ReleaseGpuSdfResources();
                    ReleaseGpuSurfaceResources();
                    sdfSamples = null;
                    sdfNativeReady = false;
                    Debug.LogWarning("Native SDF init aborted because the imported blank mesh was rejected by the native kernel.", this);
                    return;
                }
            }
            else
            {
                UploadSdfFromNative();
            }

            int nativeBackend = SdfNativePlugin.sdf_get_active_compute_backend();
            int nativeBackendCapabilities = SdfNativePlugin.sdf_get_compute_backend_capabilities();
            UploadNativeCutterMesh();
            Debug.Log(
                $"Native SDF ready: plugin={SdfNativePlugin.PluginName}, " +
                $"samples={SdfNativePlugin.sdf_get_sample_count():n0}, " +
                $"openVdb={SdfNativePlugin.sdf_openvdb_available() != 0}, " +
                $"computeBackend={FormatNativeComputeBackend(nativeBackend)}, " +
                $"backendCaps=0x{nativeBackendCapabilities:X}, " +
                $"blankShape={blankShape}, " +
                $"workpieceActiveVoxels={nativeOpenVdbActiveVoxelCount:n0}, " +
                $"cutterActiveVoxels={nativeCutterActiveVoxelCount:n0}.",
                this);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native SDF plugin init failed: {ex.Message}", this);
            sdfNativeReady = false;
        }
    }

    private void ShutdownNativeSdfPlugin()
    {
        if (!sdfNativeReady)
        {
            return;
        }

        try
        {
            SdfNativePlugin.sdf_plugin_shutdown();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native SDF plugin shutdown failed: {ex.Message}", this);
        }

        sdfNativeReady = false;
    }

    private bool UploadNativeBlankMesh()
    {
        if (blankShape != WorkpieceBlankShape.ImportedMesh)
        {
            return true;
        }

        if (!sdfNativeReady ||
            nativeBlankMeshVertices == null ||
            nativeBlankMeshTriangleIndices == null ||
            nativeBlankMeshVertexCount <= 0 ||
            nativeBlankMeshIndexCount < 3)
        {
            return false;
        }

        try
        {
            int uploaded = SdfNativePlugin.sdf_set_blank_mesh(
                nativeBlankMeshVertices,
                nativeBlankMeshVertexCount,
                nativeBlankMeshTriangleIndices,
                nativeBlankMeshIndexCount);
            if (uploaded == 0)
            {
                return false;
            }

            ClearGpuVisualCuts();
            ClearNativeCutDetailDisplay(false);
            nativeConnectivityDirty = false;
            nativeConnectivityInFlight = false;
            nativeConnectivityStalled = false;
            nativeConnectivityCheckStartTime = 0f;
            nextNativeConnectivityStallLogTime = 0f;
            lastNativeConnectivityDirtyTime = 0f;
            nativeConnectivityHasRegion = false;
            UploadSdfFromNative();
            Debug.Log(
                $"Native imported blank mesh uploaded: vertices={nativeBlankMeshVertexCount:n0}, " +
                $"triangles={nativeBlankMeshIndexCount / 3:n0}, " +
                $"meshBounds=({nativeBlankMeshLocalMin}..{nativeBlankMeshLocalMax}).",
                this);
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native imported blank mesh upload failed: {ex.Message}", this);
            sdfNativeReady = false;
            return false;
        }
    }

    private void UploadNativeCutterMesh()
    {
        nativeCutterActiveVoxelCount = 0;
        if (!sdfNativeReady ||
            nativeCutterMeshVertices == null ||
            nativeCutterMeshTriangleIndices == null ||
            nativeCutterMeshVertexCount <= 0 ||
            nativeCutterMeshIndexCount < 3)
        {
            return;
        }

        try
        {
            float cutterVoxelSize = Mathf.Max(voxelSize * 0.25f, 0.02f);
            int uploaded = SdfNativePlugin.sdf_set_cutter_mesh(
                nativeCutterMeshVertices,
                nativeCutterMeshVertexCount,
                nativeCutterMeshTriangleIndices,
                nativeCutterMeshIndexCount,
                cutterVoxelSize,
                3f);
            nativeCutterActiveVoxelCount = uploaded != 0
                ? Mathf.Max(1, SdfNativePlugin.sdf_get_cutter_active_voxel_count())
                : 0;
            if (nativeCutterActiveVoxelCount > 0)
            {
                Debug.Log(
                    $"Native cutter mesh uploaded: vertices={nativeCutterMeshVertexCount:n0}, " +
                    $"triangles={nativeCutterMeshIndexCount / 3:n0}, " +
                    $"cutterVoxelSize={cutterVoxelSize:0.####}mm, " +
                    $"activeVoxels={nativeCutterActiveVoxelCount:n0}, " +
                    $"meshBounds=({nativeCutterMeshLocalMin}..{nativeCutterMeshLocalMax}).",
                    this);
            }
            else
            {
                Debug.LogWarning(
                    $"Native cutter mesh upload produced no cutter SDF: vertices={nativeCutterMeshVertexCount:n0}, " +
                    $"triangles={nativeCutterMeshIndexCount / 3:n0}.",
                    this);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native cutter mesh upload failed: {ex.Message}", this);
            nativeCutterActiveVoxelCount = 0;
            sdfNativeReady = false;
        }
    }

    private int NativeCutSelectedCutter(Vector3 localStart, Vector3 localEnd, Vector3 localAxis, Vector3 localRight,
        float localRadius, float localHeight, float updateBand,
        int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        if (!sdfNativeReady)
        {
            return 0;
        }

        try
        {
            Vector3 resolvedRight = ResolveLocalCutterRight(localAxis, localRight);
            int changed = SdfNativePlugin.sdf_cut_selected_cutter(
                localStart.x, localStart.y, localStart.z,
                localEnd.x, localEnd.y, localEnd.z,
                localAxis.x, localAxis.y, localAxis.z,
                resolvedRight.x, resolvedRight.y, resolvedRight.z,
                localRadius, localHeight, updateBand,
                minX, maxX, minY, maxY, minZ, maxZ);
            if (logNativeCutDiagnostics && changed <= 0 && Application.isPlaying && Time.time >= nextNativeNoChangeLogTime)
            {
                nextNativeNoChangeLogTime = Time.time + 1f;
                Debug.Log(
                    $"Native selected cutter changed 0 samples: meshCutter={HasNativeMeshCutter}, " +
                    $"cutterActiveVoxels={nativeCutterActiveVoxelCount:n0}, " +
                    $"start={FormatVector(localStart)}, end={FormatVector(localEnd)}, " +
                    $"axis={FormatVector(localAxis)}, right={FormatVector(resolvedRight)}, " +
                    $"radius={localRadius:0.####}mm, height={localHeight:0.####}mm, " +
                    $"voxelSize={voxelSize:0.####}mm, " +
                    $"cutterBounds=({FormatVector(nativeCutterMeshLocalMin)}..{FormatVector(nativeCutterMeshLocalMax)}), " +
                    $"sampleBounds=({minX}-{maxX}, {minY}-{maxY}, {minZ}-{maxZ}).",
                    this);
            }
            if (changed > 0)
            {
                MarkNativeConnectivityDirty(minX, maxX, minY, maxY, minZ, maxZ);
            }
            if (logNativeCutDiagnostics && changed > 0)
            {
                if (Application.isPlaying && Time.time >= nextNativePositiveCutLogTime)
                {
                    nextNativePositiveCutLogTime = Time.time + 1f;
                    nativeOpenVdbActiveVoxelCount = Mathf.Max(0, SdfNativePlugin.sdf_openvdb_active_voxel_count());
                    int cutOperationCount = Mathf.Max(0, SdfNativePlugin.sdf_get_cut_operation_count());
                    Debug.Log(
                        $"Native selected cutter changed {changed:n0} samples: " +
                        $"workpieceActiveVoxels={nativeOpenVdbActiveVoxelCount:n0}, " +
                        $"cutOperations={cutOperationCount:n0}, " +
                        $"sampleBounds=({minX}-{maxX}, {minY}-{maxY}, {minZ}-{maxZ}).",
                        this);
                }
            }
            return changed;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native profile cutter cut failed: {ex.Message}", this);
            sdfNativeReady = false;
            return 0;
        }
    }

    private int NativeCutProfileCutter(Vector3 localStart, Vector3 localEnd, Vector3 localAxis,
        float localRadius, float localHeight, float updateBand,
        int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        if (!sdfNativeReady)
        {
            return 0;
        }

        try
        {
            int changed = SdfNativePlugin.sdf_cut_profile_cutter(
                localStart.x, localStart.y, localStart.z,
                localEnd.x, localEnd.y, localEnd.z,
                localAxis.x, localAxis.y, localAxis.z,
                localRadius, localHeight, updateBand,
                minX, maxX, minY, maxY, minZ, maxZ);
            if (logNativeCutDiagnostics && changed <= 0 && Application.isPlaying && Time.time >= nextNativeNoChangeLogTime)
            {
                nextNativeNoChangeLogTime = Time.time + 1f;
                Debug.Log(
                    $"Native profile cutter changed 0 samples: start={FormatVector(localStart)}, " +
                    $"end={FormatVector(localEnd)}, axis={FormatVector(localAxis)}, " +
                    $"radius={localRadius:0.####}mm, height={localHeight:0.####}mm, " +
                    $"voxelSize={voxelSize:0.####}mm, " +
                    $"sampleBounds=({minX}-{maxX}, {minY}-{maxY}, {minZ}-{maxZ}).",
                    this);
            }

            if (changed > 0)
            {
                MarkNativeConnectivityDirty(minX, maxX, minY, maxY, minZ, maxZ);
            }
            if (logNativeCutDiagnostics && changed > 0 &&
                Application.isPlaying && Time.time >= nextNativePositiveCutLogTime)
            {
                nextNativePositiveCutLogTime = Time.time + 1f;
                nativeOpenVdbActiveVoxelCount = Mathf.Max(0, SdfNativePlugin.sdf_openvdb_active_voxel_count());
                Debug.Log(
                    $"Native profile cutter changed {changed:n0} samples: " +
                    $"workpieceActiveVoxels={nativeOpenVdbActiveVoxelCount:n0}, " +
                    $"sampleBounds=({minX}-{maxX}, {minY}-{maxY}, {minZ}-{maxZ}).",
                    this);
            }

            return changed;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native profile cutter cut failed: {ex.Message}", this);
            sdfNativeReady = false;
            return 0;
        }
    }

    private void PollNativeConnectivity()
    {
        if (!sdfNativeReady ||
            !removeDetachedParts ||
            !automaticDetachedCleanup ||
            surfaceMode != WorkpieceSurfaceMode.SmoothSdf)
        {
            return;
        }

        if (nativeConnectivityInFlight)
        {
            if (nativeConnectivityStalled &&
                Application.isPlaying &&
                Time.time < nextNativeConnectivityCheckTime)
            {
                return;
            }

            int removed = SdfNativePlugin.sdf_try_apply_connectivity_cleanup();
            if (removed >= 0)
            {
                int componentCount = SdfNativePlugin.sdf_get_last_connectivity_component_count();
                int keepCoreCount = SdfNativePlugin.sdf_get_last_connectivity_keep_core_count();
                int removalCandidateCount = SdfNativePlugin.sdf_get_last_connectivity_removal_candidate_count();
                if (removed > 0)
                {
                    ClearGpuVisualCuts();
                    ClearNativeCutDetailDisplay(false);
                    UploadSdfFromNative();
                    RequestRebuildMesh();
                    Debug.Log($"DETACHED_CLEANUP nativeRemovedCells={removed}", this);
                }

                Debug.Log(
                    $"DETACHED_CLEANUP_RESULT check={nativeConnectivityCheckSerial} " +
                    $"components={componentCount} keepCore={keepCoreCount} " +
                    $"removalCandidates={removalCandidateCount} removed={removed} " +
                    $"plugin={SdfNativePlugin.PluginName}",
                    this);

                nativeConnectivityInFlight = false;
                nativeConnectivityStalled = false;

                if (removed > 0)
                {
                    nativeConnectivityDirty = true;
                    nextNativeConnectivityCheckTime = Application.isPlaying
                        ? Time.time + NativeConnectivityDelay()
                        : 0f;
                }
                else if (nativeConnectivityDirty)
                {
                    nextNativeConnectivityCheckTime = Application.isPlaying
                        ? Time.time + NativeConnectivityDelay()
                        : 0f;
                }
                else
                {
                    nativeConnectivityHasRegion = false;
                }
            }
            else if (Application.isPlaying &&
                Time.time - nativeConnectivityCheckStartTime > 0.75f)
            {
                nativeConnectivityStalled = true;
                nextNativeConnectivityCheckTime = Time.time + 0.25f;
                if (Time.time >= nextNativeConnectivityStallLogTime)
                {
                    nextNativeConnectivityStallLogTime = Time.time + 1f;
                    Debug.LogWarning(
                        $"DETACHED_CLEANUP_STALLED check={nativeConnectivityCheckSerial} " +
                        $"elapsed={Time.time - nativeConnectivityCheckStartTime:0.###}s " +
                        $"region={nativeConnectivityHasRegion} " +
                        $"bounds=({nativeConnectivityMinX}-{nativeConnectivityMaxX}, " +
                        $"{nativeConnectivityMinY}-{nativeConnectivityMaxY}, " +
                        $"{nativeConnectivityMinZ}-{nativeConnectivityMaxZ})",
                        this);
                }
            }
        }

        if (!nativeConnectivityInFlight &&
            !nativeConnectivityStalled &&
            nativeConnectivityDirty &&
            (!Application.isPlaying ||
             (Time.time >= nextNativeConnectivityCheckTime &&
              Time.time - lastNativeConnectivityDirtyTime >= NativeConnectivityIdleDelaySeconds)))
        {
            nativeConnectivityCheckSerial++;
            int padding = 32;
            if (nativeConnectivityHasRegion)
            {
                SdfNativePlugin.sdf_check_connectivity_region(
                    nativeConnectivityMinX,
                    nativeConnectivityMaxX,
                    nativeConnectivityMinY,
                    nativeConnectivityMaxY,
                    nativeConnectivityMinZ,
                    nativeConnectivityMaxZ,
                    padding);
            }
            else
            {
                SdfNativePlugin.sdf_check_connectivity();
            }
            nativeConnectivityInFlight = true;
            nativeConnectivityStalled = false;
            nativeConnectivityCheckStartTime = Application.isPlaying ? Time.time : 0f;
            nativeConnectivityDirty = false;
            nextNativeConnectivityCheckTime = Application.isPlaying
                ? Time.time + NativeConnectivityDelay()
                : 0f;
            Debug.Log(
                $"DETACHED_CLEANUP_CHECK started check={nativeConnectivityCheckSerial} " +
                $"region={nativeConnectivityHasRegion} " +
                $"bounds=({nativeConnectivityMinX}-{nativeConnectivityMaxX}, " +
                $"{nativeConnectivityMinY}-{nativeConnectivityMaxY}, " +
                $"{nativeConnectivityMinZ}-{nativeConnectivityMaxZ}) padding={padding}",
                this);
        }
    }

    private void MarkNativeConnectivityDirty()
    {
        bool wasDirty = nativeConnectivityDirty;
        nativeConnectivityDirty = true;
        lastNativeConnectivityDirtyTime = Application.isPlaying ? Time.time : 0f;
        nextNativeConnectivityCheckTime = Application.isPlaying
            ? Time.time + NativeConnectivityDelay()
            : 0f;
        if (automaticDetachedCleanup && !wasDirty && !nativeConnectivityInFlight)
        {
            Debug.Log("DETACHED_CLEANUP_DIRTY scheduled", this);
        }
    }

    private float NativeConnectivityDelay()
    {
        return Mathf.Max(detachedCleanupInterval, NativeConnectivityIdleDelaySeconds);
    }

    private void MarkNativeConnectivityDirty(int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        minX = Mathf.Clamp(minX, 0, SampleWidth - 1);
        maxX = Mathf.Clamp(maxX, 0, SampleWidth - 1);
        minY = Mathf.Clamp(minY, 0, SampleHeight - 1);
        maxY = Mathf.Clamp(maxY, 0, SampleHeight - 1);
        minZ = Mathf.Clamp(minZ, 0, SampleDepth - 1);
        maxZ = Mathf.Clamp(maxZ, 0, SampleDepth - 1);

        if (!nativeConnectivityHasRegion)
        {
            nativeConnectivityMinX = minX;
            nativeConnectivityMaxX = maxX;
            nativeConnectivityMinY = minY;
            nativeConnectivityMaxY = maxY;
            nativeConnectivityMinZ = minZ;
            nativeConnectivityMaxZ = maxZ;
            nativeConnectivityHasRegion = true;
        }
        else
        {
            nativeConnectivityMinX = Mathf.Min(nativeConnectivityMinX, minX);
            nativeConnectivityMaxX = Mathf.Max(nativeConnectivityMaxX, maxX);
            nativeConnectivityMinY = Mathf.Min(nativeConnectivityMinY, minY);
            nativeConnectivityMaxY = Mathf.Max(nativeConnectivityMaxY, maxY);
            nativeConnectivityMinZ = Mathf.Min(nativeConnectivityMinZ, minZ);
            nativeConnectivityMaxZ = Mathf.Max(nativeConnectivityMaxZ, maxZ);
        }

        MarkNativeConnectivityDirty();
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";
    }

    private static string FormatNativeComputeBackend(int backend)
    {
        return backend switch
        {
            0 => "CPU",
            1 => "GPU",
            _ => $"Unknown({backend})"
        };
    }

    private static int ResolveNativeBlankShape(WorkpieceBlankShape shape)
    {
        return shape switch
        {
            WorkpieceBlankShape.Box => 0,
            WorkpieceBlankShape.Cylinder => 1,
            WorkpieceBlankShape.Tube => 2,
            WorkpieceBlankShape.HalfTube => 3,
            WorkpieceBlankShape.ImportedMesh => 0,
            _ => 0
        };
    }

    private void UploadSdfFromNative()
    {
        if (!sdfNativeReady)
        {
            return;
        }

        try
        {
            EnsureSdfLinearSamples();
            SdfNativePlugin.sdf_get_data(sdfLinearSamples, sdfLinearSamples.Length);

            if (sdfSamples != null)
            {
                CopyLinearToSdfSamples();
            }

            // Upload to GPU
            if (sdfSampleBuffer != null)
            {
                sdfSampleBuffer.SetData(sdfLinearSamples);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native SDF upload failed: {ex.Message}", this);
        }
    }

    private float ResolveBlankInnerRadiusLocal()
    {
        if (blankShape != WorkpieceBlankShape.Tube && blankShape != WorkpieceBlankShape.HalfTube)
        {
            return 0f;
        }

        float outerRadius = Mathf.Max(
            voxelSize * 0.5f,
            Mathf.Min(LocalSize.x, LocalSize.z) * 0.5f - voxelSize * 0.5f);
        return Mathf.Clamp(blankInnerRadius, 0f, Mathf.Max(0f, outerRadius - voxelSize * 0.5f));
    }

    private int GetCellIndex(int x, int y, int z)
    {
        return x + width * (y + height * z);
    }

    private void DecodeCellIndex(int index, out int x, out int y, out int z)
    {
        int layerSize = width * height;
        z = index / layerSize;
        int layerOffset = index - z * layerSize;
        y = layerOffset / width;
        x = layerOffset - y * width;
    }

    private void BuildBlockyMesh()
    {
        Vector3 min = GetLocalMin();

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int z = 0; z < depth; z++)
                {
                    if (!voxels[x, y, z])
                    {
                        continue;
                    }

                    AddVisibleVoxelFaces(x, y, z, min);
                }
            }
        }
    }

    private void BuildSmoothSdfMesh()
    {
        BuildSmoothSdfMeshRange(0, CellWidth, 0, CellHeight, 0, CellDepth);
    }

    private void BuildSmoothSdfMeshRange(int startX, int endX, int startY, int endY, int startZ, int endZ)
    {
        Vector3[] cornerPositions = new Vector3[8];
        float[] cornerValues = new float[8];

        for (int x = startX; x < endX; x++)
        {
            for (int y = startY; y < endY; y++)
            {
                for (int z = startZ; z < endZ; z++)
                {
                    float minValue = float.PositiveInfinity;
                    float maxValue = float.NegativeInfinity;

                    for (int corner = 0; corner < 8; corner++)
                    {
                        int sx = x + CubeCorners[corner, 0];
                        int sy = y + CubeCorners[corner, 1];
                        int sz = z + CubeCorners[corner, 2];

                        float value = sdfSamples[sx, sy, sz];
                        cornerValues[corner] = value;
                        cornerPositions[corner] = GetSamplePointLocal(sx, sy, sz);
                        minValue = Mathf.Min(minValue, value);
                        maxValue = Mathf.Max(maxValue, value);
                    }

                    if (minValue > IsoLevel || maxValue < IsoLevel)
                    {
                        continue;
                    }

                    for (int tetra = 0; tetra < 6; tetra++)
                    {
                        PolygonizeTetrahedron(tetra, cornerPositions, cornerValues);
                    }
                }
            }
        }
    }

    private IEnumerator BuildSmoothSdfMeshCoroutine()
    {
        int cellWidth = SampleWidth - 1;
        int cellHeight = SampleHeight - 1;
        int cellDepth = SampleDepth - 1;
        int processedCells = 0;

        Vector3[] cornerPositions = new Vector3[8];
        float[] cornerValues = new float[8];

        for (int x = 0; x < cellWidth; x++)
        {
            for (int y = 0; y < cellHeight; y++)
            {
                for (int z = 0; z < cellDepth; z++)
                {
                    float minValue = float.PositiveInfinity;
                    float maxValue = float.NegativeInfinity;

                    for (int corner = 0; corner < 8; corner++)
                    {
                        int sx = x + CubeCorners[corner, 0];
                        int sy = y + CubeCorners[corner, 1];
                        int sz = z + CubeCorners[corner, 2];

                        float value = sdfSamples[sx, sy, sz];
                        cornerValues[corner] = value;
                        cornerPositions[corner] = GetSamplePointLocal(sx, sy, sz);
                        minValue = Mathf.Min(minValue, value);
                        maxValue = Mathf.Max(maxValue, value);
                    }

                    if (minValue <= IsoLevel && maxValue >= IsoLevel)
                    {
                        for (int tetra = 0; tetra < 6; tetra++)
                        {
                            PolygonizeTetrahedron(tetra, cornerPositions, cornerValues);
                        }
                    }

                    processedCells++;
                    if (processedCells >= smoothRebuildCellsPerFrame)
                    {
                        processedCells = 0;
                        yield return null;
                    }
                }
            }
        }
    }

    private void PolygonizeTetrahedron(int tetraIndex, Vector3[] cubePositions, float[] cubeValues)
    {
        int insideCount = 0;
        for (int i = 0; i < 4; i++)
        {
            int corner = Tetrahedra[tetraIndex, i];
            tetraPositions[i] = cubePositions[corner];
            tetraValues[i] = cubeValues[corner];
            tetraInside[i] = tetraValues[i] <= IsoLevel;

            if (tetraInside[i])
            {
                insideCount++;
            }
        }

        if (insideCount == 0 || insideCount == 4)
        {
            return;
        }

        if (insideCount == 1 || insideCount == 3)
        {
            int singleIndex = FindSingleIndex(tetraInside, insideCount == 1);
            int a = -1;
            int b = -1;
            int c = -1;
            int write = 0;

            for (int i = 0; i < 4; i++)
            {
                if (i == singleIndex)
                {
                    continue;
                }

                if (write == 0)
                {
                    a = i;
                }
                else if (write == 1)
                {
                    b = i;
                }
                else
                {
                    c = i;
                }

                write++;
            }

            AddSmoothTriangle(
                InterpolateIso(tetraPositions[singleIndex], tetraPositions[a], tetraValues[singleIndex], tetraValues[a]),
                InterpolateIso(tetraPositions[singleIndex], tetraPositions[b], tetraValues[singleIndex], tetraValues[b]),
                InterpolateIso(tetraPositions[singleIndex], tetraPositions[c], tetraValues[singleIndex], tetraValues[c]));
            return;
        }

        int insideA = -1;
        int insideB = -1;
        int outsideA = -1;
        int outsideB = -1;

        for (int i = 0; i < 4; i++)
        {
            if (tetraInside[i])
            {
                if (insideA < 0)
                {
                    insideA = i;
                }
                else
                {
                    insideB = i;
                }
            }
            else
            {
                if (outsideA < 0)
                {
                    outsideA = i;
                }
                else
                {
                    outsideB = i;
                }
            }
        }

        Vector3 p0 = InterpolateIso(tetraPositions[insideA], tetraPositions[outsideA], tetraValues[insideA], tetraValues[outsideA]);
        Vector3 p1 = InterpolateIso(tetraPositions[insideA], tetraPositions[outsideB], tetraValues[insideA], tetraValues[outsideB]);
        Vector3 p2 = InterpolateIso(tetraPositions[insideB], tetraPositions[outsideA], tetraValues[insideB], tetraValues[outsideA]);
        Vector3 p3 = InterpolateIso(tetraPositions[insideB], tetraPositions[outsideB], tetraValues[insideB], tetraValues[outsideB]);

        AddSmoothTriangle(p0, p2, p1);
        AddSmoothTriangle(p1, p2, p3);
    }

    private static int FindSingleIndex(bool[] inside, bool findInside)
    {
        for (int i = 0; i < inside.Length; i++)
        {
            if (inside[i] == findInside)
            {
                return i;
            }
        }

        return 0;
    }

    private static Vector3 InterpolateIso(Vector3 a, Vector3 b, float valueA, float valueB)
    {
        float delta = valueB - valueA;
        if (Mathf.Abs(delta) < 0.000001f)
        {
            return (a + b) * 0.5f;
        }

        float t = Mathf.Clamp01((IsoLevel - valueA) / delta);
        return Vector3.LerpUnclamped(a, b, t);
    }

    private void AddSmoothTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 centroidNormal = GetSdfNormal((a + b + c) / 3f);
        Vector3 faceNormal = Vector3.Cross(b - a, c - a);

        if (Vector3.Dot(faceNormal, centroidNormal) < 0f)
        {
            Vector3 temp = b;
            b = c;
            c = temp;
        }

        int startIndex = vertices.Count;

        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);

        triangles.Add(startIndex);
        triangles.Add(startIndex + 1);
        triangles.Add(startIndex + 2);

        normals.Add(GetSdfNormal(a));
        normals.Add(GetSdfNormal(b));
        normals.Add(GetSdfNormal(c));

        uvs.Add(ProjectUv(a));
        uvs.Add(ProjectUv(b));
        uvs.Add(ProjectUv(c));
    }

    private void AddVisibleVoxelFaces(int x, int y, int z, Vector3 gridMin)
    {
        Vector3 voxelMin = gridMin + new Vector3(x, y, z) * voxelSize;
        Vector3 voxelMax = voxelMin + Vector3.one * voxelSize;

        if (!IsInside(x + 1, y, z) || !voxels[x + 1, y, z])
        {
            AddVoxelFace(
                new Vector3(voxelMax.x, voxelMin.y, voxelMin.z),
                new Vector3(voxelMax.x, voxelMax.y, voxelMin.z),
                new Vector3(voxelMax.x, voxelMax.y, voxelMax.z),
                new Vector3(voxelMax.x, voxelMin.y, voxelMax.z),
                Vector3.right);
        }

        if (!IsInside(x - 1, y, z) || !voxels[x - 1, y, z])
        {
            AddVoxelFace(
                new Vector3(voxelMin.x, voxelMin.y, voxelMax.z),
                new Vector3(voxelMin.x, voxelMax.y, voxelMax.z),
                new Vector3(voxelMin.x, voxelMax.y, voxelMin.z),
                new Vector3(voxelMin.x, voxelMin.y, voxelMin.z),
                Vector3.left);
        }

        if (!IsInside(x, y + 1, z) || !voxels[x, y + 1, z])
        {
            AddVoxelFace(
                new Vector3(voxelMin.x, voxelMax.y, voxelMax.z),
                new Vector3(voxelMax.x, voxelMax.y, voxelMax.z),
                new Vector3(voxelMax.x, voxelMax.y, voxelMin.z),
                new Vector3(voxelMin.x, voxelMax.y, voxelMin.z),
                Vector3.up);
        }

        if (!IsInside(x, y - 1, z) || !voxels[x, y - 1, z])
        {
            AddVoxelFace(
                new Vector3(voxelMin.x, voxelMin.y, voxelMin.z),
                new Vector3(voxelMax.x, voxelMin.y, voxelMin.z),
                new Vector3(voxelMax.x, voxelMin.y, voxelMax.z),
                new Vector3(voxelMin.x, voxelMin.y, voxelMax.z),
                Vector3.down);
        }

        if (!IsInside(x, y, z + 1) || !voxels[x, y, z + 1])
        {
            AddVoxelFace(
                new Vector3(voxelMin.x, voxelMin.y, voxelMax.z),
                new Vector3(voxelMax.x, voxelMin.y, voxelMax.z),
                new Vector3(voxelMax.x, voxelMax.y, voxelMax.z),
                new Vector3(voxelMin.x, voxelMax.y, voxelMax.z),
                Vector3.forward);
        }

        if (!IsInside(x, y, z - 1) || !voxels[x, y, z - 1])
        {
            AddVoxelFace(
                new Vector3(voxelMax.x, voxelMin.y, voxelMin.z),
                new Vector3(voxelMin.x, voxelMin.y, voxelMin.z),
                new Vector3(voxelMin.x, voxelMax.y, voxelMin.z),
                new Vector3(voxelMax.x, voxelMax.y, voxelMin.z),
                Vector3.back);
        }
    }

    private void AddVoxelFace(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        int startIndex = vertices.Count;

        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
        vertices.Add(d);

        triangles.Add(startIndex);
        triangles.Add(startIndex + 1);
        triangles.Add(startIndex + 2);
        triangles.Add(startIndex);
        triangles.Add(startIndex + 2);
        triangles.Add(startIndex + 3);

        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);
        normals.Add(normal);

        uvs.Add(new Vector2(0f, 0f));
        uvs.Add(new Vector2(1f, 0f));
        uvs.Add(new Vector2(1f, 1f));
        uvs.Add(new Vector2(0f, 1f));
    }

    private void EnsureComponents()
    {
        if (meshFilter == null)
        {
            meshFilter = GetComponent<MeshFilter>();
        }

        if (meshCollider == null)
        {
            meshCollider = GetComponent<MeshCollider>();
        }

        if (meshRenderer == null)
        {
            meshRenderer = GetComponent<MeshRenderer>();
        }

        if (runtimeMesh == null)
        {
            runtimeMesh = new Mesh
            {
                name = "Runtime Voxel Workpiece",
                indexFormat = IndexFormat.UInt32
            };
            runtimeMesh.MarkDynamic();
        }
    }

    private float SampleSdf(Vector3 localPoint)
    {
        Vector3 grid = (localPoint - GetLocalMin()) / voxelSize + Vector3.one;

        int x0 = ClampIndex(Mathf.FloorToInt(grid.x), SampleWidth - 1);
        int y0 = ClampIndex(Mathf.FloorToInt(grid.y), SampleHeight - 1);
        int z0 = ClampIndex(Mathf.FloorToInt(grid.z), SampleDepth - 1);
        int x1 = Mathf.Min(x0 + 1, SampleWidth - 1);
        int y1 = Mathf.Min(y0 + 1, SampleHeight - 1);
        int z1 = Mathf.Min(z0 + 1, SampleDepth - 1);

        float tx = Mathf.Clamp01(grid.x - x0);
        float ty = Mathf.Clamp01(grid.y - y0);
        float tz = Mathf.Clamp01(grid.z - z0);

        float c00 = Mathf.Lerp(sdfSamples[x0, y0, z0], sdfSamples[x1, y0, z0], tx);
        float c10 = Mathf.Lerp(sdfSamples[x0, y1, z0], sdfSamples[x1, y1, z0], tx);
        float c01 = Mathf.Lerp(sdfSamples[x0, y0, z1], sdfSamples[x1, y0, z1], tx);
        float c11 = Mathf.Lerp(sdfSamples[x0, y1, z1], sdfSamples[x1, y1, z1], tx);
        float c0 = Mathf.Lerp(c00, c10, ty);
        float c1 = Mathf.Lerp(c01, c11, ty);
        return Mathf.Lerp(c0, c1, tz);
    }

    private Vector3 GetSdfNormal(Vector3 localPoint)
    {
        float e = voxelSize;
        float dx = SampleSdf(localPoint + Vector3.right * e) - SampleSdf(localPoint - Vector3.right * e);
        float dy = SampleSdf(localPoint + Vector3.up * e) - SampleSdf(localPoint - Vector3.up * e);
        float dz = SampleSdf(localPoint + Vector3.forward * e) - SampleSdf(localPoint - Vector3.forward * e);

        Vector3 normal = new Vector3(dx, dy, dz);
        if (normal.sqrMagnitude < 0.000001f)
        {
            return Vector3.up;
        }

        return normal.normalized;
    }

    private Vector2 ProjectUv(Vector3 localPoint)
    {
        return new Vector2(localPoint.x / voxelSize, localPoint.z / voxelSize);
    }

    private Vector3 GetLocalMin()
    {
        return -LocalSize * 0.5f;
    }

    private Vector3 GetVoxelCenterLocal(int x, int y, int z, Vector3 gridMin)
    {
        return gridMin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * voxelSize;
    }

    private Vector3 GetSamplePointLocal(int x, int y, int z)
    {
        return GetLocalMin() + new Vector3(x - 1, y - 1, z - 1) * voxelSize;
    }

    private bool IsInside(int x, int y, int z)
    {
        return x >= 0 && x < width && y >= 0 && y < height && z >= 0 && z < depth;
    }

    private static int ClampIndex(int index, int length)
    {
        return Mathf.Clamp(index, 0, length - 1);
    }

    private float WorldDistanceToLocalDistance(float worldDistance)
    {
        Vector3 scale = transform.lossyScale;
        float minScale = Mathf.Max(0.0001f, Mathf.Min(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        return worldDistance / minScale;
    }
}
