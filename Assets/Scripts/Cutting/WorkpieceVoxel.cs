using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public enum WorkpieceSurfaceMode
{
    BlockyVoxels,
    SmoothSdf
}

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public sealed class WorkpieceVoxel : MonoBehaviour
{
    public const int MaxCutterProfileRadiusSamples = 32;
    public const int MaxCutterAngularProfileAxialSamples = 16;
    public const int MaxCutterAngularProfileAngleSamples = 32;
    public const int MaxCutterAngularProfileSamples =
        MaxCutterAngularProfileAxialSamples * MaxCutterAngularProfileAngleSamples;

    [Header("Voxel Grid")]
    [Min(1)] public int width = 116;
    [Min(1)] public int height = 50;
    [Min(1)] public int depth = 77;
    [Min(0.001f)] public float voxelSize = 0.025f;
    [Min(100000)] public int maxAllocatedSdfSamples = 12000000;

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

    [Header("Detached Material")]
    public bool removeDetachedParts = true;
    [Min(0.01f)] public float detachedCleanupInterval = 0.08f;
    public bool useGpuDetachedCleanup;
    [Min(0.25f)] public float gpuDetachedAirValueVoxels = 1.5f;
    [Min(10000)] public int maxGlobalConnectivityCells = 3000000;
    [Min(1)] public int maxGpuDetachedRemovalBoxes = 256;
    [Range(0.1f, 1f)] public float globalConnectivityFeatureScale = 0.25f;

    [Header("GPU SDF Cutting")]
    public bool useGpuSdfCutting;
    public ComputeShader sdfCutCompute;
    [Min(2)] public int profileSegmentCount = 6;

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
    [Min(16)] public int maxGpuVisualCutOperations = 512;
    public Color gpuSurfaceColor = new Color(0.72f, 0.76f, 0.78f, 1f);

    [Header("Runtime")]
    public bool initializeOnStart = true;
    public bool updateCollider;

    private const float IsoLevel = 0f;
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
    private ComputeBuffer sdfChangedCounterBuffer;
    private float[] sdfLinearSamples;
    private readonly int[] sdfChangedCounterData = new int[1];
    private int sdfCutKernel = -1;
    private int sdfProfileCutterKernel = -1;
    private int sdfInitKernel = -1;
    private int sdfRemoveUnsupportedColumnsKernel = -1;
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
    private ComputeBuffer gpuDetachedRemovalBuffer;
    private int gpuDetachedRemovalBufferCapacity;
    private bool gpuDetachedRemovalsDirty;
    private ComputeBuffer gpuDetachedVoxelMaskBuffer;
    private int gpuDetachedVoxelMaskCapacity;
    private Vector3Int gpuDetachedVoxelMaskSize;
    private Vector3 gpuDetachedVoxelMaskMin;
    private Vector3 gpuDetachedVoxelMaskStep = Vector3.one;
    private float gpuDetachedVoxelMaskAirValue;
    private int chunkCountX;
    private int chunkCountY;
    private int chunkCountZ;
    private int cachedChunkSize;
    private int cachedCellWidth;
    private int cachedCellHeight;
    private int cachedCellDepth;
    private float nextDetachedCleanupTime;
    private bool sdfNativeReady;
    private float nextNativeConnectivityCheckTime;
    private bool nativeConnectivityDirty;
    private bool nativeConnectivityInFlight;
    private bool globalVisualConnectivityDirty;
    private float lastGlobalVisualCutTime;

    private readonly List<Vector3> vertices = new List<Vector3>(65536);
    private readonly List<int> triangles = new List<int>(65536);
    private readonly List<Vector3> normals = new List<Vector3>(65536);
    private readonly List<Vector2> uvs = new List<Vector2>(65536);
    private readonly Vector3[] tetraPositions = new Vector3[4];
    private readonly float[] tetraValues = new float[4];
    private readonly bool[] tetraInside = new bool[4];
    private bool[] connectivitySolid;
    private int[] connectivityLabels;
    private readonly Queue<int> connectivityQueue = new Queue<int>(65536);
    private readonly List<int> componentSizes = new List<int>(128);
    private readonly List<GpuVisualCutOperation> gpuVisualCutOperations = new List<GpuVisualCutOperation>(64);
    private readonly List<GpuDetachedRemovalBox> gpuDetachedRemovalBoxes = new List<GpuDetachedRemovalBox>(16);
    private readonly float[] cutterProfileRadiusSamples = new float[MaxCutterProfileRadiusSamples];
    private readonly float[] cutterAngularProfileMinRadiusSamples = new float[MaxCutterAngularProfileSamples];
    private readonly float[] cutterAngularProfileMaxRadiusSamples = new float[MaxCutterAngularProfileSamples];
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct GpuDetachedRemovalBox
    {
        public Vector4 center;
        public Vector4 size;
    }

    private struct GlobalConnectivityComponent
    {
        public int label;
        public int count;
        public int minX;
        public int minY;
        public int minZ;
        public int maxX;
        public int maxY;
        public int maxZ;
        public bool touchesSupportedBoundary;
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
        get { return voxels != null && (sdfSamples != null || (UsesGpuSurfaceRendering && sdfGpuReady)); }
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
        get { return useGpuSdfCutting || UsesGpuSurfaceRendering; }
    }

    private bool HasAngularCutterProfile
    {
        get
        {
            return cutterAngularProfileAxialSampleCount >= 2 &&
                   cutterAngularProfileAngleSampleCount >= 3;
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
        TryCleanupGlobalVisualIslandsIfDue();
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
    }

    private void OnValidate()
    {
        width = Mathf.Max(1, width);
        height = Mathf.Max(1, height);
        depth = Mathf.Max(1, depth);
        voxelSize = Mathf.Max(0.001f, voxelSize);
        maxAllocatedSdfSamples = Mathf.Max(100000, maxAllocatedSdfSamples);
        smoothRebuildCellsPerFrame = Mathf.Max(100, smoothRebuildCellsPerFrame);
        chunkSize = Mathf.Max(2, chunkSize);
        chunkRebuildsPerFrame = Mathf.Max(1, chunkRebuildsPerFrame);
        immediateChunkRebuildLimit = Mathf.Max(1, immediateChunkRebuildLimit);
        detachedCleanupInterval = Mathf.Max(0.01f, detachedCleanupInterval);
        maxGlobalConnectivityCells = Mathf.Max(10000, maxGlobalConnectivityCells);
        maxGpuDetachedRemovalBoxes = Mathf.Max(1, maxGpuDetachedRemovalBoxes);
        globalConnectivityFeatureScale = Mathf.Clamp(globalConnectivityFeatureScale, 0.1f, 1f);
        maxGpuTriangles = Mathf.Max(10000, maxGpuTriangles);
        gpuRaymarchMaxSteps = Mathf.Max(16, gpuRaymarchMaxSteps);
        gpuRaymarchStepScale = Mathf.Clamp(gpuRaymarchStepScale, 0.25f, 2f);
        maxGpuVisualCutOperations = Mathf.Max(16, maxGpuVisualCutOperations);
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
        cutterProfileRadiusSampleCount = 0;
        if (normalizedRadiusSamples == null || sampleCount < 2)
        {
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
            return;
        }

        cutterProfileRadiusSampleCount = count;
    }

    public void SetAngularProfileRadiusSamples(
        float[] normalizedMinRadiusSamples,
        float[] normalizedMaxRadiusSamples,
        int axialSampleCount,
        int angleSampleCount)
    {
        cutterAngularProfileAxialSampleCount = 0;
        cutterAngularProfileAngleSampleCount = 0;
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
        gpuDetachedRemovalBoxes.Clear();
        gpuDetachedRemovalsDirty = true;
        globalVisualConnectivityDirty = false;
        lastGlobalVisualCutTime = 0f;
        InitializeBlockVoxels();
        InitializeSdfSamples();
        InitializeGpuSdfResources();
        InitializeNativeSdfPlugin();

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
            RebuildAllChunksImmediate();
            ClearParentMesh();
        }
        else
        {
            DestroyChunks();
            RebuildMeshImmediate();
        }
    }

    public bool CutSphere(Vector3 worldCenter, float worldRadius)
    {
        return CutSphere(worldCenter, worldRadius, true, out _);
    }

    public bool CutSphere(Vector3 worldCenter, float worldRadius, out int removedVoxelCount)
    {
        return CutSphere(worldCenter, worldRadius, true, out removedVoxelCount);
    }

    public bool CutSphere(Vector3 worldCenter, float worldRadius, bool rebuildMesh)
    {
        return CutSphere(worldCenter, worldRadius, rebuildMesh, out _);
    }

    public bool CutSphere(Vector3 worldCenter, float worldRadius, bool rebuildMesh, out int removedVoxelCount)
    {
        return CutSweptSphere(worldCenter, worldCenter, worldRadius, rebuildMesh, out removedVoxelCount);
    }

    public bool CutSweptSphere(Vector3 worldStart, Vector3 worldEnd, float worldRadius)
    {
        return CutSweptSphere(worldStart, worldEnd, worldRadius, true, out _);
    }

    public bool CutSweptSphere(Vector3 worldStart, Vector3 worldEnd, float worldRadius, bool rebuildMesh)
    {
        return CutSweptSphere(worldStart, worldEnd, worldRadius, rebuildMesh, out _);
    }

    public bool CutSweptSphere(Vector3 worldStart, Vector3 worldEnd, float worldRadius, bool rebuildMesh, out int removedVoxelCount)
    {
        removedVoxelCount = 0;

        if (worldRadius <= 0f)
        {
            return false;
        }

        if (!IsInitialized)
        {
            ResetWorkpiece();
        }

        bool changed = surfaceMode == WorkpieceSurfaceMode.SmoothSdf
            ? CutCapsuleSdf(worldStart, worldEnd, worldRadius, out removedVoxelCount)
            : CutCapsuleVoxels(worldStart, worldEnd, worldRadius, out removedVoxelCount);

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
            ? CutProfileCutterSdf(
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

        if (surfaceMode != WorkpieceSurfaceMode.SmoothSdf || !removeDetachedParts)
        {
            return;
        }

        if (UsesGpuSurfaceRendering)
        {
            if (!useExpandedDisplayBounds)
            {
                TryRemoveUnsupportedSdfColumnsGpuNoReadback();
            }

            // Use native plugin for GPU mode
            if (sdfNativeReady)
            {
                SdfNativePlugin.sdf_check_connectivity();
                // Spin-wait for result (ContextMenu is manual, OK to block briefly)
                int timeout = 500;
                while (SdfNativePlugin.sdf_is_connectivity_ready() == 0 && timeout > 0)
                {
                    System.Threading.Thread.Sleep(1);
                    timeout--;
                }
                if (SdfNativePlugin.sdf_is_connectivity_ready() != 0)
                {
                    if (SdfNativePlugin.sdf_get_connectivity_result() != 0)
                    {
                        int removed = SdfNativePlugin.sdf_apply_removal();
                        if (removed > 0)
                        {
                            UploadSdfFromNative();
                            Debug.Log($"Native plugin removed {removed} island cells.", this);
                        }
                    }
                    else
                    {
                        SdfNativePlugin.sdf_consume_connectivity_result();
                    }

                    nativeConnectivityInFlight = false;
                    nativeConnectivityDirty = false;
                }
            }
            return;
        }

        if (sdfSamples == null)
        {
            return;
        }

        if (RemoveDetachedSdfComponents() > 0)
        {
            RebuildMesh();
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

        if (meshRenderer != null)
        {
            meshRenderer.enabled = true;
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

        for (int x = 0; x < SampleWidth; x++)
        {
            for (int y = 0; y < SampleHeight; y++)
            {
                for (int z = 0; z < SampleDepth; z++)
                {
                    sdfSamples[x, y, z] = BoxSdf(GetSamplePointLocal(x, y, z));
                }
            }
        }
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

        if (!UsesGpuSurfaceRendering && sdfSamples == null)
        {
            return false;
        }

        if (!SystemInfo.supportsComputeShaders)
        {
            return false;
        }

        ComputeShader shader = GetSdfCutCompute();
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
            sdfCutCompute = shader;
            sdfCutKernel = sdfCutCompute.FindKernel("CutCapsuleSdf");
            sdfProfileCutterKernel = sdfCutCompute.FindKernel("CutProfileCutterSdf");
            sdfInitKernel = sdfCutCompute.FindKernel("InitializeBoxSdf");
            sdfRemoveUnsupportedColumnsKernel = sdfCutCompute.FindKernel("RemoveUnsupportedColumnsSdf");

            sdfSampleBuffer = new ComputeBuffer(sampleCount, sizeof(float));
            if (UsesGpuSurfaceRendering)
            {
                DispatchGpuSdfInitialization();
            }
            else
            {
                EnsureSdfLinearSamples();
                CopySdfSamplesToLinear();
                sdfSampleBuffer.SetData(sdfLinearSamples);
            }

            sdfChangedCounterBuffer = new ComputeBuffer(1, sizeof(int));
            sdfGpuReady = true;
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"GPU SDF cutting disabled: {exception.Message}", this);
            sdfGpuUnavailable = true;
            ReleaseGpuSdfResources();
            return false;
        }
    }

    private ComputeShader GetSdfCutCompute()
    {
        if (sdfCutCompute != null)
        {
            return sdfCutCompute;
        }

        sdfCutCompute = Resources.Load<ComputeShader>("Cutting/WorkpieceSdfCut");
        return sdfCutCompute;
    }

    private void ReleaseGpuSdfResources()
    {
        if (sdfSampleBuffer != null)
        {
            sdfSampleBuffer.Release();
            sdfSampleBuffer = null;
        }

        if (sdfChangedCounterBuffer != null)
        {
            sdfChangedCounterBuffer.Release();
            sdfChangedCounterBuffer = null;
        }

        sdfGpuReady = false;
        sdfCutKernel = -1;
        sdfProfileCutterKernel = -1;
        sdfInitKernel = -1;
        sdfRemoveUnsupportedColumnsKernel = -1;
    }

    private void DispatchGpuSdfInitialization()
    {
        sdfCutCompute.SetBuffer(sdfInitKernel, "_SdfSamples", sdfSampleBuffer);
        sdfCutCompute.SetInts("_SampleSize", SampleWidth, SampleHeight, SampleDepth);
        sdfCutCompute.SetVector("_GridMin", GetLocalMin());
        sdfCutCompute.SetVector("_LocalSize", LocalSize);
        sdfCutCompute.SetFloat("_VoxelSize", voxelSize);

        int threadGroupsX = Mathf.CeilToInt(SampleWidth / 8f);
        int threadGroupsY = Mathf.CeilToInt(SampleHeight / 8f);
        int threadGroupsZ = Mathf.CeilToInt(SampleDepth / 4f);
        sdfCutCompute.Dispatch(sdfInitKernel, threadGroupsX, threadGroupsY, threadGroupsZ);
    }

    private bool TryRemoveUnsupportedSdfColumnsGpuNoReadback()
    {
        if (!useGpuDetachedCleanup || !EnsureGpuSdfResources() || sdfRemoveUnsupportedColumnsKernel < 0)
        {
            return false;
        }

        try
        {
            sdfChangedCounterData[0] = 0;
            sdfChangedCounterBuffer.SetData(sdfChangedCounterData);

            sdfCutCompute.SetBuffer(sdfRemoveUnsupportedColumnsKernel, "_SdfSamples", sdfSampleBuffer);
            sdfCutCompute.SetBuffer(sdfRemoveUnsupportedColumnsKernel, "_ChangedCounter", sdfChangedCounterBuffer);
            sdfCutCompute.SetInts("_SampleSize", SampleWidth, SampleHeight, SampleDepth);
            sdfCutCompute.SetVector("_GridMin", GetLocalMin());
            sdfCutCompute.SetVector("_LocalSize", LocalSize);
            sdfCutCompute.SetFloat("_VoxelSize", voxelSize);
            sdfCutCompute.SetFloat("_IsoLevel", IsoLevel);
            sdfCutCompute.SetFloat("_DetachedAirValue", Mathf.Max(voxelSize * 0.25f, voxelSize * gpuDetachedAirValueVoxels));

            int threadGroupsX = Mathf.CeilToInt(SampleWidth / 8f);
            int threadGroupsZ = Mathf.CeilToInt(SampleDepth / 8f);
            sdfCutCompute.Dispatch(sdfRemoveUnsupportedColumnsKernel, threadGroupsX, 1, threadGroupsZ);
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"GPU detached cleanup failed: {exception.Message}", this);
            sdfGpuUnavailable = true;
            ReleaseGpuSdfResources();
            return false;
        }
    }

    private void TryCleanupDetachedPartsGpuIfDue()
    {
        if (!removeDetachedParts || !useGpuDetachedCleanup ||
            !UsesGpuSurfaceRendering || useExpandedDisplayBounds)
        {
            return;
        }

        if (Application.isPlaying && Time.time < nextDetachedCleanupTime)
        {
            return;
        }

        nextDetachedCleanupTime = Application.isPlaying
            ? Time.time + Mathf.Max(0.01f, detachedCleanupInterval)
            : 0f;
        TryRemoveUnsupportedSdfColumnsGpuNoReadback();
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

        if (gpuDetachedRemovalBuffer != null)
        {
            gpuDetachedRemovalBuffer.Release();
            gpuDetachedRemovalBuffer = null;
        }

        if (gpuDetachedVoxelMaskBuffer != null)
        {
            gpuDetachedVoxelMaskBuffer.Release();
            gpuDetachedVoxelMaskBuffer = null;
        }

        gpuVisualCutBufferCapacity = 0;
        gpuVisualCutsDirty = true;
        gpuDetachedRemovalBufferCapacity = 0;
        gpuDetachedRemovalsDirty = true;
        gpuDetachedVoxelMaskCapacity = 0;
        gpuDetachedVoxelMaskSize = Vector3Int.zero;
        gpuDetachedVoxelMaskMin = Vector3.zero;
        gpuDetachedVoxelMaskStep = Vector3.one;
        gpuDetachedVoxelMaskAirValue = 0f;
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
        runtimeGpuSurfaceMaterial.SetInt("_VisualCutOperationCount", gpuVisualCutOperations.Count);
        EnsureGpuDetachedRemovalBuffer();
        runtimeGpuSurfaceMaterial.SetBuffer("_DetachedRemovalBoxes", gpuDetachedRemovalBuffer);
        runtimeGpuSurfaceMaterial.SetInt("_DetachedRemovalBoxCount", gpuDetachedRemovalBoxes.Count);
        EnsureGpuDetachedVoxelMaskBuffer();
        runtimeGpuSurfaceMaterial.SetBuffer("_DetachedVoxelMask", gpuDetachedVoxelMaskBuffer);
        runtimeGpuSurfaceMaterial.SetVector(
            "_DetachedVoxelMaskSize",
            new Vector4(gpuDetachedVoxelMaskSize.x, gpuDetachedVoxelMaskSize.y, gpuDetachedVoxelMaskSize.z, 0f));
        runtimeGpuSurfaceMaterial.SetVector("_DetachedVoxelMaskMin", gpuDetachedVoxelMaskMin);
        runtimeGpuSurfaceMaterial.SetVector("_DetachedVoxelMaskStep", gpuDetachedVoxelMaskStep);
        runtimeGpuSurfaceMaterial.SetFloat("_DetachedVoxelMaskAirValue", gpuDetachedVoxelMaskAirValue);
        runtimeGpuSurfaceMaterial.SetInt("_ProfileSegmentCount", profileSegmentCount);
        runtimeGpuSurfaceMaterial.SetInt("_AngularProfileAxialSampleCount", cutterAngularProfileAxialSampleCount);
        runtimeGpuSurfaceMaterial.SetInt("_AngularProfileAngleSampleCount", cutterAngularProfileAngleSampleCount);
        runtimeGpuSurfaceMaterial.SetFloatArray("_AngularProfileMinRadiusSamples", cutterAngularProfileMinRadiusSamples);
        runtimeGpuSurfaceMaterial.SetFloatArray("_AngularProfileMaxRadiusSamples", cutterAngularProfileMaxRadiusSamples);
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

    private bool CutCapsuleVoxels(Vector3 worldStart, Vector3 worldEnd, float worldRadius, out int removedVoxelCount)
    {
        removedVoxelCount = 0;

        Vector3 localStart = transform.InverseTransformPoint(worldStart);
        Vector3 localEnd = transform.InverseTransformPoint(worldEnd);
        float localRadius = WorldDistanceToLocalDistance(worldRadius);
        float localRadiusSqr = localRadius * localRadius;

        Vector3 min = GetLocalMin();
        Vector3 segmentMin = Vector3.Min(localStart, localEnd);
        Vector3 segmentMax = Vector3.Max(localStart, localEnd);
        Vector3 localMin = segmentMin - Vector3.one * localRadius;
        Vector3 localMax = segmentMax + Vector3.one * localRadius;

        int minX = ClampIndex(Mathf.FloorToInt((localMin.x - min.x) / voxelSize), width);
        int maxX = ClampIndex(Mathf.FloorToInt((localMax.x - min.x) / voxelSize), width);
        int minY = ClampIndex(Mathf.FloorToInt((localMin.y - min.y) / voxelSize), height);
        int maxY = ClampIndex(Mathf.FloorToInt((localMax.y - min.y) / voxelSize), height);
        int minZ = ClampIndex(Mathf.FloorToInt((localMin.z - min.z) / voxelSize), depth);
        int maxZ = ClampIndex(Mathf.FloorToInt((localMax.z - min.z) / voxelSize), depth);

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    if (!voxels[x, y, z])
                    {
                        continue;
                    }

                    Vector3 voxelCenter = GetVoxelCenterLocal(x, y, z, min);
                    Vector3 closest = ClosestPointOnSegment(localStart, localEnd, voxelCenter);
                    if ((voxelCenter - closest).sqrMagnitude <= localRadiusSqr)
                    {
                        voxels[x, y, z] = false;
                        removedVoxelCount++;
                    }
                }
            }
        }

        return removedVoxelCount > 0;
    }

    private bool CutCapsuleSdf(Vector3 worldStart, Vector3 worldEnd, float worldRadius, out int changedSampleCount)
    {
        changedSampleCount = 0;

        Vector3 localStart = transform.InverseTransformPoint(worldStart);
        Vector3 localEnd = transform.InverseTransformPoint(worldEnd);
        float localRadius = WorldDistanceToLocalDistance(worldRadius);
        float updateBand = voxelSize * 2f;
        float affectedRadius = localRadius + updateBand;

        Vector3 min = GetLocalMin();
        Vector3 segmentMin = Vector3.Min(localStart, localEnd);
        Vector3 segmentMax = Vector3.Max(localStart, localEnd);
        Vector3 localMin = segmentMin - Vector3.one * affectedRadius;
        Vector3 localMax = segmentMax + Vector3.one * affectedRadius;

        int minX = ClampIndex(Mathf.FloorToInt((localMin.x - min.x) / voxelSize) + 1, SampleWidth);
        int maxX = ClampIndex(Mathf.CeilToInt((localMax.x - min.x) / voxelSize) + 1, SampleWidth);
        int minY = ClampIndex(Mathf.FloorToInt((localMin.y - min.y) / voxelSize) + 1, SampleHeight);
        int maxY = ClampIndex(Mathf.CeilToInt((localMax.y - min.y) / voxelSize) + 1, SampleHeight);
        int minZ = ClampIndex(Mathf.FloorToInt((localMin.z - min.z) / voxelSize) + 1, SampleDepth);
        int maxZ = ClampIndex(Mathf.CeilToInt((localMax.z - min.z) / voxelSize) + 1, SampleDepth);

        if (UsesGpuSurfaceRendering)
        {
            if (TryCutCapsuleSdfGpuNoReadback(localStart, localEnd, localRadius, affectedRadius, minX, maxX, minY, maxY, minZ, maxZ))
            {
                NativeCutCapsule(localStart, localEnd, localRadius, minX, maxX, minY, maxY, minZ, maxZ);
                TryCleanupDetachedPartsGpuIfDue();
                changedSampleCount = 1;
                return true;
            }

            return false;
        }

        Vector3 priorityPoint = localEnd - Vector3.up * localRadius;

        bool gpuUpdated = TryCutCapsuleSdfGpu(
            localStart,
            localEnd,
            localRadius,
            affectedRadius,
            minX,
            maxX,
            minY,
            maxY,
            minZ,
            maxZ,
            out changedSampleCount,
            out priorityPoint);

        if (!gpuUpdated)
        {
            changedSampleCount = CutCapsuleSdfCpu(
                localStart,
                localEnd,
                localRadius,
                affectedRadius,
                minX,
                maxX,
                minY,
                maxY,
                minZ,
                maxZ,
                out priorityPoint);

            if (changedSampleCount > 0)
            {
                UploadSdfSamplesToGpu();
            }
        }

        if (changedSampleCount > 0 && UsesChunkedSmoothMesh)
        {
            MarkDirtyCells(
                Mathf.Max(0, minX - 1),
                Mathf.Min(CellWidth, maxX + 1),
                Mathf.Max(0, minY - 1),
                Mathf.Min(CellHeight, maxY + 1),
                Mathf.Max(0, minZ - 1),
                Mathf.Min(CellDepth, maxZ + 1),
                priorityPoint);
        }

        if (changedSampleCount > 0 && ShouldCleanupDetachedParts())
        {
            changedSampleCount += RemoveDetachedSdfComponents();
        }

        return changedSampleCount > 0;
    }

    private bool CutProfileCutterSdf(
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

        Vector3 startTop = localStart + localAxis * localHeight;
        Vector3 endTop = localEnd + localAxis * localHeight;
        Vector3 localMin = Vector3.Min(Vector3.Min(localStart, localEnd), Vector3.Min(startTop, endTop)) - Vector3.one * affectedRadius;
        Vector3 localMax = Vector3.Max(Vector3.Max(localStart, localEnd), Vector3.Max(startTop, endTop)) + Vector3.one * affectedRadius;
        bool visualChanged = RegisterGpuVisualProfileCut(localStart, localEnd, localAxis, localRight, localRadius, localHeight, localMin, localMax);

        Vector3 detailMin = GetLocalMin() - Vector3.one * updateBand;
        Vector3 detailMax = -GetLocalMin() + Vector3.one * updateBand;
        if (!AabbOverlaps(localMin, localMax, detailMin, detailMax))
        {
            changedSampleCount = visualChanged ? 1 : 0;
            return visualChanged;
        }

        Vector3 min = GetLocalMin();
        int minX = ClampIndex(Mathf.FloorToInt((localMin.x - min.x) / voxelSize) + 1, SampleWidth);
        int maxX = ClampIndex(Mathf.CeilToInt((localMax.x - min.x) / voxelSize) + 1, SampleWidth);
        int minY = ClampIndex(Mathf.FloorToInt((localMin.y - min.y) / voxelSize) + 1, SampleHeight);
        int maxY = ClampIndex(Mathf.CeilToInt((localMax.y - min.y) / voxelSize) + 1, SampleHeight);
        int minZ = ClampIndex(Mathf.FloorToInt((localMin.z - min.z) / voxelSize) + 1, SampleDepth);
        int maxZ = ClampIndex(Mathf.CeilToInt((localMax.z - min.z) / voxelSize) + 1, SampleDepth);

        if (TryCutProfileCutterSdfGpuNoReadback(localStart, localEnd, localAxis, localRight, localRadius, localHeight, updateBand, minX, maxX, minY, maxY, minZ, maxZ))
        {
            if (HasAngularCutterProfile)
            {
                ShutdownNativeSdfPlugin();
            }
            else
            {
                NativeCutProfileCutter(localStart, localEnd, localAxis, localRadius, localHeight, updateBand, minX, maxX, minY, maxY, minZ, maxZ);
            }

            TryCleanupDetachedPartsGpuIfDue();
            changedSampleCount = 1;
            return true;
        }

        return false;
    }

    private bool RegisterGpuVisualProfileCut(
        Vector3 localStart,
        Vector3 localEnd,
        Vector3 localAxis,
        Vector3 localRight,
        float localRadius,
        float localHeight,
        Vector3 operationMin,
        Vector3 operationMax)
    {
        if (!UsesGpuSurfaceRendering || !useExpandedDisplayBounds)
        {
            return false;
        }

        Vector3 displayHalf = DisplaySize * 0.5f;
        Vector3 displayMin = DisplayCenter - displayHalf;
        Vector3 displayMax = DisplayCenter + displayHalf;
        if (!AabbOverlaps(operationMin, operationMax, displayMin, displayMax))
        {
            return false;
        }

        GpuVisualCutOperation operation = new GpuVisualCutOperation
        {
            startRadius = new Vector4(localStart.x, localStart.y, localStart.z, localRadius),
            endHeight = new Vector4(localEnd.x, localEnd.y, localEnd.z, localHeight),
            axis = new Vector4(localAxis.x, localAxis.y, localAxis.z, 0f),
            profileMeta = new Vector4(
                cutterProfileRadiusSampleCount,
                localRight.x,
                localRight.y,
                localRight.z)
        };
        WriteProfileRadiusSamplesToOperation(ref operation);

        int lastIndex = gpuVisualCutOperations.Count - 1;
        if (lastIndex >= 0 && TryMergeGpuVisualCut(lastIndex, operation, out bool geometryChanged))
        {
            if (geometryChanged)
            {
                gpuVisualCutsDirty = true;
                MarkGlobalVisualConnectivityDirty();
            }
            return true;
        }

        if (gpuVisualCutOperations.Count >= Mathf.Max(16, maxGpuVisualCutOperations))
        {
            return false;
        }

        gpuVisualCutOperations.Add(operation);
        gpuVisualCutsDirty = true;
        MarkGlobalVisualConnectivityDirty();
        return true;
    }

    private void WriteProfileRadiusSamplesToOperation(ref GpuVisualCutOperation operation)
    {
        operation.profileMeta.x = cutterProfileRadiusSampleCount;
        operation.profile0 = PackProfileRadiusSamples(0);
        operation.profile1 = PackProfileRadiusSamples(4);
        operation.profile2 = PackProfileRadiusSamples(8);
        operation.profile3 = PackProfileRadiusSamples(12);
        operation.profile4 = PackProfileRadiusSamples(16);
        operation.profile5 = PackProfileRadiusSamples(20);
        operation.profile6 = PackProfileRadiusSamples(24);
        operation.profile7 = PackProfileRadiusSamples(28);
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

    private void MarkGlobalVisualConnectivityDirty()
    {
        if (!removeDetachedParts || !useExpandedDisplayBounds)
        {
            return;
        }

        globalVisualConnectivityDirty = true;
        lastGlobalVisualCutTime = Application.isPlaying ? Time.time : 0f;
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

    private void EnsureGpuDetachedRemovalBuffer()
    {
        int requiredCapacity = Mathf.NextPowerOfTwo(Mathf.Max(1, gpuDetachedRemovalBoxes.Count));
        if (gpuDetachedRemovalBuffer == null || gpuDetachedRemovalBufferCapacity < requiredCapacity)
        {
            if (gpuDetachedRemovalBuffer != null)
            {
                gpuDetachedRemovalBuffer.Release();
            }

            gpuDetachedRemovalBufferCapacity = requiredCapacity;
            gpuDetachedRemovalBuffer = new ComputeBuffer(gpuDetachedRemovalBufferCapacity, sizeof(float) * 8);
            gpuDetachedRemovalsDirty = true;
        }

        if (!gpuDetachedRemovalsDirty)
        {
            return;
        }

        if (gpuDetachedRemovalBoxes.Count > 0)
        {
            gpuDetachedRemovalBuffer.SetData(
                gpuDetachedRemovalBoxes,
                0,
                0,
                gpuDetachedRemovalBoxes.Count);
        }
        else
        {
            gpuDetachedRemovalBuffer.SetData(new GpuDetachedRemovalBox[1]);
        }

        gpuDetachedRemovalsDirty = false;
    }

    private void EnsureGpuDetachedVoxelMaskBuffer()
    {
        if (gpuDetachedVoxelMaskBuffer != null)
        {
            return;
        }

        gpuDetachedVoxelMaskCapacity = 1;
        gpuDetachedVoxelMaskBuffer = new ComputeBuffer(1, sizeof(float));
        gpuDetachedVoxelMaskBuffer.SetData(new float[1]);
    }

    private void UploadGpuDetachedVoxelMask(
        uint[] mask,
        int cellsX,
        int cellsY,
        int cellsZ,
        Vector3 gridMin,
        Vector3 cellStep)
    {
        int requiredCapacity = Mathf.Max(1, mask != null ? mask.Length : 0);
        if (gpuDetachedVoxelMaskBuffer == null || gpuDetachedVoxelMaskCapacity < requiredCapacity)
        {
            if (gpuDetachedVoxelMaskBuffer != null)
            {
                gpuDetachedVoxelMaskBuffer.Release();
            }

            gpuDetachedVoxelMaskCapacity = requiredCapacity;
            gpuDetachedVoxelMaskBuffer = new ComputeBuffer(requiredCapacity, sizeof(float));
        }

        if (mask != null && mask.Length > 0)
        {
            int removedCellCount = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i] != 0)
                {
                    removedCellCount++;
                }
            }

            // An empty mask must be disabled completely. Leaving a populated
            // grid of negative placeholder values active can still affect the
            // max() CSG field and its numerical normals.
            if (removedCellCount == 0)
            {
                gpuDetachedVoxelMaskBuffer.SetData(new float[1]);
                gpuDetachedVoxelMaskSize = Vector3Int.zero;
                gpuDetachedVoxelMaskMin = Vector3.zero;
                gpuDetachedVoxelMaskStep = Vector3.one;
                gpuDetachedVoxelMaskAirValue = 0f;
                return;
            }

            // Convert the binary connectivity result into a conservative
            // signed distance field. A raw +/- binary value has a discontinuity
            // on its negative side; that discontinuity was leaking into the
            // ray-marched normal and produced the visible "frosted" speckles.
            // A 26-neighbour breadth-first distance is a Chebyshev distance,
            // so using the smallest cell step never over-estimates the true
            // Euclidean distance and remains safe for sphere tracing.
            int[] distance = new int[mask.Length];
            int[] queue = new int[mask.Length];
            for (int i = 0; i < distance.Length; i++)
            {
                distance[i] = -1;
            }

            int queueHead = 0;
            int queueTail = 0;
            int layerStride = cellsX * cellsY;
            for (int z = 0; z < cellsZ; z++)
            {
                for (int y = 0; y < cellsY; y++)
                {
                    for (int x = 0; x < cellsX; x++)
                    {
                        int index = x + cellsX * y + layerStride * z;
                        bool removed = mask[index] != 0;
                        bool boundary =
                            (x > 0 && (mask[index - 1] != 0) != removed) ||
                            (x + 1 < cellsX && (mask[index + 1] != 0) != removed) ||
                            (y > 0 && (mask[index - cellsX] != 0) != removed) ||
                            (y + 1 < cellsY && (mask[index + cellsX] != 0) != removed) ||
                            (z > 0 && (mask[index - layerStride] != 0) != removed) ||
                            (z + 1 < cellsZ && (mask[index + layerStride] != 0) != removed) ||
                            (removed && (x == 0 || x + 1 == cellsX ||
                                         y == 0 || y + 1 == cellsY ||
                                         z == 0 || z + 1 == cellsZ));

                        if (boundary)
                        {
                            distance[index] = 0;
                            queue[queueTail++] = index;
                        }
                    }
                }
            }

            while (queueHead < queueTail)
            {
                int index = queue[queueHead++];
                int z = index / layerStride;
                int remainder = index - z * layerStride;
                int y = remainder / cellsX;
                int x = remainder - y * cellsX;
                int nextDistance = distance[index] + 1;

                for (int dz = -1; dz <= 1; dz++)
                {
                    int nz = z + dz;
                    if (nz < 0 || nz >= cellsZ)
                    {
                        continue;
                    }

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= cellsY)
                        {
                            continue;
                        }

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0 && dz == 0)
                            {
                                continue;
                            }

                            int nx = x + dx;
                            if (nx < 0 || nx >= cellsX)
                            {
                                continue;
                            }

                            int neighbor = nx + cellsX * ny + layerStride * nz;
                            if (distance[neighbor] >= 0)
                            {
                                continue;
                            }

                            distance[neighbor] = nextDistance;
                            queue[queueTail++] = neighbor;
                        }
                    }
                }
            }

            float distanceStep = Mathf.Min(
                cellStep.x,
                Mathf.Min(cellStep.y, cellStep.z));
            float airValue = distanceStep * 0.5f;
            float[] continuousMask = new float[mask.Length];
            for (int i = 0; i < mask.Length; i++)
            {
                float signedDistance = (Mathf.Max(0, distance[i]) + 0.5f) * distanceStep;
                continuousMask[i] = mask[i] != 0 ? signedDistance : -signedDistance;
            }
            gpuDetachedVoxelMaskBuffer.SetData(continuousMask);
            gpuDetachedVoxelMaskSize = new Vector3Int(cellsX, cellsY, cellsZ);
            gpuDetachedVoxelMaskMin = gridMin;
            gpuDetachedVoxelMaskStep = new Vector3(
                Mathf.Max(voxelSize * 0.000001f, cellStep.x),
                Mathf.Max(voxelSize * 0.000001f, cellStep.y),
                Mathf.Max(voxelSize * 0.000001f, cellStep.z));
            gpuDetachedVoxelMaskAirValue = airValue;
        }
        else
        {
            gpuDetachedVoxelMaskBuffer.SetData(new float[1]);
            gpuDetachedVoxelMaskSize = Vector3Int.zero;
            gpuDetachedVoxelMaskMin = Vector3.zero;
            gpuDetachedVoxelMaskStep = Vector3.one;
            gpuDetachedVoxelMaskAirValue = 0f;
        }
    }

    private void TryCleanupGlobalVisualIslandsIfDue()
    {
        if (!globalVisualConnectivityDirty || !removeDetachedParts ||
            !UsesGpuSurfaceRendering || !useExpandedDisplayBounds)
        {
            return;
        }

        if (Application.isPlaying &&
            Time.time < lastGlobalVisualCutTime + Mathf.Max(0.05f, detachedCleanupInterval))
        {
            return;
        }

        globalVisualConnectivityDirty = false;
        CleanupGlobalVisualIslands();
    }

    private void CleanupGlobalVisualIslands()
    {
        if (gpuVisualCutOperations.Count == 0)
        {
            return;
        }

        Vector3 displayHalf = DisplaySize * 0.5f;
        Vector3 displayMin = DisplayCenter - displayHalf;
        Vector3 displayMax = DisplayCenter + displayHalf;
        Vector3 cutMinAll = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 cutMaxAll = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        float minimumFeature = float.PositiveInfinity;

        for (int i = 0; i < gpuVisualCutOperations.Count; i++)
        {
            GetGpuVisualCutBounds(gpuVisualCutOperations[i], out Vector3 cutMin, out Vector3 cutMax);
            cutMinAll = Vector3.Min(cutMinAll, cutMin);
            cutMaxAll = Vector3.Max(cutMaxAll, cutMax);
            minimumFeature = Mathf.Min(
                minimumFeature,
                Mathf.Min(
                    Mathf.Max(voxelSize, gpuVisualCutOperations[i].startRadius.w),
                    Mathf.Max(voxelSize, gpuVisualCutOperations[i].endHeight.w)));
        }

        if (float.IsInfinity(minimumFeature))
        {
            return;
        }

        float targetCellSize = Mathf.Max(
            voxelSize * 2f,
            minimumFeature * Mathf.Clamp(globalConnectivityFeatureScale, 0.1f, 1f));
        float padding = Mathf.Max(minimumFeature, targetCellSize * 2f);

        // A detached piece often extends above or below the local cut window. If
        // vertical connectivity is evaluated only in that window, touching the
        // window edge looks like support and the island survives. Keep X/Z
        // focused around the cut footprint for resolution, but analyze the full
        // display height down to the real support boundary.
        Vector3 analysisMin = new Vector3(
            Mathf.Max(displayMin.x, cutMinAll.x - padding),
            displayMin.y,
            Mathf.Max(displayMin.z, cutMinAll.z - padding));
        Vector3 analysisMax = new Vector3(
            Mathf.Min(displayMax.x, cutMaxAll.x + padding),
            displayMax.y,
            Mathf.Min(displayMax.z, cutMaxAll.z + padding));
        Vector3 analysisSize = analysisMax - analysisMin;
        if (analysisSize.x <= 0f || analysisSize.y <= 0f || analysisSize.z <= 0f)
        {
            return;
        }

        targetCellSize = Mathf.Max(
            targetCellSize,
            Mathf.Max(analysisSize.x, Mathf.Max(analysisSize.y, analysisSize.z)) / 1024f);
        int cellsX = Mathf.Max(1, Mathf.CeilToInt(analysisSize.x / targetCellSize));
        int cellsY = Mathf.Max(1, Mathf.CeilToInt(analysisSize.y / targetCellSize));
        int cellsZ = Mathf.Max(1, Mathf.CeilToInt(analysisSize.z / targetCellSize));
        long totalCells = (long)cellsX * cellsY * cellsZ;
        int cellBudget = Mathf.Max(10000, maxGlobalConnectivityCells);

        while (totalCells > cellBudget)
        {
            float relaxation = Mathf.Max(1.05f, Mathf.Pow(totalCells / (float)cellBudget, 1f / 3f));
            targetCellSize *= relaxation;
            cellsX = Mathf.Max(1, Mathf.CeilToInt(analysisSize.x / targetCellSize));
            cellsY = Mathf.Max(1, Mathf.CeilToInt(analysisSize.y / targetCellSize));
            cellsZ = Mathf.Max(1, Mathf.CeilToInt(analysisSize.z / targetCellSize));
            totalCells = (long)cellsX * cellsY * cellsZ;
        }

        if (totalCells <= 1 || totalCells > int.MaxValue)
        {
            return;
        }

        Vector3 cellStep = new Vector3(
            analysisSize.x / cellsX,
            analysisSize.y / cellsY,
            analysisSize.z / cellsZ);
        int cellCount = (int)totalCells;
        byte[] solid = new byte[cellCount];
        uint[] removalMask = new uint[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            solid[i] = 1;
        }

        if (gpuDetachedRemovalBoxes.Count > 0)
        {
            gpuDetachedRemovalBoxes.Clear();
            gpuDetachedRemovalsDirty = true;
        }

        // Connectivity must never enlarge the cutter volume: doing so can
        // sever a real, narrow bridge and turn valid material into a false
        // island. The grid resolution is derived from the current cutter
        // feature size, so center-inside classification is sufficient.
        const float cutTolerance = 0f;
        for (int operationIndex = 0; operationIndex < gpuVisualCutOperations.Count; operationIndex++)
        {
            RasterizeVisualCut(
                solid,
                cellsX,
                cellsY,
                cellsZ,
                analysisMin,
                cellStep,
                gpuVisualCutOperations[operationIndex],
                cutTolerance);
        }

        int[] labels = new int[cellCount];
        Queue<int> queue = new Queue<int>(Mathf.Min(cellCount, 65536));
        List<GlobalConnectivityComponent> components = new List<GlobalConnectivityComponent>(16);
        int nextLabel = 0;

        for (int index = 0; index < cellCount; index++)
        {
            if (solid[index] == 0 || labels[index] != 0)
            {
                continue;
            }

            nextLabel++;
            GlobalConnectivityComponent component = new GlobalConnectivityComponent
            {
                label = nextLabel,
                minX = cellsX,
                minY = cellsY,
                minZ = cellsZ,
                maxX = -1,
                maxY = -1,
                maxZ = -1
            };
            labels[index] = nextLabel;
            queue.Enqueue(index);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                DecodeGlobalCellIndex(current, cellsX, cellsY, out int x, out int y, out int z);
                component.count++;
                component.minX = Mathf.Min(component.minX, x);
                component.minY = Mathf.Min(component.minY, y);
                component.minZ = Mathf.Min(component.minZ, z);
                component.maxX = Mathf.Max(component.maxX, x);
                component.maxY = Mathf.Max(component.maxY, y);
                component.maxZ = Mathf.Max(component.maxZ, z);
                component.touchesSupportedBoundary |= IsGlobalSupportBoundary(
                    x, y, z,
                    cellsX, cellsY, cellsZ,
                    analysisMin, analysisMax,
                    displayMin, displayMax,
                    cellStep);

                TryVisitGlobalNeighbor(x - 1, y, z, cellsX, cellsY, cellsZ, solid, labels, nextLabel, queue);
                TryVisitGlobalNeighbor(x + 1, y, z, cellsX, cellsY, cellsZ, solid, labels, nextLabel, queue);
                TryVisitGlobalNeighbor(x, y - 1, z, cellsX, cellsY, cellsZ, solid, labels, nextLabel, queue);
                TryVisitGlobalNeighbor(x, y + 1, z, cellsX, cellsY, cellsZ, solid, labels, nextLabel, queue);
                TryVisitGlobalNeighbor(x, y, z - 1, cellsX, cellsY, cellsZ, solid, labels, nextLabel, queue);
                TryVisitGlobalNeighbor(x, y, z + 1, cellsX, cellsY, cellsZ, solid, labels, nextLabel, queue);
            }

            components.Add(component);
        }

        // Do not remove components merely because they look enclosed in a
        // horizontal slice. A valid part can still be connected to material
        // below that slice. Only the full 3D connectivity result below is
        // allowed to mark removal voxels.
        int machiningRemovedCells = 0;

        bool hasSupportedComponent = false;
        int largestComponentIndex = 0;
        for (int i = 0; i < components.Count; i++)
        {
            hasSupportedComponent |= components[i].touchesSupportedBoundary;
            if (components[i].count > components[largestComponentIndex].count)
            {
                largestComponentIndex = i;
            }
        }

        int removedComponents = 0;
        bool[] removeComponentLabels = new bool[nextLabel + 1];
        if (components.Count > 1)
        {
            for (int i = 0; i < components.Count; i++)
            {
                bool keep = hasSupportedComponent
                    ? components[i].touchesSupportedBoundary
                    : i == largestComponentIndex;
                if (keep)
                {
                    continue;
                }

                removeComponentLabels[components[i].label] = true;
                removedComponents++;
            }
        }

        int detachedRemovedCells = 0;
        if (removedComponents > 0)
        {
            for (int index = 0; index < labels.Length; index++)
            {
                int label = labels[index];
                if (label > 0 && removeComponentLabels[label] && removalMask[index] == 0)
                {
                    removalMask[index] = 1;
                    detachedRemovedCells++;
                }
            }
        }

        UploadGpuDetachedVoxelMask(
            removalMask,
            cellsX,
            cellsY,
            cellsZ,
            analysisMin,
            cellStep);

        Debug.Log(
            $"GLOBAL_ISLAND_MASK machiningCells={machiningRemovedCells}, " +
            $"detachedCells={detachedRemovedCells}, components={removedComponents}, " +
            $"grid={cellsX}x{cellsY}x{cellsZ}, cell={cellStep}",
            this);
    }

    private int CleanupGlobalMachiningIslands(
        byte[] solid,
        uint[] removalMask,
        int cellsX,
        int cellsY,
        int cellsZ,
        Vector3 analysisMin,
        Vector3 analysisMax,
        Vector3 displayMin,
        Vector3 displayMax,
        Vector3 cellStep)
    {
        int sliceCellCount = cellsX * cellsZ;
        int[] sliceLabels = new int[sliceCellCount];
        Queue<int> queue = new Queue<int>(Mathf.Min(sliceCellCount, 65536));
        int removedCells = 0;

        for (int y = 0; y < cellsY; y++)
        {
            System.Array.Clear(sliceLabels, 0, sliceLabels.Length);
            List<GlobalConnectivityComponent> sliceComponents = new List<GlobalConnectivityComponent>(8);
            int nextLabel = 0;

            for (int z = 0; z < cellsZ; z++)
            {
                for (int x = 0; x < cellsX; x++)
                {
                    int sliceIndex = x + cellsX * z;
                    int globalIndex = GetGlobalCellIndex(x, y, z, cellsX, cellsY);
                    if (solid[globalIndex] == 0 || sliceLabels[sliceIndex] != 0)
                    {
                        continue;
                    }

                    nextLabel++;
                    GlobalConnectivityComponent component = new GlobalConnectivityComponent
                    {
                        label = nextLabel,
                        minX = cellsX,
                        minZ = cellsZ,
                        maxX = -1,
                        maxZ = -1
                    };
                    sliceLabels[sliceIndex] = nextLabel;
                    queue.Enqueue(sliceIndex);

                    while (queue.Count > 0)
                    {
                        int current = queue.Dequeue();
                        int currentZ = current / cellsX;
                        int currentX = current - currentZ * cellsX;
                        component.count++;
                        component.minX = Mathf.Min(component.minX, currentX);
                        component.minZ = Mathf.Min(component.minZ, currentZ);
                        component.maxX = Mathf.Max(component.maxX, currentX);
                        component.maxZ = Mathf.Max(component.maxZ, currentZ);
                        component.touchesSupportedBoundary |=
                            (currentX == 0 && analysisMin.x > displayMin.x + cellStep.x * 0.25f) ||
                            (currentX == cellsX - 1 && analysisMax.x < displayMax.x - cellStep.x * 0.25f) ||
                            (currentZ == 0 && analysisMin.z > displayMin.z + cellStep.z * 0.25f) ||
                            (currentZ == cellsZ - 1 && analysisMax.z < displayMax.z - cellStep.z * 0.25f);

                        TryVisitGlobalSliceNeighbor(
                            currentX - 1, currentZ, y,
                            cellsX, cellsY, cellsZ,
                            solid, sliceLabels, nextLabel, queue);
                        TryVisitGlobalSliceNeighbor(
                            currentX + 1, currentZ, y,
                            cellsX, cellsY, cellsZ,
                            solid, sliceLabels, nextLabel, queue);
                        TryVisitGlobalSliceNeighbor(
                            currentX, currentZ - 1, y,
                            cellsX, cellsY, cellsZ,
                            solid, sliceLabels, nextLabel, queue);
                        TryVisitGlobalSliceNeighbor(
                            currentX, currentZ + 1, y,
                            cellsX, cellsY, cellsZ,
                            solid, sliceLabels, nextLabel, queue);
                    }

                    sliceComponents.Add(component);
                }
            }

            if (sliceComponents.Count <= 1)
            {
                continue;
            }

            bool hasSupportedComponent = false;
            int largestComponentIndex = 0;
            for (int i = 0; i < sliceComponents.Count; i++)
            {
                hasSupportedComponent |= sliceComponents[i].touchesSupportedBoundary;
                if (sliceComponents[i].count > sliceComponents[largestComponentIndex].count)
                {
                    largestComponentIndex = i;
                }
            }

            for (int i = 0; i < sliceComponents.Count; i++)
            {
                bool keep = hasSupportedComponent
                    ? sliceComponents[i].touchesSupportedBoundary
                    : i == largestComponentIndex;
                if (keep)
                {
                    continue;
                }

                int labelToRemove = sliceComponents[i].label;
                int firstRemovedY = Mathf.Max(0, y - 1);
                for (int z = 0; z < cellsZ; z++)
                {
                    for (int x = 0; x < cellsX; x++)
                    {
                        if (sliceLabels[x + cellsX * z] != labelToRemove)
                        {
                            continue;
                        }

                        for (int removeY = firstRemovedY; removeY < cellsY; removeY++)
                        {
                            int globalIndex = GetGlobalCellIndex(x, removeY, z, cellsX, cellsY);
                            solid[globalIndex] = 0;
                            if (removalMask[globalIndex] == 0)
                            {
                                removalMask[globalIndex] = 1;
                                removedCells++;
                            }
                        }
                    }
                }
            }
        }

        return removedCells;
    }

    private void ExpandGlobalRemovalBounds(ref Vector3 boxMin, ref Vector3 boxMax, Vector3 cellStep)
    {
        Vector3 minimumPadding = Vector3.one * (voxelSize * 2f);
        Vector3 padding = Vector3.Max(cellStep * 1.5f, minimumPadding);
        boxMin -= padding;
        boxMax += padding;
    }

    private void ExpandGlobalMachiningRemovalBounds(ref Vector3 boxMin, ref Vector3 boxMax, Vector3 cellStep)
    {
        float minimumPadding = voxelSize * 2f;
        float paddingX = Mathf.Max(cellStep.x * 1.5f, minimumPadding);
        float paddingZ = Mathf.Max(cellStep.z * 1.5f, minimumPadding);
        float lowerPadding = Mathf.Max(cellStep.y * 0.1f, minimumPadding);
        float upperPadding = Mathf.Max(cellStep.y * 1.5f, minimumPadding);
        boxMin -= new Vector3(paddingX, lowerPadding, paddingZ);
        boxMax += new Vector3(paddingX, upperPadding, paddingZ);
    }

    private static void TryVisitGlobalSliceNeighbor(
        int x,
        int z,
        int y,
        int cellsX,
        int cellsY,
        int cellsZ,
        byte[] solid,
        int[] labels,
        int label,
        Queue<int> queue)
    {
        if (x < 0 || x >= cellsX || z < 0 || z >= cellsZ || y < 0 || y >= cellsY)
        {
            return;
        }

        int sliceIndex = x + cellsX * z;
        int globalIndex = GetGlobalCellIndex(x, y, z, cellsX, cellsY);
        if (solid[globalIndex] == 0 || labels[sliceIndex] != 0)
        {
            return;
        }

        labels[sliceIndex] = label;
        queue.Enqueue(sliceIndex);
    }

    private void RasterizeVisualCut(
        byte[] solid,
        int cellsX,
        int cellsY,
        int cellsZ,
        Vector3 analysisMin,
        Vector3 cellStep,
        GpuVisualCutOperation operation,
        float tolerance)
    {
        GetGpuVisualCutBounds(operation, out Vector3 cutMin, out Vector3 cutMax);
        GetGlobalRasterRange(cutMin, cutMax, analysisMin, cellStep, cellsX, cellsY, cellsZ,
            out int minX, out int maxX, out int minY, out int maxY, out int minZ, out int maxZ);

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Vector3 point = analysisMin + Vector3.Scale(
                        new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                        cellStep);
                    if (GpuVisualProfileDifferenceCpu(point, operation) >= -tolerance)
                    {
                        solid[GetGlobalCellIndex(x, y, z, cellsX, cellsY)] = 0;
                    }
                }
            }
        }
    }

    private static void RasterizeRemovalBox(
        byte[] solid,
        int cellsX,
        int cellsY,
        int cellsZ,
        Vector3 analysisMin,
        Vector3 cellStep,
        Vector3 boxMin,
        Vector3 boxMax)
    {
        GetGlobalRasterRange(boxMin, boxMax, analysisMin, cellStep, cellsX, cellsY, cellsZ,
            out int minX, out int maxX, out int minY, out int maxY, out int minZ, out int maxZ);
        for (int z = minZ; z <= maxZ; z++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    Vector3 point = analysisMin + Vector3.Scale(
                        new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                        cellStep);
                    if (point.x >= boxMin.x && point.x <= boxMax.x &&
                        point.y >= boxMin.y && point.y <= boxMax.y &&
                        point.z >= boxMin.z && point.z <= boxMax.z)
                    {
                        solid[GetGlobalCellIndex(x, y, z, cellsX, cellsY)] = 0;
                    }
                }
            }
        }
    }

    private static void GetGlobalRasterRange(
        Vector3 boundsMin,
        Vector3 boundsMax,
        Vector3 gridMin,
        Vector3 cellStep,
        int cellsX,
        int cellsY,
        int cellsZ,
        out int minX,
        out int maxX,
        out int minY,
        out int maxY,
        out int minZ,
        out int maxZ)
    {
        minX = Mathf.Clamp(Mathf.FloorToInt((boundsMin.x - gridMin.x) / cellStep.x), 0, cellsX - 1);
        maxX = Mathf.Clamp(Mathf.FloorToInt((boundsMax.x - gridMin.x) / cellStep.x), 0, cellsX - 1);
        minY = Mathf.Clamp(Mathf.FloorToInt((boundsMin.y - gridMin.y) / cellStep.y), 0, cellsY - 1);
        maxY = Mathf.Clamp(Mathf.FloorToInt((boundsMax.y - gridMin.y) / cellStep.y), 0, cellsY - 1);
        minZ = Mathf.Clamp(Mathf.FloorToInt((boundsMin.z - gridMin.z) / cellStep.z), 0, cellsZ - 1);
        maxZ = Mathf.Clamp(Mathf.FloorToInt((boundsMax.z - gridMin.z) / cellStep.z), 0, cellsZ - 1);
    }

    private static int GetGlobalCellIndex(int x, int y, int z, int cellsX, int cellsY)
    {
        return x + cellsX * (y + cellsY * z);
    }

    private static void DecodeGlobalCellIndex(int index, int cellsX, int cellsY, out int x, out int y, out int z)
    {
        int layerSize = cellsX * cellsY;
        z = index / layerSize;
        int layerOffset = index - z * layerSize;
        y = layerOffset / cellsX;
        x = layerOffset - y * cellsX;
    }

    private static void TryVisitGlobalNeighbor(
        int x,
        int y,
        int z,
        int cellsX,
        int cellsY,
        int cellsZ,
        byte[] solid,
        int[] labels,
        int label,
        Queue<int> queue)
    {
        if (x < 0 || x >= cellsX || y < 0 || y >= cellsY || z < 0 || z >= cellsZ)
        {
            return;
        }

        int index = GetGlobalCellIndex(x, y, z, cellsX, cellsY);
        if (solid[index] == 0 || labels[index] != 0)
        {
            return;
        }

        labels[index] = label;
        queue.Enqueue(index);
    }

    private static bool IsGlobalSupportBoundary(
        int x,
        int y,
        int z,
        int cellsX,
        int cellsY,
        int cellsZ,
        Vector3 analysisMin,
        Vector3 analysisMax,
        Vector3 displayMin,
        Vector3 displayMax,
        Vector3 cellStep)
    {
        float epsilonX = cellStep.x * 0.25f;
        float epsilonY = cellStep.y * 0.25f;
        float epsilonZ = cellStep.z * 0.25f;
        return (x == 0 && analysisMin.x > displayMin.x + epsilonX) ||
               (x == cellsX - 1 && analysisMax.x < displayMax.x - epsilonX) ||
               y == 0 ||
               (y == cellsY - 1 && analysisMax.y < displayMax.y - epsilonY) ||
               (z == 0 && analysisMin.z > displayMin.z + epsilonZ) ||
               (z == cellsZ - 1 && analysisMax.z < displayMax.z - epsilonZ);
    }

    private bool TryAddGpuDetachedRemovalBox(Vector3 boxMin, Vector3 boxMax)
    {
        Vector3 size = boxMax - boxMin;
        if (size.x <= 0f || size.y <= 0f || size.z <= 0f)
        {
            return false;
        }

        Vector3 center = (boxMin + boxMax) * 0.5f;
        for (int i = 0; i < gpuDetachedRemovalBoxes.Count; i++)
        {
            GpuDetachedRemovalBox existing = gpuDetachedRemovalBoxes[i];
            Vector3 existingCenter = new Vector3(existing.center.x, existing.center.y, existing.center.z);
            Vector3 existingSize = new Vector3(existing.size.x, existing.size.y, existing.size.z);
            Vector3 existingMin = existingCenter - existingSize * 0.5f;
            Vector3 existingMax = existingCenter + existingSize * 0.5f;
            if (boxMin.x >= existingMin.x && boxMax.x <= existingMax.x &&
                boxMin.y >= existingMin.y && boxMax.y <= existingMax.y &&
                boxMin.z >= existingMin.z && boxMax.z <= existingMax.z)
            {
                return false;
            }
        }

        if (gpuDetachedRemovalBoxes.Count >= Mathf.Max(1, maxGpuDetachedRemovalBoxes))
        {
            Debug.LogWarning("Global detached removal box budget reached; increase maxGpuDetachedRemovalBoxes.", this);
            return false;
        }

        gpuDetachedRemovalBoxes.Add(new GpuDetachedRemovalBox
        {
            center = new Vector4(center.x, center.y, center.z, 0f),
            size = new Vector4(size.x, size.y, size.z, 0f)
        });
        return true;
    }

    private static void GetGpuVisualCutBounds(GpuVisualCutOperation operation, out Vector3 boundsMin, out Vector3 boundsMax)
    {
        Vector3 start = new Vector3(operation.startRadius.x, operation.startRadius.y, operation.startRadius.z);
        Vector3 end = new Vector3(operation.endHeight.x, operation.endHeight.y, operation.endHeight.z);
        Vector3 axis = new Vector3(operation.axis.x, operation.axis.y, operation.axis.z);
        axis = axis.sqrMagnitude < 0.000001f ? Vector3.up : axis.normalized;
        float radius = Mathf.Max(0f, operation.startRadius.w);
        float height = Mathf.Max(0f, operation.endHeight.w);
        Vector3 startTop = start + axis * height;
        Vector3 endTop = end + axis * height;
        boundsMin = Vector3.Min(Vector3.Min(start, end), Vector3.Min(startTop, endTop)) - Vector3.one * radius;
        boundsMax = Vector3.Max(Vector3.Max(start, end), Vector3.Max(startTop, endTop)) + Vector3.one * radius;
    }

    private float GpuVisualProfileDifferenceCpu(Vector3 samplePoint, GpuVisualCutOperation operation)
    {
        Vector3 start = new Vector3(operation.startRadius.x, operation.startRadius.y, operation.startRadius.z);
        Vector3 end = new Vector3(operation.endHeight.x, operation.endHeight.y, operation.endHeight.z);
        Vector3 axis = new Vector3(operation.axis.x, operation.axis.y, operation.axis.z);
        axis = axis.sqrMagnitude < 0.000001f ? Vector3.up : axis.normalized;
        float radius = Mathf.Max(0f, operation.startRadius.w);
        float cutterHeight = Mathf.Max(voxelSize, operation.endHeight.w);
        Vector3 motion = end - start;
        Vector3 radialMotion = motion - axis * Vector3.Dot(motion, axis);
        Vector3 toSampleFromStart = samplePoint - start;
        Vector3 radialFromStart = toSampleFromStart - axis * Vector3.Dot(toSampleFromStart, axis);
        float epsilon = Mathf.Max(radius, cutterHeight) * 0.000001f;
        float epsilonSqr = epsilon * epsilon;
        float radialT = radialMotion.sqrMagnitude > epsilonSqr
            ? Mathf.Clamp01(Vector3.Dot(radialFromStart, radialMotion) / radialMotion.sqrMagnitude)
            : 1f;
        float fullT = motion.sqrMagnitude > epsilonSqr
            ? Mathf.Clamp01(Vector3.Dot(samplePoint - start, motion) / motion.sqrMagnitude)
            : radialT;
        int sampleCount = motion.sqrMagnitude > epsilonSqr ? 8 : 1;
        float bestDifference = float.MinValue;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = sampleCount == 1
                ? radialT
                : i == 0
                    ? radialT
                    : i == 1
                        ? fullT
                        : (float)(i - 1) / (sampleCount - 2);
            Vector3 root = Vector3.Lerp(start, end, t);
            Vector3 toSample = samplePoint - root;
            float axial = Vector3.Dot(toSample, axis);
            Vector3 radial = toSample - axis * axial;
            float radialDifference = OperationCutterProfileRadialDifference(axial, radial, radius, cutterHeight, axis, operation);
            float difference = Mathf.Min(radialDifference, Mathf.Min(axial, cutterHeight - axial));
            bestDifference = Mathf.Max(bestDifference, difference);
        }

        return bestDifference;
    }

    private float OperationCutterProfileRadialDifference(
        float axial,
        Vector3 radial,
        float radius,
        float cutterHeight,
        Vector3 axis,
        GpuVisualCutOperation operation)
    {
        if (cutterAngularProfileAxialSampleCount >= 2 && cutterAngularProfileAngleSampleCount >= 3)
        {
            float radialDistance = radial.magnitude;
            GetAngularProfileInterval(
                axial,
                radial,
                radius,
                cutterHeight,
                axis,
                new Vector3(operation.profileMeta.y, operation.profileMeta.z, operation.profileMeta.w),
                out float minRadius,
                out float maxRadius);
            if (maxRadius > 0.000001f)
            {
                return maxRadius - radialDistance;
            }

            return -1000000f;
        }

        return OperationCutterProfileRadius(axial, radius, cutterHeight, operation) - radial.magnitude;
    }

    private void GetAngularProfileInterval(
        float axial,
        Vector3 radial,
        float radius,
        float cutterHeight,
        Vector3 axis,
        Vector3 right,
        out float minRadius,
        out float maxRadius)
    {
        minRadius = 0f;
        maxRadius = -1f;
        if (cutterAngularProfileAxialSampleCount < 2 || cutterAngularProfileAngleSampleCount < 3)
        {
            return;
        }

        float normalizedHeight = Mathf.Clamp01(axial / Mathf.Max(cutterHeight, voxelSize * 0.000001f));
        Vector3 localRight = ResolveLocalCutterRight(axis, right);
        Vector3 forward = Vector3.Cross(axis.normalized, localRight).normalized;
        float angle = Mathf.Atan2(Vector3.Dot(radial, forward), Vector3.Dot(radial, localRight));
        float normalizedAngle = Mathf.Repeat(angle / (Mathf.PI * 2f), 1f);
        float minSample = SampleAngularProfile(normalizedHeight, normalizedAngle, true);
        float maxSample = SampleAngularProfile(normalizedHeight, normalizedAngle, false);
        if (maxSample <= 0.000001f)
        {
            return;
        }

        minRadius = radius * Mathf.Max(0f, minSample);
        maxRadius = radius * Mathf.Max(maxSample, minSample);
    }

    private float SampleAngularProfile(float normalizedHeight, float normalizedAngle, bool sampleMin)
    {
        int axialCount = cutterAngularProfileAxialSampleCount;
        int angleCount = cutterAngularProfileAngleSampleCount;
        float axialPosition = Mathf.Clamp01(normalizedHeight) * (axialCount - 1);
        int axial0 = Mathf.Min(Mathf.FloorToInt(axialPosition), axialCount - 2);
        int axial1 = axial0 + 1;
        float axialT = Mathf.Clamp01(axialPosition - axial0);
        float anglePosition = Mathf.Repeat(normalizedAngle, 1f) * angleCount;
        int angle0 = Mathf.FloorToInt(anglePosition) % angleCount;
        int angle1 = (angle0 + 1) % angleCount;
        float angleT = Mathf.Clamp01(anglePosition - Mathf.Floor(anglePosition));
        float value00 = GetAngularProfileSample(axial0, angle0, sampleMin);
        float value01 = GetAngularProfileSample(axial0, angle1, sampleMin);
        float value10 = GetAngularProfileSample(axial1, angle0, sampleMin);
        float value11 = GetAngularProfileSample(axial1, angle1, sampleMin);
        float angleA = Mathf.Lerp(value00, value01, Mathf.SmoothStep(0f, 1f, angleT));
        float angleB = Mathf.Lerp(value10, value11, Mathf.SmoothStep(0f, 1f, angleT));
        return Mathf.Lerp(angleA, angleB, Mathf.SmoothStep(0f, 1f, axialT));
    }

    private float GetAngularProfileSample(int axialIndex, int angleIndex, bool sampleMin)
    {
        int index = axialIndex * cutterAngularProfileAngleSampleCount + angleIndex;
        return sampleMin
            ? cutterAngularProfileMinRadiusSamples[index]
            : cutterAngularProfileMaxRadiusSamples[index];
    }

    private float OperationCutterProfileRadius(
        float axial,
        float radius,
        float cutterHeight,
        GpuVisualCutOperation operation)
    {
        int sampleCount = Mathf.Clamp(Mathf.RoundToInt(operation.profileMeta.x), 0, MaxCutterProfileRadiusSamples);
        if (sampleCount >= 2)
        {
            float normalizedHeight = Mathf.Clamp01(axial / Mathf.Max(cutterHeight, 0.000001f));
            float samplePosition = normalizedHeight * (sampleCount - 1);
            int sampleIndex = Mathf.Min(Mathf.FloorToInt(samplePosition), sampleCount - 2);
            float t = Mathf.Clamp01(samplePosition - sampleIndex);
            float radiusA = GetOperationProfileRadiusSample(operation, sampleIndex);
            float radiusB = GetOperationProfileRadiusSample(operation, sampleIndex + 1);
            return radius * Mathf.Lerp(radiusA, radiusB, Mathf.SmoothStep(0f, 1f, t));
        }

        return GlobalCutterProfileRadius(axial, radius, cutterHeight);
    }

    private static float GetOperationProfileRadiusSample(GpuVisualCutOperation operation, int index)
    {
        if (index < 0 || index >= MaxCutterProfileRadiusSamples)
        {
            return 0f;
        }

        Vector4 packed = index < 4
            ? operation.profile0
            : index < 8
                ? operation.profile1
                : index < 12
                    ? operation.profile2
                    : index < 16
                        ? operation.profile3
                        : index < 20
                            ? operation.profile4
                            : index < 24
                                ? operation.profile5
                                : index < 28
                                    ? operation.profile6
                                    : operation.profile7;
        int component = index & 3;
        return component == 0
            ? packed.x
            : component == 1
                ? packed.y
                : component == 2
                    ? packed.z
                    : packed.w;
    }

    private float GlobalCutterProfileRadius(float axial, float radius, float cutterHeight)
    {
        if (cutterProfileRadiusSampleCount >= 2)
        {
            float sampledNormalizedHeight = Mathf.Clamp01(axial / Mathf.Max(cutterHeight, voxelSize * 0.000001f));
            float samplePosition = sampledNormalizedHeight * (cutterProfileRadiusSampleCount - 1);
            int sampleIndex = Mathf.Min(Mathf.FloorToInt(samplePosition), cutterProfileRadiusSampleCount - 2);
            float sampleT = Mathf.Clamp01(samplePosition - sampleIndex);
            float sampleRadiusA = cutterProfileRadiusSamples[sampleIndex];
            float sampleRadiusB = cutterProfileRadiusSamples[sampleIndex + 1];
            return radius * Mathf.Lerp(sampleRadiusA, sampleRadiusB, Mathf.SmoothStep(0f, 1f, sampleT));
        }

        float normalizedHeight = Mathf.Clamp01(axial / Mathf.Max(cutterHeight, voxelSize * 0.000001f));
        int segments = Mathf.Max(2, profileSegmentCount);
        float profilePosition = normalizedHeight * segments;
        int segment = Mathf.Min(Mathf.FloorToInt(profilePosition), segments - 1);
        float t = Mathf.Clamp01(profilePosition - segment);
        float innerRadius = radius * 0.6535898f;
        float radiusA = (segment & 1) == 0 ? radius : innerRadius;
        float radiusB = ((segment + 1) & 1) == 0 ? radius : innerRadius;
        return Mathf.Lerp(radiusA, radiusB, Mathf.SmoothStep(0f, 1f, t));
    }

    private static bool AabbOverlaps(Vector3 minA, Vector3 maxA, Vector3 minB, Vector3 maxB)
    {
        return minA.x <= maxB.x && maxA.x >= minB.x &&
               minA.y <= maxB.y && maxA.y >= minB.y &&
               minA.z <= maxB.z && maxA.z >= minB.z;
    }

    private bool TryCutProfileCutterSdfGpuNoReadback(
        Vector3 localStart,
        Vector3 localEnd,
        Vector3 localAxis,
        Vector3 localRight,
        float localRadius,
        float localHeight,
        float updateBand,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int minZ,
        int maxZ)
    {
        if (!EnsureGpuSdfResources())
        {
            return false;
        }

        try
        {
            sdfChangedCounterData[0] = 0;
            sdfChangedCounterBuffer.SetData(sdfChangedCounterData);
            DispatchCutProfileCutterSdfGpu(localStart, localEnd, localAxis, localRight, localRadius, localHeight, updateBand, minX, maxX, minY, maxY, minZ, maxZ);
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"GPU profile cutter SDF cutting failed: {exception.Message}", this);
            sdfGpuUnavailable = true;
            ReleaseGpuSdfResources();
            return false;
        }
    }

    private bool TryCutCapsuleSdfGpuNoReadback(
        Vector3 localStart,
        Vector3 localEnd,
        float localRadius,
        float affectedRadius,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int minZ,
        int maxZ)
    {
        if (!EnsureGpuSdfResources())
        {
            return false;
        }

        try
        {
            sdfChangedCounterData[0] = 0;
            sdfChangedCounterBuffer.SetData(sdfChangedCounterData);
            DispatchCutCapsuleSdfGpu(localStart, localEnd, localRadius, affectedRadius, minX, maxX, minY, maxY, minZ, maxZ);
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"GPU SDF cutting failed: {exception.Message}", this);
            sdfGpuUnavailable = true;
            ReleaseGpuSdfResources();
            return false;
        }
    }

    private int CutCapsuleSdfCpu(
        Vector3 localStart,
        Vector3 localEnd,
        float localRadius,
        float affectedRadius,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int minZ,
        int maxZ,
        out Vector3 priorityPoint)
    {
        int changedSampleCount = 0;
        float affectedRadiusSqr = affectedRadius * affectedRadius;
        priorityPoint = localEnd - Vector3.up * localRadius;
        float priorityT = -1f;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Vector3 samplePoint = GetSamplePointLocal(x, y, z);
                    Vector3 closest = ClosestPointOnSegment(localStart, localEnd, samplePoint);
                    Vector3 toSample = samplePoint - closest;
                    if (toSample.sqrMagnitude > affectedRadiusSqr)
                    {
                        continue;
                    }

                    float cutterDifference = localRadius - toSample.magnitude;
                    float oldValue = sdfSamples[x, y, z];
                    float newValue = Mathf.Max(oldValue, cutterDifference);

                    if (newValue > oldValue + SdfChangeEpsilon)
                    {
                        sdfSamples[x, y, z] = newValue;
                        changedSampleCount++;

                        float segmentT = GetSegmentT(localStart, localEnd, samplePoint);
                        if (segmentT > priorityT)
                        {
                            priorityT = segmentT;
                            priorityPoint = samplePoint;
                        }
                    }
                }
            }
        }

        return changedSampleCount;
    }

    private bool TryCutCapsuleSdfGpu(
        Vector3 localStart,
        Vector3 localEnd,
        float localRadius,
        float affectedRadius,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int minZ,
        int maxZ,
        out int changedSampleCount,
        out Vector3 priorityPoint)
    {
        changedSampleCount = 0;
        priorityPoint = localEnd - Vector3.up * localRadius;

        if (!EnsureGpuSdfResources())
        {
            return false;
        }

        try
        {
            sdfChangedCounterData[0] = 0;
            sdfChangedCounterBuffer.SetData(sdfChangedCounterData);

            DispatchCutCapsuleSdfGpu(localStart, localEnd, localRadius, affectedRadius, minX, maxX, minY, maxY, minZ, maxZ);

            sdfChangedCounterBuffer.GetData(sdfChangedCounterData);
            changedSampleCount = Mathf.Max(0, sdfChangedCounterData[0]);
            if (changedSampleCount > 0)
            {
                EnsureSdfLinearSamples();
                sdfSampleBuffer.GetData(sdfLinearSamples);
                CopyLinearToSdfSamples();
                priorityPoint = FindSdfCutPriorityPoint(localStart, localEnd, localRadius, minX, maxX, minY, maxY, minZ, maxZ);
            }

            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"GPU SDF cutting failed, falling back to CPU: {exception.Message}", this);
            sdfGpuUnavailable = true;
            ReleaseGpuSdfResources();
            return false;
        }
    }

    private void DispatchCutCapsuleSdfGpu(
        Vector3 localStart,
        Vector3 localEnd,
        float localRadius,
        float affectedRadius,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int minZ,
        int maxZ)
    {
        sdfCutCompute.SetBuffer(sdfCutKernel, "_SdfSamples", sdfSampleBuffer);
        sdfCutCompute.SetBuffer(sdfCutKernel, "_ChangedCounter", sdfChangedCounterBuffer);
        sdfCutCompute.SetInts("_MinIndex", minX, minY, minZ);
        sdfCutCompute.SetInts("_MaxIndexExclusive", maxX + 1, maxY + 1, maxZ + 1);
        sdfCutCompute.SetInts("_SampleSize", SampleWidth, SampleHeight, SampleDepth);
        sdfCutCompute.SetVector("_GridMin", GetLocalMin());
        sdfCutCompute.SetVector("_LocalStart", localStart);
        sdfCutCompute.SetVector("_LocalEnd", localEnd);
        sdfCutCompute.SetFloat("_VoxelSize", voxelSize);
        sdfCutCompute.SetFloat("_LocalRadius", localRadius);
        sdfCutCompute.SetFloat("_AffectedRadiusSqr", affectedRadius * affectedRadius);
        sdfCutCompute.SetFloat("_ChangeEpsilon", SdfChangeEpsilon);

        int threadGroupsX = Mathf.CeilToInt((maxX - minX + 1) / 8f);
        int threadGroupsY = Mathf.CeilToInt((maxY - minY + 1) / 8f);
        int threadGroupsZ = Mathf.CeilToInt((maxZ - minZ + 1) / 4f);
        sdfCutCompute.Dispatch(sdfCutKernel, threadGroupsX, threadGroupsY, threadGroupsZ);
    }

    private void DispatchCutProfileCutterSdfGpu(
        Vector3 localStart,
        Vector3 localEnd,
        Vector3 localAxis,
        Vector3 localRight,
        float localRadius,
        float localHeight,
        float updateBand,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int minZ,
        int maxZ)
    {
        sdfCutCompute.SetBuffer(sdfProfileCutterKernel, "_SdfSamples", sdfSampleBuffer);
        sdfCutCompute.SetBuffer(sdfProfileCutterKernel, "_ChangedCounter", sdfChangedCounterBuffer);
        sdfCutCompute.SetInts("_MinIndex", minX, minY, minZ);
        sdfCutCompute.SetInts("_MaxIndexExclusive", maxX + 1, maxY + 1, maxZ + 1);
        sdfCutCompute.SetInts("_SampleSize", SampleWidth, SampleHeight, SampleDepth);
        sdfCutCompute.SetVector("_GridMin", GetLocalMin());
        sdfCutCompute.SetVector("_LocalStart", localStart);
        sdfCutCompute.SetVector("_LocalEnd", localEnd);
        sdfCutCompute.SetVector("_CutterAxis", localAxis.normalized);
        sdfCutCompute.SetVector("_CutterRight", ResolveLocalCutterRight(localAxis, localRight));
        sdfCutCompute.SetFloat("_VoxelSize", voxelSize);
        sdfCutCompute.SetFloat("_LocalRadius", localRadius);
        sdfCutCompute.SetFloat("_CutterHeight", localHeight);
        sdfCutCompute.SetFloat("_CutBand", updateBand);
        sdfCutCompute.SetFloat("_AffectedRadiusSqr", (localRadius + updateBand) * (localRadius + updateBand));
        sdfCutCompute.SetFloat("_ChangeEpsilon", SdfChangeEpsilon);
        sdfCutCompute.SetInt("_ProfileSegmentCount", profileSegmentCount);
        sdfCutCompute.SetInt("_ProfileRadiusSampleCount", cutterProfileRadiusSampleCount);
        sdfCutCompute.SetFloats("_ProfileRadiusSamples", cutterProfileRadiusSamples);
        sdfCutCompute.SetInt("_AngularProfileAxialSampleCount", cutterAngularProfileAxialSampleCount);
        sdfCutCompute.SetInt("_AngularProfileAngleSampleCount", cutterAngularProfileAngleSampleCount);
        sdfCutCompute.SetFloats("_AngularProfileMinRadiusSamples", cutterAngularProfileMinRadiusSamples);
        sdfCutCompute.SetFloats("_AngularProfileMaxRadiusSamples", cutterAngularProfileMaxRadiusSamples);

        int threadGroupsX = Mathf.CeilToInt((maxX - minX + 1) / 8f);
        int threadGroupsY = Mathf.CeilToInt((maxY - minY + 1) / 8f);
        int threadGroupsZ = Mathf.CeilToInt((maxZ - minZ + 1) / 4f);
        sdfCutCompute.Dispatch(sdfProfileCutterKernel, threadGroupsX, threadGroupsY, threadGroupsZ);
    }

    private Vector3 FindSdfCutPriorityPoint(
        Vector3 localStart,
        Vector3 localEnd,
        float localRadius,
        int minX,
        int maxX,
        int minY,
        int maxY,
        int minZ,
        int maxZ)
    {
        Vector3 priorityPoint = localEnd - Vector3.up * localRadius;
        float priorityT = -1f;
        float searchRadius = localRadius + voxelSize;
        float searchRadiusSqr = searchRadius * searchRadius;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    Vector3 samplePoint = GetSamplePointLocal(x, y, z);
                    Vector3 closest = ClosestPointOnSegment(localStart, localEnd, samplePoint);
                    if ((samplePoint - closest).sqrMagnitude > searchRadiusSqr)
                    {
                        continue;
                    }

                    float segmentT = GetSegmentT(localStart, localEnd, samplePoint);
                    if (segmentT > priorityT)
                    {
                        priorityT = segmentT;
                        priorityPoint = samplePoint;
                    }
                }
            }
        }

        return priorityPoint;
    }

    private bool ShouldCleanupDetachedParts()
    {
        if (!removeDetachedParts || surfaceMode != WorkpieceSurfaceMode.SmoothSdf)
        {
            return false;
        }

        if (UsesGpuSurfaceRendering)
        {
            return false;
        }
        else if (sdfSamples == null)
        {
            return false;
        }

        if (!Application.isPlaying)
        {
            return true;
        }

        if (Time.time < nextDetachedCleanupTime)
        {
            return false;
        }

        nextDetachedCleanupTime = Time.time + detachedCleanupInterval;
        return true;
    }

    private int RemoveDetachedSdfComponents()
    {
        int cellCount = width * height * depth;
        EnsureConnectivityBuffers(cellCount);

        connectivityQueue.Clear();
        componentSizes.Clear();
        componentSizes.Add(0);

        for (int i = 0; i < cellCount; i++)
        {
            connectivityLabels[i] = 0;
        }

        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = GetCellIndex(x, y, z);
                    connectivitySolid[index] = CellContainsSdfMaterial(x, y, z);
                }
            }
        }

        int componentId = 0;
        int largestComponentId = 0;
        int largestComponentSize = 0;

        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = GetCellIndex(x, y, z);
                    if (!connectivitySolid[index] || connectivityLabels[index] != 0)
                    {
                        continue;
                    }

                    componentId++;
                    int size = FloodFillComponent(x, y, z, componentId);
                    componentSizes.Add(size);

                    if (size > largestComponentSize)
                    {
                        largestComponentSize = size;
                        largestComponentId = componentId;
                    }
                }
            }
        }

        if (componentId <= 1 || largestComponentId == 0)
        {
            return 0;
        }

        int removedCells = 0;
        int minRemovedX = width;
        int minRemovedY = height;
        int minRemovedZ = depth;
        int maxRemovedX = -1;
        int maxRemovedY = -1;
        int maxRemovedZ = -1;

        for (int z = 0; z < depth; z++)
        {
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = GetCellIndex(x, y, z);
                    if (!connectivitySolid[index] || connectivityLabels[index] == largestComponentId)
                    {
                        continue;
                    }

                    CarveCellToAir(x, y, z);
                    removedCells++;

                    minRemovedX = Mathf.Min(minRemovedX, x);
                    minRemovedY = Mathf.Min(minRemovedY, y);
                    minRemovedZ = Mathf.Min(minRemovedZ, z);
                    maxRemovedX = Mathf.Max(maxRemovedX, x);
                    maxRemovedY = Mathf.Max(maxRemovedY, y);
                    maxRemovedZ = Mathf.Max(maxRemovedZ, z);
                }
            }
        }

        if (removedCells > 0 && UsesChunkedSmoothMesh)
        {
            MarkDirtyCells(
                Mathf.Max(0, minRemovedX - 1),
                Mathf.Min(CellWidth, maxRemovedX + 2),
                Mathf.Max(0, minRemovedY - 1),
                Mathf.Min(CellHeight, maxRemovedY + 2),
                Mathf.Max(0, minRemovedZ - 1),
                Mathf.Min(CellDepth, maxRemovedZ + 2),
                Vector3.zero);
        }

        if (removedCells > 0)
        {
            UploadSdfSamplesToGpu();
        }

        return removedCells;
    }

    private void EnsureConnectivityBuffers(int cellCount)
    {
        if (connectivitySolid == null || connectivitySolid.Length != cellCount)
        {
            connectivitySolid = new bool[cellCount];
            connectivityLabels = new int[cellCount];
        }
    }

    private int FloodFillComponent(int startX, int startY, int startZ, int componentId)
    {
        int startIndex = GetCellIndex(startX, startY, startZ);
        connectivityLabels[startIndex] = componentId;
        connectivityQueue.Enqueue(startIndex);

        int size = 0;
        while (connectivityQueue.Count > 0)
        {
            int index = connectivityQueue.Dequeue();
            DecodeCellIndex(index, out int x, out int y, out int z);
            size++;

            TryVisitComponentNeighbor(x + 1, y, z, componentId);
            TryVisitComponentNeighbor(x - 1, y, z, componentId);
            TryVisitComponentNeighbor(x, y + 1, z, componentId);
            TryVisitComponentNeighbor(x, y - 1, z, componentId);
            TryVisitComponentNeighbor(x, y, z + 1, componentId);
            TryVisitComponentNeighbor(x, y, z - 1, componentId);
        }

        return size;
    }

    private void TryVisitComponentNeighbor(int x, int y, int z, int componentId)
    {
        if (!IsInside(x, y, z))
        {
            return;
        }

        int index = GetCellIndex(x, y, z);
        if (!connectivitySolid[index] || connectivityLabels[index] != 0)
        {
            return;
        }

        connectivityLabels[index] = componentId;
        connectivityQueue.Enqueue(index);
    }

    private bool CellContainsSdfMaterial(int x, int y, int z)
    {
        Vector3 center = GetVoxelCenterLocal(x, y, z, GetLocalMin());
        if (SampleSdf(center) <= IsoLevel)
        {
            return true;
        }

        for (int corner = 0; corner < 8; corner++)
        {
            int sx = x + 1 + CubeCorners[corner, 0];
            int sy = y + 1 + CubeCorners[corner, 1];
            int sz = z + 1 + CubeCorners[corner, 2];

            if (sdfSamples[sx, sy, sz] <= IsoLevel)
            {
                return true;
            }
        }

        return false;
    }

    private void CarveCellToAir(int x, int y, int z)
    {
        float airValue = voxelSize * 1.5f;

        for (int sx = x; sx <= x + 2; sx++)
        {
            if (sx < 0 || sx >= SampleWidth)
            {
                continue;
            }

            for (int sy = y; sy <= y + 2; sy++)
            {
                if (sy < 0 || sy >= SampleHeight)
                {
                    continue;
                }

                for (int sz = z; sz <= z + 2; sz++)
                {
                    if (sz < 0 || sz >= SampleDepth)
                    {
                        continue;
                    }

                    sdfSamples[sx, sy, sz] = Mathf.Max(sdfSamples[sx, sy, sz], airValue);
                }
            }
        }
    }

    // ========================================================================
    // Native SDF plugin integration (Zig sdf_island_remover)
    // ========================================================================

    private void InitializeNativeSdfPlugin()
    {
        sdfNativeReady = false;
        nextNativeConnectivityCheckTime = 0f;
        nativeConnectivityDirty = false;
        nativeConnectivityInFlight = false;

        if (surfaceMode != WorkpieceSurfaceMode.SmoothSdf)
        {
            return;
        }

        try
        {
            Vector3 min = GetLocalMin();
            Vector3 size = LocalSize;
            SdfNativePlugin.sdf_plugin_init(
                width, height, depth,
                SampleWidth, SampleHeight, SampleDepth,
                voxelSize,
                min.x, min.y, min.z,
                size.x, size.y, size.z);
            SdfNativePlugin.sdf_set_profile_segment_count(profileSegmentCount);
            sdfNativeReady = true;
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

    private void NativeCutCapsule(Vector3 localStart, Vector3 localEnd, float localRadius,
        int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        if (!sdfNativeReady)
        {
            return;
        }

        try
        {
            int changed = SdfNativePlugin.sdf_cut_capsule(
                localStart.x, localStart.y, localStart.z,
                localEnd.x, localEnd.y, localEnd.z,
                localRadius,
                minX, maxX, minY, maxY, minZ, maxZ);
            nativeConnectivityDirty |= changed > 0;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native capsule cut failed: {ex.Message}", this);
            sdfNativeReady = false;
        }
    }

    private void NativeCutProfileCutter(Vector3 localStart, Vector3 localEnd, Vector3 localAxis,
        float localRadius, float localHeight, float updateBand,
        int minX, int maxX, int minY, int maxY, int minZ, int maxZ)
    {
        if (!sdfNativeReady)
        {
            return;
        }

        try
        {
            int changed = SdfNativePlugin.sdf_cut_profile_cutter(
                localStart.x, localStart.y, localStart.z,
                localEnd.x, localEnd.y, localEnd.z,
                localAxis.x, localAxis.y, localAxis.z,
                localRadius, localHeight, updateBand,
                minX, maxX, minY, maxY, minZ, maxZ);
            nativeConnectivityDirty |= changed > 0;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native profile cutter cut failed: {ex.Message}", this);
            sdfNativeReady = false;
        }
    }

    private void PollNativeConnectivity()
    {
        if (!sdfNativeReady || !removeDetachedParts ||
            !UsesGpuSurfaceRendering || useExpandedDisplayBounds)
        {
            return;
        }

        if (nativeConnectivityInFlight && SdfNativePlugin.sdf_is_connectivity_ready() != 0)
        {
            if (SdfNativePlugin.sdf_get_connectivity_result() != 0)
            {
                int removed = SdfNativePlugin.sdf_apply_removal();
                if (removed > 0)
                {
                    UploadSdfFromNative();
                    Debug.Log($"DETACHED_CLEANUP nativeRemovedCells={removed}", this);
                }
            }
            else
            {
                SdfNativePlugin.sdf_consume_connectivity_result();
            }

            nativeConnectivityInFlight = false;

            // If cutting continued while the snapshot was being analyzed,
            // immediately analyze the newest state instead of waiting another interval.
            if (nativeConnectivityDirty)
            {
                nextNativeConnectivityCheckTime = 0f;
            }
        }

        if (!nativeConnectivityInFlight &&
            nativeConnectivityDirty &&
            (!Application.isPlaying || Time.time >= nextNativeConnectivityCheckTime))
        {
            SdfNativePlugin.sdf_check_connectivity();
            nativeConnectivityInFlight = true;
            nativeConnectivityDirty = false;
            nextNativeConnectivityCheckTime = Time.time + detachedCleanupInterval;
        }
    }

    private void UploadSdfFromNative()
    {
        if (!sdfNativeReady || sdfSampleBuffer == null)
        {
            return;
        }

        try
        {
            EnsureSdfLinearSamples();
            SdfNativePlugin.sdf_get_data(sdfLinearSamples, sdfLinearSamples.Length);

            // Also update CPU sdfSamples for consistency
            CopyLinearToSdfSamples();

            // Upload to GPU
            sdfSampleBuffer.SetData(sdfLinearSamples);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native SDF upload failed: {ex.Message}", this);
        }
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

    private static Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        Vector3 segment = b - a;
        float lengthSqr = segment.sqrMagnitude;
        if (lengthSqr < 0.000001f)
        {
            return a;
        }

        float t = Vector3.Dot(point - a, segment) / lengthSqr;
        return a + segment * Mathf.Clamp01(t);
    }

    private static float GetSegmentT(Vector3 a, Vector3 b, Vector3 point)
    {
        Vector3 segment = b - a;
        float lengthSqr = segment.sqrMagnitude;
        if (lengthSqr < 0.000001f)
        {
            return 1f;
        }

        return Mathf.Clamp01(Vector3.Dot(point - a, segment) / lengthSqr);
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

    private float BoxSdf(Vector3 localPoint)
    {
        Vector3 halfSize = LocalSize * 0.5f;
        Vector3 q = new Vector3(
            Mathf.Abs(localPoint.x),
            Mathf.Abs(localPoint.y),
            Mathf.Abs(localPoint.z)) - halfSize;

        Vector3 outside = new Vector3(
            Mathf.Max(q.x, 0f),
            Mathf.Max(q.y, 0f),
            Mathf.Max(q.z, 0f));

        float inside = Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
        return outside.magnitude + inside;
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
