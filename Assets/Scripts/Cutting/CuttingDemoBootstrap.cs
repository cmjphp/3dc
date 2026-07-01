using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public enum CuttingPrecisionStorageMode
{
    DenseWorkpiece,
    LocalPrecisionWindow,
    SparseBricks
}

[DisallowMultipleComponent]
public sealed class CuttingDemoBootstrap : MonoBehaviour
{
    private const string CutterResourcesFolder = "Cutting";
    private const string DefaultCutterResourcePath = "Cutting/Cutter";
    private const string ThreadMillResourcePath = "Cutting/xd_luowenxidao";
    private static readonly Color DefaultWorkpieceColor = new Color(0.72f, 0.76f, 0.78f, 1f);
    public const float DefaultCutterDiameterMm = 6f;
    public const float DefaultCutterHeightMm = 3f;
    public const float MinWorkpieceSizeMm = 1f;
    public const float MaxWorkpieceSizeMm = 1000f;
    public const float MinCutterSizeMm = 0.001f;
    public const float MaxCutterSizeMm = 1000f;
    public const float MinCutterDirectionDegrees = -360f;
    public const float MaxCutterDirectionDegrees = 360f;
    public const float MinCutterSpeedMmPerSecond = 0.001f;
    public const float MaxCutterSpeedMmPerSecond = 10000f;

    [System.Serializable]
    public sealed class CutterToolPreset
    {
        public string displayName = "刀具";
        public GameObject modelPrefab;
        public string modelResourcePath;
        public Vector3 modelLocalEuler = new Vector3(-90f, 0f, 0f);
        public bool normalizeModelToRadius = true;
        public bool useProfileCutter = true;
        public bool preferNativeMeshCutter;
        [Min(2)] public int profileSegmentCount = 6;
        public Vector3 directionEuler;
        [Min(0.001f)] public float diameterMm = DefaultCutterDiameterMm;
        [Min(0.001f)] public float heightMm = DefaultCutterHeightMm;
    }

    [Header("Bootstrap")]
    public bool createOnStart = true;
    public bool skipIfWorkpieceExists = true;
    public bool useRealtimeDefaults = true;
    public bool showWorkpieceSizeUi = true;
    public bool showMeasurementUi = true;

    [Header("Precision Target")]
    public bool driveGridFromPrecisionTarget = true;
    public CuttingPrecisionStorageMode precisionStorageMode = CuttingPrecisionStorageMode.DenseWorkpiece;
    [Min(0.001f)] public float targetPrecisionMm = 0.35f;
    public Vector3 travelEnvelopeMm = new Vector3(1000f, 1000f, 1000f);
    public Vector3 workpieceSizeMm = new Vector3(100f, 100f, 100f);
    public Vector3 localPrecisionWindowMm = new Vector3(20f, 8f, 20f);
    [Min(8)] public int sparseBrickResolution = 64;
    [Min(1)] public int maxResidentSparseBricks = 4096;
    [Min(1)] public int maxResidentSparseGpuBricks = 96;
    [Min(1)] public int maxRenderedSparseGpuBricks = 96;
    [Min(1)] public int maxSparseGpuBrickCutsPerOperation = 24;
    [Min(100000)] public int maxSdfSamples = 64000000;

    [Header("Measurement")]
    [Min(0.0001f)] public float measurementPrecisionMm = 0.001f;
    [Min(1f)] public float defaultMeasurementMaxDistanceMm = 1500f;
    [Min(0.001f)] public float measurementMaxMarchStepMm = 1f;
    [Min(64)] public int maxMeasurementMarchIterations = 4096;
    [Min(1024)] public int maxMeasurementOperationHistory = 200000;

    [Header("Workpiece")]
    public int width = 116;
    public int height = 50;
    public int depth = 77;
    public float voxelSize = 0.025f;
    public WorkpieceBlankShape blankShape = WorkpieceBlankShape.Box;
    [Min(0f)] public float blankInnerRadiusMm = 20f;
    public GameObject importedBlankModelRoot;
    public string importedBlankModelResourcePath;
    public string importedBlankFilePath;
    public Vector3 importedBlankScalePercent = Vector3.one * 100f;
    public WorkpieceSurfaceMode surfaceMode = WorkpieceSurfaceMode.SmoothSdf;
    public bool updateCollider;
    [Min(100)] public int smoothRebuildCellsPerFrame = 2500;
    public bool useChunkedSmoothMesh = true;
    [Min(2)] public int chunkSize = 8;
    [Min(1)] public int chunkRebuildsPerFrame = 6;
    [Min(0)] public int dirtyChunkNeighborShell = 1;
    public bool rebuildCoreChunksImmediately = true;
    [Min(1)] public int immediateChunkRebuildLimit = 12;
    public bool removeDetachedParts = true;
    [Min(0.01f)] public float detachedCleanupInterval = 0.08f;
    [UnityEngine.Serialization.FormerlySerializedAs("useGpuSdfCutting")]
    public bool useGpuSdfDisplayBuffer;
    public bool useGpuSurfaceRendering = true;
    public bool useGpuVisualCutPreview;
    public bool useNativeCutDetailDisplay;
    [Min(10000)] public int maxGpuTriangles = 1000000;
    [Min(16)] public int gpuRaymarchMaxSteps = 192;
    [Range(0.25f, 2f)] public float gpuRaymarchStepScale = 0.75f;

    [Header("Cutter")]
    public float cutterRadius = DefaultCutterDiameterMm * 0.5f;
    public Vector3 cutterStartPosition = new Vector3(0f, 2.05f, 0f);
    public GameObject cutterModelPrefab;
    public string cutterModelResourcePath = DefaultCutterResourcePath;
    public Vector3 cutterModelLocalEuler = new Vector3(-90f, 0f, 0f);
    public bool normalizeCutterModelToRadius = true;
    public bool keepCutterVisible = true;
    [Min(1f)] public float minimumCutterVisualPixels = 14f;
    public bool useProfileCutter = true;
    public bool preferNativeMeshCutter;
    [Min(2)] public int cutterProfileSegmentCount = 6;
    public Vector3 cutterDirectionEuler;
    [Min(0.001f)] public float cutterHeightScale =
        DefaultCutterHeightMm / (DefaultCutterDiameterMm * 0.5f);

    [Header("Cutter Movement")]
    [Min(0.001f)] public float cutterMoveSpeedMmPerSecond = 50f;
    [Min(0.001f)] public float cutterVerticalSpeedMmPerSecond = 20f;
    [Min(1f)] public float cutterShiftMultiplier = 3f;
    [Min(0f)] public float initialCutDepthMm = 2f;

    [Header("Diagnostics")]
    public bool logCutDiagnostics;

    [Header("Cutter Library")]
    public int selectedCutterToolIndex;
    public bool autoDiscoverResourceCutters = true;
    [Min(1)] public int maxAutoDiscoveredCutterTools = 64;
    public CutterToolPreset[] cutterTools;

    [Header("Materials")]
    public Material workpieceMaterial;
    public Material cutterMaterial;

    private readonly float[] cutterProfileRadiusSamples =
        new float[WorkpieceVoxel.MaxCutterProfileRadiusSamples];
    private readonly float[] cutterAngularProfileMinRadiusSamples =
        new float[WorkpieceVoxel.MaxCutterAngularProfileSamples];
    private readonly float[] cutterAngularProfileMaxRadiusSamples =
        new float[WorkpieceVoxel.MaxCutterAngularProfileSamples];
    private int cutterProfileRadiusSampleCount;
    private int cutterAngularProfileAxialSampleCount;
    private int cutterAngularProfileAngleSampleCount;
    private WorkpieceVoxel activeWorkpiece;
    private CutterController activeCutter;

    private void Start()
    {
        workpieceSizeMm = SanitizeWorkpieceSize(workpieceSizeMm);
        EnsureCutterToolLibrary();
        if (createOnStart)
        {
            CreateDemo();
        }
        EnsureWorkpieceSizeUi();
    }

    [ContextMenu("Create Demo")]
    public void CreateDemo()
    {
        workpieceSizeMm = SanitizeWorkpieceSize(workpieceSizeMm);
        if (useRealtimeDefaults)
        {
            ApplyRealtimeDefaults();
        }
        else
        {
            ApplyPrecisionTarget();
        }

        WorkpieceVoxel existingWorkpiece = ResolveActiveWorkpiece();
        if (skipIfWorkpieceExists && existingWorkpiece != null)
        {
            ApplyWorkpieceSettings(existingWorkpiece);
            existingWorkpiece.ResetWorkpiece();
            activeWorkpiece = existingWorkpiece;
            ConfigureExistingCutter(existingWorkpiece);
            ConfigureCamera(existingWorkpiece);
            return;
        }

        WorkpieceVoxel workpiece = CreateWorkpiece();
        activeWorkpiece = workpiece;
        CreateCutter(workpiece);
        ConfigureCamera(workpiece);
        EnsureLight();
    }

    private void ApplyRealtimeDefaults()
    {
        // Workpiece geometry remains caller-owned. Cutter geometry has an
        // explicit startup default; runtime UI changes remain dynamic because
        // SetCutterParameters does not call this startup initializer again.
        driveGridFromPrecisionTarget = true;
        precisionStorageMode = CuttingPrecisionStorageMode.DenseWorkpiece;
        targetPrecisionMm = 0.35f;
        sparseBrickResolution = 64;
        maxResidentSparseBricks = 4096;
        maxResidentSparseGpuBricks = 96;
        maxRenderedSparseGpuBricks = 96;
        maxSparseGpuBrickCutsPerOperation = 24;
        maxSdfSamples = 64000000;
        measurementPrecisionMm = 0.001f;
        defaultMeasurementMaxDistanceMm = 1500f;
        measurementMaxMarchStepMm = 1f;
        maxMeasurementMarchIterations = 4096;
        maxMeasurementOperationHistory = 200000;
        ApplyPrecisionTarget();
        surfaceMode = WorkpieceSurfaceMode.SmoothSdf;
        updateCollider = false;
        smoothRebuildCellsPerFrame = 2500;
        useChunkedSmoothMesh = true;
        chunkSize = 8;
        chunkRebuildsPerFrame = 6;
        dirtyChunkNeighborShell = 1;
        rebuildCoreChunksImmediately = true;
        immediateChunkRebuildLimit = 12;
        removeDetachedParts = true;
        detachedCleanupInterval = 0.08f;
        useGpuSdfDisplayBuffer = false;
        useGpuSurfaceRendering = true;
        useGpuVisualCutPreview = false;
        useNativeCutDetailDisplay = false;
        maxGpuTriangles = 1000000;
        gpuRaymarchMaxSteps = Mathf.Max(gpuRaymarchMaxSteps, 512);
        gpuRaymarchStepScale = 0.5f;
        cutterModelLocalEuler = new Vector3(-90f, 0f, 0f);
        normalizeCutterModelToRadius = true;
        keepCutterVisible = true;
        minimumCutterVisualPixels = 14f;
        useProfileCutter = true;
        cutterProfileSegmentCount = 6;
        cutterDirectionEuler = Vector3.zero;
        cutterRadius = DefaultCutterDiameterMm * 0.5f;
        cutterHeightScale = DefaultCutterHeightMm / cutterRadius;
        EnsureCutterToolLibrary();
        selectedCutterToolIndex = 0;
        ApplyCutterToolPreset(selectedCutterToolIndex, false);
        cutterMoveSpeedMmPerSecond = 50f;
        cutterVerticalSpeedMmPerSecond = 20f;
        cutterShiftMultiplier = 3f;
        initialCutDepthMm = 2f;
    }

    private void ApplyPrecisionTarget()
    {
        if (!driveGridFromPrecisionTarget)
        {
            return;
        }

        Vector3 fullSize = SanitizeWorkpieceSize(workpieceSizeMm);
        Vector3 localWindowSize = SanitizeSize(localPrecisionWindowMm);
        Vector3 size = precisionStorageMode == CuttingPrecisionStorageMode.LocalPrecisionWindow
            || precisionStorageMode == CuttingPrecisionStorageMode.SparseBricks
            ? localWindowSize
            : fullSize;

        float requestedVoxelSize = Mathf.Max(0.001f, targetPrecisionMm);
        float actualVoxelSize = requestedVoxelSize;
        int nextWidth = Mathf.Max(1, Mathf.CeilToInt(size.x / actualVoxelSize));
        int nextHeight = Mathf.Max(1, Mathf.CeilToInt(size.y / actualVoxelSize));
        int nextDepth = Mathf.Max(1, Mathf.CeilToInt(size.z / actualVoxelSize));
        long sampleCount = CountSdfSamples(nextWidth, nextHeight, nextDepth);
        long fullDenseSampleCount = CountSdfSamplesForSize(fullSize, requestedVoxelSize);
        int safeMaxSamples = Mathf.Max(100000, maxSdfSamples);

        if (precisionStorageMode == CuttingPrecisionStorageMode.DenseWorkpiece && fullDenseSampleCount > safeMaxSamples)
        {
            Debug.LogWarning(
                $"Dense {requestedVoxelSize:0.####}mm SDF for {fullSize.x:0.###}x{fullSize.y:0.###}x{fullSize.z:0.###}mm " +
                $"would need {fullDenseSampleCount:n0} samples. Use LocalPrecisionWindow or sparse bricks for long travel.");
        }

        if ((precisionStorageMode == CuttingPrecisionStorageMode.LocalPrecisionWindow
                || precisionStorageMode == CuttingPrecisionStorageMode.SparseBricks)
            && fullDenseSampleCount > safeMaxSamples)
        {
            Debug.Log(
                $"Using local high-precision window {size.x:0.###}x{size.y:0.###}x{size.z:0.###}mm at {requestedVoxelSize:0.####}mm with {precisionStorageMode}. " +
                $"Full travel envelope dense allocation would be {fullDenseSampleCount:n0} samples.");
        }

        if (sampleCount > safeMaxSamples)
        {
            float relaxation = Mathf.Pow(sampleCount / (float)safeMaxSamples, 1f / 3f);
            actualVoxelSize = requestedVoxelSize * relaxation;
            nextWidth = Mathf.Max(1, Mathf.CeilToInt(size.x / actualVoxelSize));
            nextHeight = Mathf.Max(1, Mathf.CeilToInt(size.y / actualVoxelSize));
            nextDepth = Mathf.Max(1, Mathf.CeilToInt(size.z / actualVoxelSize));
            sampleCount = CountSdfSamples(nextWidth, nextHeight, nextDepth);

            Debug.LogWarning(
                $"Requested precision {requestedVoxelSize:0.####}mm would allocate too many SDF samples. " +
                $"Using {actualVoxelSize:0.####}mm instead ({sampleCount:n0} samples).");
        }

        width = nextWidth;
        height = nextHeight;
        depth = nextDepth;
        voxelSize = actualVoxelSize;

        int recommendedRaymarchSteps = Mathf.CeilToInt(Mathf.Max(size.x, size.y, size.z) / Mathf.Max(actualVoxelSize * 0.75f, 0.0001f));
        gpuRaymarchMaxSteps = Mathf.Clamp(recommendedRaymarchSteps, 256, 1024);
    }

    private static long CountSdfSamples(int gridWidth, int gridHeight, int gridDepth)
    {
        return (long)(gridWidth + 3) * (gridHeight + 3) * (gridDepth + 3);
    }

    private static long CountSdfSamplesForSize(Vector3 sizeMm, float voxelSizeMm)
    {
        int gridWidth = Mathf.Max(1, Mathf.CeilToInt(sizeMm.x / voxelSizeMm));
        int gridHeight = Mathf.Max(1, Mathf.CeilToInt(sizeMm.y / voxelSizeMm));
        int gridDepth = Mathf.Max(1, Mathf.CeilToInt(sizeMm.z / voxelSizeMm));
        return CountSdfSamples(gridWidth, gridHeight, gridDepth);
    }

    private static Vector3 SanitizeSize(Vector3 size)
    {
        return new Vector3(
            Mathf.Max(0.001f, size.x),
            Mathf.Max(0.001f, size.y),
            Mathf.Max(0.001f, size.z));
    }

    private static Vector3 SanitizeWorkpieceSize(Vector3 size)
    {
        return new Vector3(
            Mathf.Clamp(size.x, MinWorkpieceSizeMm, MaxWorkpieceSizeMm),
            Mathf.Clamp(size.y, MinWorkpieceSizeMm, MaxWorkpieceSizeMm),
            Mathf.Clamp(size.z, MinWorkpieceSizeMm, MaxWorkpieceSizeMm));
    }

    public void SetWorkpieceSizeMm(Vector3 requestedSizeMm)
    {
        SetWorkpieceDefinition(
            requestedSizeMm,
            blankShape,
            blankInnerRadiusMm,
            importedBlankModelResourcePath);
    }

    public void SetWorkpieceBlank(
        WorkpieceBlankShape requestedShape,
        float requestedInnerRadiusMm,
        string requestedImportedResourcePath)
    {
        SetWorkpieceDefinition(
            workpieceSizeMm,
            requestedShape,
            requestedInnerRadiusMm,
            requestedImportedResourcePath);
    }

    public bool SetWorkpieceDefinition(
        Vector3 requestedSizeMm,
        WorkpieceBlankShape requestedShape,
        float requestedInnerRadiusMm,
        string requestedImportedResourcePath)
    {
        blankShape = requestedShape;
        blankInnerRadiusMm = Mathf.Max(0f, requestedInnerRadiusMm);
        string importedPath = requestedImportedResourcePath ?? string.Empty;
        importedBlankFilePath = ImportedStlMeshLoader.IsStlPath(importedPath)
            ? importedPath.Trim().Replace('\\', '/')
            : string.Empty;
        importedBlankModelResourcePath = string.IsNullOrEmpty(importedBlankFilePath)
            ? SanitizeResourcePath(importedPath)
            : string.Empty;
        if (!string.IsNullOrEmpty(importedBlankModelResourcePath) ||
            !string.IsNullOrEmpty(importedBlankFilePath))
        {
            importedBlankModelRoot = null;
        }

        if (blankShape == WorkpieceBlankShape.ImportedMesh)
        {
            importedBlankScalePercent = SanitizeImportedScalePercent(requestedSizeMm);
            if (TryResolveImportedBlankSize(importedBlankScalePercent, out Vector3 importedSize))
            {
                workpieceSizeMm = SanitizeWorkpieceSize(importedSize);
            }
            else
            {
                Debug.LogWarning(
                    $"Imported blank '{importedBlankModelResourcePath}' could not be resolved or has no readable bounds.");
                return false;
            }
        }
        else
        {
            workpieceSizeMm = SanitizeWorkpieceSize(requestedSizeMm);
        }

        ApplyPrecisionTarget();

        WorkpieceVoxel workpiece = ResolveActiveWorkpiece();
        if (workpiece == null)
        {
            CreateDemo();
            workpiece = ResolveActiveWorkpiece();
            return workpiece != null &&
                (surfaceMode != WorkpieceSurfaceMode.SmoothSdf || workpiece.IsNativeSdfReady);
        }

        ApplyWorkpieceSettings(workpiece);
        workpiece.ResetWorkpiece();
        activeWorkpiece = workpiece;
        bool ready = surfaceMode != WorkpieceSurfaceMode.SmoothSdf || workpiece.IsNativeSdfReady;
        if (ready)
        {
            workpiece.RequestRebuildMesh();
        }

        ConfigureExistingCutter(workpiece);
        ConfigureCamera(workpiece);
        return ready;
    }

    public void SetCutterParameters(
        float requestedDiameterMm,
        float requestedHeightMm,
        float requestedMoveSpeedMmPerSecond,
        float requestedVerticalSpeedMmPerSecond)
    {
        float diameter = Mathf.Clamp(requestedDiameterMm, MinCutterSizeMm, MaxCutterSizeMm);
        float cutterHeight = Mathf.Clamp(requestedHeightMm, MinCutterSizeMm, MaxCutterSizeMm);
        cutterRadius = diameter * 0.5f;
        cutterHeightScale = cutterHeight / cutterRadius;
        cutterMoveSpeedMmPerSecond = Mathf.Clamp(
            requestedMoveSpeedMmPerSecond,
            MinCutterSpeedMmPerSecond,
            MaxCutterSpeedMmPerSecond);
        cutterVerticalSpeedMmPerSecond = Mathf.Clamp(
            requestedVerticalSpeedMmPerSecond,
            MinCutterSpeedMmPerSecond,
            MaxCutterSpeedMmPerSecond);

        CutterController cutter = ResolveActiveCutter();
        if (cutter == null)
        {
            return;
        }

        cutter.cutterRadius = cutterRadius;
        cutter.cutterHeightScale = cutterHeightScale;
        cutter.moveSpeed = cutterMoveSpeedMmPerSecond;
        cutter.verticalSpeed = cutterVerticalSpeedMmPerSecond;
        cutter.useProfileCutter = useProfileCutter;
        cutter.transform.rotation = Quaternion.Euler(cutterDirectionEuler);
        ApplyCutterProfileSettingsToScene(cutter.workpiece);
        cutter.ResetCutSweep();
        SetupCutterVisual(cutter.gameObject);
        RefreshCutterProfileFromVisual(cutter);
    }

    public void SetCutterDirectionEuler(Vector3 requestedEuler)
    {
        cutterDirectionEuler = SanitizeCutterDirectionEuler(requestedEuler);

        CutterController cutter = ResolveActiveCutter();
        if (cutter == null)
        {
            return;
        }

        cutter.transform.rotation = Quaternion.Euler(cutterDirectionEuler);
        cutter.ResetCutSweep();
    }

    public string[] GetCutterToolNames()
    {
        EnsureCutterToolLibrary();
        string[] names = new string[cutterTools.Length];
        for (int i = 0; i < cutterTools.Length; i++)
        {
            names[i] = GetCutterToolName(i);
        }

        return names;
    }

    public string SelectedCutterToolName
    {
        get { return GetCutterToolName(selectedCutterToolIndex); }
    }

    public void SelectCutterTool(int index)
    {
        EnsureCutterToolLibrary();
        if (cutterTools.Length == 0)
        {
            return;
        }

        selectedCutterToolIndex = Mathf.Clamp(index, 0, cutterTools.Length - 1);
        ApplyCutterToolPreset(selectedCutterToolIndex, true);
    }

    private string GetCutterToolName(int index)
    {
        EnsureCutterToolLibrary();
        if (cutterTools.Length == 0 || index < 0 || index >= cutterTools.Length)
        {
            return "刀具";
        }

        string displayName = cutterTools[index].displayName;
        return string.IsNullOrWhiteSpace(displayName) ? $"刀具 {index + 1}" : displayName;
    }

    private void EnsureCutterToolLibrary()
    {
        if (cutterTools == null || cutterTools.Length == 0)
        {
            cutterTools = new[]
            {
                CreateDefaultCutterPreset()
            };
        }

        for (int i = 0; i < cutterTools.Length; i++)
        {
            if (cutterTools[i] == null)
            {
                cutterTools[i] = i == 0 ? CreateDefaultCutterPreset() : CreateGenericCutterPreset(i);
            }

            NormalizeCutterToolPreset(cutterTools[i], i);
        }

        if (autoDiscoverResourceCutters)
        {
            AddDiscoveredResourceCutterTools();
        }
        else if (!HasCutterToolResource(ThreadMillResourcePath) &&
            Resources.Load<GameObject>(ThreadMillResourcePath) != null)
        {
            int oldLength = cutterTools.Length;
            System.Array.Resize(ref cutterTools, oldLength + 1);
            cutterTools[oldLength] = CreateThreadMillPreset();
        }

        selectedCutterToolIndex = Mathf.Clamp(selectedCutterToolIndex, 0, cutterTools.Length - 1);
    }

    private static CutterToolPreset CreateDefaultCutterPreset()
    {
        return new CutterToolPreset
        {
            displayName = "默认刀具",
            modelResourcePath = DefaultCutterResourcePath,
            modelLocalEuler = new Vector3(-90f, 0f, 0f),
            normalizeModelToRadius = true,
            useProfileCutter = true,
            preferNativeMeshCutter = false,
            profileSegmentCount = 6,
            directionEuler = Vector3.zero,
            diameterMm = DefaultCutterDiameterMm,
            heightMm = DefaultCutterHeightMm
        };
    }

    private static CutterToolPreset CreateThreadMillPreset()
    {
        Vector3 modelEuler = new Vector3(-90f, 0f, 0f);
        float inferredHeight = InferCutterHeightFromModel(
            Resources.Load<GameObject>(ThreadMillResourcePath),
            modelEuler,
            DefaultCutterDiameterMm,
            18f);

        return new CutterToolPreset
        {
            displayName = "螺纹铣刀",
            modelResourcePath = ThreadMillResourcePath,
            modelLocalEuler = modelEuler,
            normalizeModelToRadius = true,
            useProfileCutter = true,
            preferNativeMeshCutter = true,
            profileSegmentCount = 24,
            directionEuler = Vector3.zero,
            diameterMm = DefaultCutterDiameterMm,
            heightMm = inferredHeight
        };
    }

    private static CutterToolPreset CreateGenericCutterPreset(int index)
    {
        return new CutterToolPreset
        {
            displayName = $"刀具 {index + 1}",
            modelResourcePath = DefaultCutterResourcePath,
            modelLocalEuler = new Vector3(-90f, 0f, 0f),
            normalizeModelToRadius = true,
            useProfileCutter = true,
            preferNativeMeshCutter = false,
            profileSegmentCount = 6,
            directionEuler = Vector3.zero,
            diameterMm = DefaultCutterDiameterMm,
            heightMm = DefaultCutterHeightMm
        };
    }

    private void AddDiscoveredResourceCutterTools()
    {
        GameObject[] resourceModels = Resources.LoadAll<GameObject>(CutterResourcesFolder);
        if (resourceModels == null || resourceModels.Length == 0)
        {
            return;
        }

        System.Array.Sort(resourceModels, (left, right) =>
            string.Compare(
                left != null ? left.name : string.Empty,
                right != null ? right.name : string.Empty,
                System.StringComparison.OrdinalIgnoreCase));

        int addedCount = 0;
        int maxAddedCount = Mathf.Max(1, maxAutoDiscoveredCutterTools);
        for (int i = 0; i < resourceModels.Length && addedCount < maxAddedCount; i++)
        {
            GameObject model = resourceModels[i];
            if (model == null || HasCutterToolModel(model))
            {
                continue;
            }

            int oldLength = cutterTools.Length;
            System.Array.Resize(ref cutterTools, oldLength + 1);
            cutterTools[oldLength] = CreateDiscoveredResourceCutterPreset(model);
            addedCount++;
        }
    }

    private static CutterToolPreset CreateDiscoveredResourceCutterPreset(GameObject model)
    {
        Vector3 modelEuler = new Vector3(-90f, 0f, 0f);
        float diameterMm = DefaultCutterDiameterMm;
        float heightMm = InferCutterHeightFromModel(
            model,
            modelEuler,
            diameterMm,
            DefaultCutterHeightMm);

        return new CutterToolPreset
        {
            displayName = ToDisplayName(model != null ? model.name : "刀具"),
            modelPrefab = model,
            modelLocalEuler = modelEuler,
            normalizeModelToRadius = true,
            useProfileCutter = true,
            preferNativeMeshCutter = true,
            profileSegmentCount = 24,
            directionEuler = Vector3.zero,
            diameterMm = diameterMm,
            heightMm = heightMm
        };
    }

    private static void NormalizeCutterToolPreset(CutterToolPreset preset, int index)
    {
        if (string.IsNullOrWhiteSpace(preset.displayName))
        {
            preset.displayName = index == 0 ? "默认刀具" : "刀具";
        }

        if (string.IsNullOrWhiteSpace(preset.modelResourcePath) && preset.modelPrefab == null)
        {
            preset.modelResourcePath = DefaultCutterResourcePath;
        }

        preset.diameterMm = Mathf.Clamp(preset.diameterMm, MinCutterSizeMm, MaxCutterSizeMm);
        preset.heightMm = Mathf.Clamp(preset.heightMm, MinCutterSizeMm, MaxCutterSizeMm);
        preset.profileSegmentCount = Mathf.Max(2, preset.profileSegmentCount);

        GameObject model = ResolvePresetModelPrefab(preset);
        bool isDefaultModel = model == null || ResourceNameMatches(DefaultCutterResourcePath, model.name);
        if (!isDefaultModel && Mathf.Approximately(preset.heightMm, DefaultCutterHeightMm))
        {
            preset.heightMm = InferCutterHeightFromModel(
                model,
                preset.modelLocalEuler,
                preset.diameterMm,
                preset.heightMm);
        }

        if (!isDefaultModel && preset.profileSegmentCount == 6)
        {
            preset.profileSegmentCount = 24;
        }

        if (!isDefaultModel)
        {
            preset.preferNativeMeshCutter = true;
        }
    }

    private bool HasCutterToolResource(string resourcePath)
    {
        for (int i = 0; i < cutterTools.Length; i++)
        {
            CutterToolPreset preset = cutterTools[i];
            if (preset != null && preset.modelResourcePath == resourcePath)
            {
                return true;
            }
        }

        return false;
    }

    private static GameObject ResolvePresetModelPrefab(CutterToolPreset preset)
    {
        if (preset == null)
        {
            return null;
        }

        if (preset.modelPrefab != null)
        {
            return preset.modelPrefab;
        }

        if (string.IsNullOrWhiteSpace(preset.modelResourcePath))
        {
            return null;
        }

        return Resources.Load<GameObject>(preset.modelResourcePath);
    }

    private bool HasCutterToolModel(GameObject model)
    {
        if (model == null || cutterTools == null)
        {
            return false;
        }

        string modelName = model.name;
        for (int i = 0; i < cutterTools.Length; i++)
        {
            CutterToolPreset preset = cutterTools[i];
            if (preset == null)
            {
                continue;
            }

            if (preset.modelPrefab == model ||
                (preset.modelPrefab != null && preset.modelPrefab.name == modelName) ||
                ResourceNameMatches(preset.modelResourcePath, modelName))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ResourceNameMatches(string resourcePath, string modelName)
    {
        if (string.IsNullOrWhiteSpace(resourcePath) || string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        int separatorIndex = resourcePath.LastIndexOf('/');
        string resourceName = separatorIndex >= 0
            ? resourcePath.Substring(separatorIndex + 1)
            : resourcePath;
        return resourceName == modelName;
    }

    private static string ToDisplayName(string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return "刀具";
        }

        return resourceName.Replace('_', ' ').Replace('-', ' ');
    }

    private static float InferCutterHeightFromModel(
        GameObject modelPrefab,
        Vector3 modelLocalEuler,
        float diameterMm,
        float fallbackHeightMm)
    {
        if (modelPrefab == null)
        {
            return Mathf.Clamp(fallbackHeightMm, MinCutterSizeMm, MaxCutterSizeMm);
        }

        GameObject probeRoot = new GameObject("Cutter Bounds Probe");
        probeRoot.hideFlags = HideFlags.HideAndDontSave;
        GameObject model = null;
        try
        {
            model = Object.Instantiate(modelPrefab, probeRoot.transform);
            model.hideFlags = HideFlags.HideAndDontSave;
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.Euler(modelLocalEuler);
            model.transform.localScale = Vector3.one;

            if (!TryGetRendererBoundsInSpace(model, probeRoot.transform, out Bounds bounds))
            {
                return Mathf.Clamp(fallbackHeightMm, MinCutterSizeMm, MaxCutterSizeMm);
            }

            float radialSize = Mathf.Max(bounds.size.x, bounds.size.z);
            if (radialSize <= 0.000001f || bounds.size.y <= 0.000001f)
            {
                return Mathf.Clamp(fallbackHeightMm, MinCutterSizeMm, MaxCutterSizeMm);
            }

            float aspectHeight = bounds.size.y / radialSize;
            float inferredHeight = Mathf.Max(MinCutterSizeMm, diameterMm) * aspectHeight;
            return Mathf.Clamp(inferredHeight, MinCutterSizeMm, MaxCutterSizeMm);
        }
        finally
        {
            if (model != null)
            {
                DestroyProbeObject(model);
            }

            DestroyProbeObject(probeRoot);
        }
    }

    private static void DestroyProbeObject(GameObject target)
    {
        if (target == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Object.Destroy(target);
        }
        else
        {
            Object.DestroyImmediate(target);
        }
    }

    private void ApplyCutterToolPreset(int index, bool updateExistingCutter)
    {
        EnsureCutterToolLibrary();
        if (cutterTools.Length == 0)
        {
            return;
        }

        selectedCutterToolIndex = Mathf.Clamp(index, 0, cutterTools.Length - 1);
        CutterToolPreset preset = cutterTools[selectedCutterToolIndex];
        float diameter = Mathf.Clamp(preset.diameterMm, MinCutterSizeMm, MaxCutterSizeMm);
        float cutterHeight = Mathf.Clamp(preset.heightMm, MinCutterSizeMm, MaxCutterSizeMm);
        cutterRadius = diameter * 0.5f;
        cutterHeightScale = cutterHeight / cutterRadius;
        cutterModelPrefab = preset.modelPrefab;
        cutterModelResourcePath = preset.modelResourcePath;
        cutterModelLocalEuler = preset.modelLocalEuler;
        normalizeCutterModelToRadius = preset.normalizeModelToRadius;
        useProfileCutter = preset.useProfileCutter;
        preferNativeMeshCutter = preset.preferNativeMeshCutter;
        cutterProfileSegmentCount = Mathf.Max(2, preset.profileSegmentCount);
        cutterDirectionEuler = SanitizeCutterDirectionEuler(preset.directionEuler);

        if (!updateExistingCutter)
        {
            return;
        }

        CutterController cutter = ResolveActiveCutter();
        if (cutter == null)
        {
            return;
        }

        cutter.cutterRadius = cutterRadius;
        cutter.cutterHeightScale = cutterHeightScale;
        cutter.useProfileCutter = useProfileCutter;
        cutter.transform.rotation = Quaternion.Euler(cutterDirectionEuler);
        ApplyCutterProfileSettingsToScene(cutter.workpiece);
        cutter.ResetCutSweep();
        SetupCutterVisual(cutter.gameObject);
        RefreshCutterProfileFromVisual(cutter);
        activeCutter = cutter;
    }

    private void EnsureWorkpieceSizeUi()
    {
        WorkpieceSizeRuntimeUi sizeUi = GetComponent<WorkpieceSizeRuntimeUi>();
        if (!showWorkpieceSizeUi)
        {
            if (sizeUi != null)
            {
                sizeUi.enabled = false;
            }
            return;
        }

        if (sizeUi == null)
        {
            sizeUi = gameObject.AddComponent<WorkpieceSizeRuntimeUi>();
        }

        sizeUi.Configure(this);
        sizeUi.enabled = true;
    }

    private WorkpieceVoxel CreateWorkpiece()
    {
        GameObject workpieceObject = new GameObject("Voxel Workpiece");
        workpieceObject.transform.position = Vector3.zero;

        MeshRenderer renderer = workpieceObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = workpieceMaterial != null
            ? workpieceMaterial
            : CreateCompatibleMaterial("Voxel Workpiece Material", DefaultWorkpieceColor);

        WorkpieceVoxel workpiece = workpieceObject.AddComponent<WorkpieceVoxel>();
        ApplyWorkpieceSettings(workpiece);
        workpiece.initializeOnStart = false;
        workpiece.ResetWorkpiece();
        activeWorkpiece = workpiece;

        return workpiece;
    }

    private void ApplyWorkpieceSettings(WorkpieceVoxel workpiece)
    {
        workpiece.width = Mathf.Max(1, width);
        workpiece.height = Mathf.Max(1, height);
        workpiece.depth = Mathf.Max(1, depth);
        workpiece.voxelSize = Mathf.Max(0.001f, voxelSize);
        workpiece.maxAllocatedSdfSamples = Mathf.Max(100000, maxSdfSamples);
        workpiece.blankShape = blankShape;
        workpiece.blankInnerRadius = Mathf.Max(0f, blankInnerRadiusMm);
        workpiece.importedBlankMeshRoot = ResolveImportedBlankModelRoot();
        workpiece.importedBlankFilePath = importedBlankFilePath;
        workpiece.importedBlankScalePercent = SanitizeImportedScalePercent(importedBlankScalePercent);
        workpiece.surfaceMode = surfaceMode;
        workpiece.asyncSmoothMeshRebuild = true;
        workpiece.smoothRebuildCellsPerFrame = Mathf.Max(100, smoothRebuildCellsPerFrame);
        workpiece.useChunkedSmoothMesh = useChunkedSmoothMesh && !useGpuSurfaceRendering;
        workpiece.chunkSize = Mathf.Max(2, chunkSize);
        workpiece.chunkRebuildsPerFrame = Mathf.Max(1, chunkRebuildsPerFrame);
        workpiece.dirtyChunkNeighborShell = Mathf.Max(0, dirtyChunkNeighborShell);
        workpiece.rebuildCoreChunksImmediately = rebuildCoreChunksImmediately;
        workpiece.immediateChunkRebuildLimit = Mathf.Max(1, immediateChunkRebuildLimit);
        workpiece.removeDetachedParts = removeDetachedParts;
        workpiece.detachedCleanupInterval = Mathf.Max(0.01f, detachedCleanupInterval);
        workpiece.useGpuSdfDisplayBuffer = useGpuSdfDisplayBuffer;
        workpiece.useGpuSurfaceRendering = useGpuSurfaceRendering;
        workpiece.useGpuVisualCutPreview = useGpuVisualCutPreview;
        workpiece.useNativeCutDetailDisplay = useNativeCutDetailDisplay;
        workpiece.gpuSurfaceColor = DefaultWorkpieceColor;
        workpiece.logNativeCutDiagnostics = logCutDiagnostics;
        workpiece.ClearDisplayOverlays();
        ApplyDefaultWorkpieceMaterial(workpiece);
        Vector3 detailSize = new Vector3(workpiece.width, workpiece.height, workpiece.depth) * workpiece.voxelSize;
        Vector3 displaySize = SanitizeWorkpieceSize(workpieceSizeMm);
        workpiece.useExpandedDisplayBounds = precisionStorageMode != CuttingPrecisionStorageMode.DenseWorkpiece;
        workpiece.expandedDisplaySize = displaySize;
        workpiece.expandedDisplayCenter = new Vector3(0f, (detailSize.y - displaySize.y) * 0.5f, 0f);
        workpiece.maxGpuTriangles = Mathf.Max(10000, maxGpuTriangles);
        workpiece.gpuRaymarchMaxSteps = Mathf.Max(16, gpuRaymarchMaxSteps);
        workpiece.gpuRaymarchStepScale = Mathf.Clamp(gpuRaymarchStepScale, 0.25f, 2f);
        workpiece.updateCollider = updateCollider;
        workpiece.initializeOnStart = false;
        workpiece.SetProfileSegmentCount(cutterProfileSegmentCount);
        ConfigureWorkpieceMeasurement(workpiece);
    }

    private GameObject ResolveImportedBlankModelRoot()
    {
        if (importedBlankModelRoot != null)
        {
            return importedBlankModelRoot;
        }

        string resourcePath = SanitizeResourcePath(importedBlankModelResourcePath);
        if (string.IsNullOrEmpty(resourcePath))
        {
            return null;
        }

        return Resources.Load<GameObject>(resourcePath);
    }

    private static string SanitizeResourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string sanitized = path.Trim().Replace('\\', '/');
        const string resourcesPrefix = "Assets/Resources/";
        if (sanitized.StartsWith(resourcesPrefix, System.StringComparison.OrdinalIgnoreCase))
        {
            sanitized = sanitized.Substring(resourcesPrefix.Length);
        }

        int extensionIndex = sanitized.LastIndexOf('.');
        if (extensionIndex > 0)
        {
            sanitized = sanitized.Substring(0, extensionIndex);
        }

        return sanitized;
    }

    private static Vector3 SanitizeImportedScalePercent(Vector3 scalePercent)
    {
        return new Vector3(
            Mathf.Clamp(scalePercent.x, 0.001f, 1000f),
            Mathf.Clamp(scalePercent.y, 0.001f, 1000f),
            Mathf.Clamp(scalePercent.z, 0.001f, 1000f));
    }

    private bool TryResolveImportedBlankSize(
        Vector3 scalePercent,
        out Vector3 sizeMm)
    {
        sizeMm = Vector3.zero;
        if (ImportedStlMeshLoader.IsStlPath(importedBlankFilePath))
        {
            float[] stlBounds6 = new float[6];
            if (SdfNativePlugin.sdf_get_stl_bounds(
                    importedBlankFilePath,
                    scalePercent.x,
                    scalePercent.y,
                    scalePercent.z,
                    stlBounds6,
                    out int triangleCount) == 0)
            {
                Debug.LogWarning($"Imported STL '{importedBlankFilePath}' could not be resolved by the native parser.");
                return false;
            }

            Vector3 min = new Vector3(stlBounds6[0], stlBounds6[1], stlBounds6[2]);
            Vector3 max = new Vector3(stlBounds6[3], stlBounds6[4], stlBounds6[5]);
            sizeMm = max - min;
            Debug.Log(
                $"IMPORTED_STL_NATIVE_BOUNDS path={importedBlankFilePath} " +
                $"triangles={triangleCount:n0} bounds=({min}..{max}) size={sizeMm}");
            return sizeMm.x > 0f && sizeMm.y > 0f && sizeMm.z > 0f;
        }

        GameObject importedRoot = ResolveImportedBlankModelRoot();
        if (importedRoot == null)
        {
            return false;
        }

        bool hasBounds = false;
        Bounds bounds = default;
        MeshFilter[] filters = importedRoot.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            EncapsulateMeshBounds(importedRoot.transform, filters[i].transform, filters[i].sharedMesh, ref bounds, ref hasBounds);
        }

        SkinnedMeshRenderer[] skinnedRenderers = importedRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinnedRenderers.Length; i++)
        {
            EncapsulateMeshBounds(importedRoot.transform, skinnedRenderers[i].transform, skinnedRenderers[i].sharedMesh, ref bounds, ref hasBounds);
        }

        if (!hasBounds)
        {
            return false;
        }

        Vector3 scale = SanitizeImportedScalePercent(scalePercent) * 0.01f;
        sizeMm = Vector3.Scale(bounds.size, scale);
        return sizeMm.x > 0f && sizeMm.y > 0f && sizeMm.z > 0f;
    }

    private static void EncapsulateMeshBounds(
        Transform root,
        Transform meshTransform,
        Mesh mesh,
        ref Bounds bounds,
        ref bool hasBounds)
    {
        if (root == null || meshTransform == null || mesh == null)
        {
            return;
        }

        Bounds meshBounds = mesh.bounds;
        Vector3 min = meshBounds.min;
        Vector3 max = meshBounds.max;
        for (int x = 0; x <= 1; x++)
        {
            for (int y = 0; y <= 1; y++)
            {
                for (int z = 0; z <= 1; z++)
                {
                    Vector3 localCorner = new Vector3(
                        x == 0 ? min.x : max.x,
                        y == 0 ? min.y : max.y,
                        z == 0 ? min.z : max.z);
                    Vector3 rootLocal = root.InverseTransformPoint(meshTransform.TransformPoint(localCorner));
                    if (!hasBounds)
                    {
                        bounds = new Bounds(rootLocal, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(rootLocal);
                    }
                }
            }
        }
    }

    private void ApplyDefaultWorkpieceMaterial(WorkpieceVoxel workpiece)
    {
        MeshRenderer renderer = workpiece.GetComponent<MeshRenderer>();
        if (renderer == null || workpieceMaterial != null)
        {
            return;
        }

        if (renderer.sharedMaterial == null)
        {
            renderer.sharedMaterial = CreateCompatibleMaterial("Voxel Workpiece Material", DefaultWorkpieceColor);
            return;
        }

        SetMaterialColor(renderer.sharedMaterial, DefaultWorkpieceColor);
    }

    private void ConfigureWorkpieceMeasurement(WorkpieceVoxel workpiece)
    {
        WorkpieceMeasurement measurement = workpiece.GetComponent<WorkpieceMeasurement>();
        if (measurement == null)
        {
            measurement = workpiece.gameObject.AddComponent<WorkpieceMeasurement>();
        }

        measurement.workpiece = workpiece;
        measurement.measurementPrecisionMm = Mathf.Clamp(measurementPrecisionMm, 0.000001f, 0.001f);
        measurement.defaultMaxDistanceMm = Mathf.Max(0.001f, defaultMeasurementMaxDistanceMm);
        measurement.maxMarchStepMm = Mathf.Max(measurement.measurementPrecisionMm, measurementMaxMarchStepMm);
        measurement.maxMarchIterations = Mathf.Max(64, maxMeasurementMarchIterations);
        ConfigureMeasurementUi(measurement);
    }

    private void ConfigureMeasurementUi(WorkpieceMeasurement measurement)
    {
        WorkpieceMeasurementRuntimeUi ui = GetComponent<WorkpieceMeasurementRuntimeUi>();
        if (!showMeasurementUi)
        {
            if (ui != null)
            {
                ui.enabled = false;
            }

            return;
        }

        if (ui == null)
        {
            ui = gameObject.AddComponent<WorkpieceMeasurementRuntimeUi>();
        }

        ui.Configure(measurement);
        ui.enabled = true;
    }

    private void CreateCutter(WorkpieceVoxel workpiece)
    {
        GameObject cutterObject = new GameObject("Cutter Tool");
        cutterObject.transform.position = GetCutterStartWorldPosition(workpiece);

        CutterController cutter = cutterObject.AddComponent<CutterController>();
        ConfigureCutter(cutter, workpiece);
        cutter.ResetCutSweep();
        SetupCutterVisual(cutterObject);
        RefreshCutterProfileFromVisual(cutter);
        activeCutter = cutter;
    }

    private void ConfigureExistingCutter(WorkpieceVoxel workpiece)
    {
        CutterController cutter = ResolveActiveCutter();
        if (cutter == null)
        {
            CreateCutter(workpiece);
            return;
        }

        ConfigureCutter(cutter, workpiece);
        cutter.transform.position = GetCutterStartWorldPosition(workpiece);
        cutter.transform.localScale = Vector3.one;
        cutter.ResetCutSweep();
        SetupCutterVisual(cutter.gameObject);
        RefreshCutterProfileFromVisual(cutter);
        activeCutter = cutter;
    }

    private Vector3 GetCutterStartWorldPosition(WorkpieceVoxel workpiece)
    {
        Vector3 localStart = cutterStartPosition;
        float cutDepth = Mathf.Clamp(initialCutDepthMm, 0f, workpiece.LocalSize.y);
        localStart.y = workpiece.LocalSize.y * 0.5f - cutDepth;
        return workpiece.transform.TransformPoint(localStart);
    }

    private void ConfigureCutter(CutterController cutter, WorkpieceVoxel workpiece)
    {
        cutter.workpiece = workpiece;
        cutter.cutterRadius = cutterRadius;
        cutter.moveSpeed = Mathf.Max(0.001f, cutterMoveSpeedMmPerSecond);
        cutter.verticalSpeed = Mathf.Max(0.001f, cutterVerticalSpeedMmPerSecond);
        cutter.shiftMultiplier = Mathf.Max(1f, cutterShiftMultiplier);
        cutter.cutInterval = 0.001f;
        cutter.meshRebuildInterval = 0.001f;
        cutter.cutContinuously = true;
        cutter.sweepBetweenCutPositions = true;
        cutter.cutEveryFrame = true;
        cutter.requestMeshRebuildAfterEachCut = true;
        cutter.useProfileCutter = useProfileCutter;
        cutter.cutterHeightScale = cutterHeightScale;
        cutter.logCutDiagnostics = logCutDiagnostics;
        cutter.transform.rotation = Quaternion.Euler(cutterDirectionEuler);
        cutter.autoMove = false;
        cutter.autoMoveStraightLine = true;
        cutter.autoMoveExtents = new Vector3(1.25f, 0f, 0f);
        cutter.autoMoveSpeed = 0.5f;
        float localStepLimit = Mathf.Max(
            workpiece.voxelSize * 4f,
            Mathf.Min(workpiece.LocalSize.x, Mathf.Min(workpiece.LocalSize.y, workpiece.LocalSize.z)) * 0.01f);
        cutter.maxMoveStepPerFrame = localStepLimit;
        cutter.maxCutSweepLength = Mathf.Max(localStepLimit, workpiece.voxelSize * 8f);
        ApplyCutterProfileSettingsToScene(workpiece);
    }

    private void ApplyCutterProfileSettingsToScene(WorkpieceVoxel workpiece)
    {
        int segmentCount = Mathf.Max(2, cutterProfileSegmentCount);
        if (workpiece == null)
        {
            workpiece = ResolveActiveWorkpiece();
        }

        if (workpiece != null)
        {
            workpiece.preferNativeMeshCutter = preferNativeMeshCutter;
            workpiece.SetProfileSegmentCount(segmentCount);
            workpiece.SetProfileRadiusSamples(cutterProfileRadiusSamples, cutterProfileRadiusSampleCount);
            workpiece.SetAngularProfileRadiusSamples(
                cutterAngularProfileMinRadiusSamples,
                cutterAngularProfileMaxRadiusSamples,
                cutterAngularProfileAxialSampleCount,
                cutterAngularProfileAngleSampleCount);
        }

    }

    private void RefreshCutterProfileFromVisual(CutterController cutter)
    {
        cutterProfileRadiusSampleCount = 0;
        for (int i = 0; i < cutterProfileRadiusSamples.Length; i++)
        {
            cutterProfileRadiusSamples[i] = 0f;
        }

        cutterAngularProfileAxialSampleCount = 0;
        cutterAngularProfileAngleSampleCount = 0;
        for (int i = 0; i < cutterAngularProfileMinRadiusSamples.Length; i++)
        {
            cutterAngularProfileMinRadiusSamples[i] = 0f;
            cutterAngularProfileMaxRadiusSamples[i] = 0f;
        }

        bool extractedProfile = cutter != null &&
            TryExtractCutterProfileRadiusSamples(
            cutter.gameObject,
            cutterProfileRadiusSamples,
            out cutterProfileRadiusSampleCount);
        if (extractedProfile)
        {
            TryExtractCutterAngularProfileRadiusSamples(
                cutter.gameObject,
                cutterAngularProfileMinRadiusSamples,
                cutterAngularProfileMaxRadiusSamples,
                out cutterAngularProfileAxialSampleCount,
                out cutterAngularProfileAngleSampleCount);
        }

        WorkpieceVoxel targetWorkpiece = cutter != null ? cutter.workpiece : null;
        ApplyCutterProfileSettingsToScene(targetWorkpiece);
        RefreshNativeCutterMeshFromVisual(cutter, targetWorkpiece);
    }

    private static bool TryExtractCutterProfileRadiusSamples(
        GameObject cutterObject,
        float[] targetSamples,
        out int sampleCount)
    {
        sampleCount = 0;
        if (cutterObject == null || targetSamples == null || targetSamples.Length == 0)
        {
            return false;
        }

        Transform visualRoot = cutterObject.transform.Find("Cutter Visual Anchor");
        if (visualRoot == null)
        {
            return false;
        }

        if (!TryGetReadableMeshVertexBounds(cutterObject.transform, visualRoot, out Bounds vertexBounds))
        {
            return false;
        }

        float axialHeight = vertexBounds.size.y;
        if (axialHeight <= 0.000001f)
        {
            return false;
        }

        int count = Mathf.Min(targetSamples.Length, WorkpieceVoxel.MaxCutterProfileRadiusSamples);
        float[] rawRadii = new float[count];
        bool[] hasRadius = new bool[count];
        Vector2 axisCenter = new Vector2(vertexBounds.center.x, vertexBounds.center.z);
        AccumulateCutterProfileRadii(
            cutterObject.transform,
            visualRoot,
            vertexBounds.min.y,
            axialHeight,
            axisCenter,
            rawRadii,
            hasRadius);

        if (!InterpolateMissingProfileRadii(rawRadii, hasRadius))
        {
            return false;
        }

        float maxRadius = 0f;
        for (int i = 0; i < count; i++)
        {
            maxRadius = Mathf.Max(maxRadius, rawRadii[i]);
        }

        if (maxRadius <= 0.000001f)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            targetSamples[i] = Mathf.Clamp01(rawRadii[i] / maxRadius);
        }

        sampleCount = count;
        return true;
    }

    private static bool TryExtractNativeCutterMesh(
        GameObject cutterObject,
        out float[] vertices,
        out int vertexCount,
        out int[] triangleIndices,
        out int indexCount)
    {
        vertices = null;
        triangleIndices = null;
        vertexCount = 0;
        indexCount = 0;

        if (cutterObject == null)
        {
            return false;
        }

        Transform visualRoot = cutterObject.transform.Find("Cutter Visual Anchor");
        if (visualRoot == null)
        {
            return false;
        }

        List<Vector3> vertexList = new List<Vector3>(4096);
        List<int> indexList = new List<int>(8192);

        MeshFilter[] filters = visualRoot.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            AccumulateNativeCutterMesh(
                cutterObject.transform,
                filters[i].transform,
                filters[i].sharedMesh,
                vertexList,
                indexList);
        }

        SkinnedMeshRenderer[] skinnedRenderers = visualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinnedRenderers.Length; i++)
        {
            AccumulateNativeCutterMesh(
                cutterObject.transform,
                skinnedRenderers[i].transform,
                skinnedRenderers[i].sharedMesh,
                vertexList,
                indexList);
        }

        if (vertexList.Count <= 0 || indexList.Count < 3)
        {
            return false;
        }

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

    private static void AccumulateNativeCutterMesh(
        Transform cutterRoot,
        Transform meshTransform,
        Mesh mesh,
        List<Vector3> vertices,
        List<int> triangleIndices)
    {
        if (cutterRoot == null ||
            meshTransform == null ||
            mesh == null ||
            !mesh.isReadable)
        {
            return;
        }

        Vector3[] meshVertices = mesh.vertices;
        int baseIndex = vertices.Count;
        for (int i = 0; i < meshVertices.Length; i++)
        {
            vertices.Add(cutterRoot.InverseTransformPoint(meshTransform.TransformPoint(meshVertices[i])));
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

    private static void RefreshNativeCutterMeshFromVisual(
        CutterController cutter,
        WorkpieceVoxel targetWorkpiece)
    {
        WorkpieceVoxel workpiece = targetWorkpiece != null
            ? targetWorkpiece
            : Object.FindFirstObjectByType<WorkpieceVoxel>();
        if (workpiece == null)
        {
            return;
        }

        if (cutter != null &&
            TryExtractNativeCutterMesh(
                cutter.gameObject,
                out float[] vertices,
                out int vertexCount,
                out int[] triangleIndices,
                out int indexCount))
        {
            workpiece.SetNativeCutterMesh(vertices, vertexCount, triangleIndices, indexCount);
            return;
        }

        workpiece.ClearNativeCutterMesh();
        Debug.LogWarning(
            "Native cutter mesh extraction failed; selected cutter has no readable triangle mesh under Cutter Visual Anchor.",
            workpiece);
    }

    private static bool TryExtractCutterAngularProfileRadiusSamples(
        GameObject cutterObject,
        float[] targetMinSamples,
        float[] targetMaxSamples,
        out int axialSampleCount,
        out int angleSampleCount)
    {
        axialSampleCount = 0;
        angleSampleCount = 0;
        if (cutterObject == null || targetMinSamples == null || targetMaxSamples == null)
        {
            return false;
        }

        Transform visualRoot = cutterObject.transform.Find("Cutter Visual Anchor");
        if (visualRoot == null ||
            !TryGetReadableMeshVertexBounds(cutterObject.transform, visualRoot, out Bounds vertexBounds))
        {
            return false;
        }

        float axialHeight = vertexBounds.size.y;
        if (axialHeight <= 0.000001f)
        {
            return false;
        }

        int axialCount = WorkpieceVoxel.MaxCutterAngularProfileAxialSamples;
        int angleCount = WorkpieceVoxel.MaxCutterAngularProfileAngleSamples;
        int sampleCount = axialCount * angleCount;
        if (targetMinSamples.Length < sampleCount || targetMaxSamples.Length < sampleCount)
        {
            return false;
        }

        float[] minRadii = new float[sampleCount];
        float[] maxRadii = new float[sampleCount];
        bool[] hasRadius = new bool[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            minRadii[i] = float.PositiveInfinity;
        }

        Vector2 axisCenter = new Vector2(vertexBounds.center.x, vertexBounds.center.z);
        AccumulateCutterAngularProfileRadii(
            cutterObject.transform,
            visualRoot,
            vertexBounds.min.y,
            axialHeight,
            axisCenter,
            axialCount,
            angleCount,
            minRadii,
            maxRadii,
            hasRadius);

        float globalMaxRadius = 0f;
        for (int i = 0; i < sampleCount; i++)
        {
            if (!hasRadius[i])
            {
                minRadii[i] = 0f;
                maxRadii[i] = 0f;
                continue;
            }

            globalMaxRadius = Mathf.Max(globalMaxRadius, maxRadii[i]);
        }

        if (globalMaxRadius <= 0.000001f)
        {
            return false;
        }

        for (int axial = 0; axial < axialCount; axial++)
        {
            int occupiedAngles = 0;
            for (int angle = 0; angle < angleCount; angle++)
            {
                if (hasRadius[axial * angleCount + angle])
                {
                    occupiedAngles++;
                }
            }

            bool solidRing = occupiedAngles >= Mathf.CeilToInt(angleCount * 0.75f);
            for (int angle = 0; angle < angleCount; angle++)
            {
                int index = axial * angleCount + angle;
                if (!hasRadius[index])
                {
                    targetMinSamples[index] = 0f;
                    targetMaxSamples[index] = 0f;
                    continue;
                }

                targetMinSamples[index] = solidRing ? 0f : Mathf.Clamp01(minRadii[index] / globalMaxRadius);
                targetMaxSamples[index] = Mathf.Clamp01(maxRadii[index] / globalMaxRadius);
            }
        }

        axialSampleCount = axialCount;
        angleSampleCount = angleCount;
        return true;
    }

    private static bool TryGetReadableMeshVertexBounds(
        Transform cutterRoot,
        Transform visualRoot,
        out Bounds bounds)
    {
        bounds = default;
        bool hasPoint = false;

        MeshFilter[] filters = visualRoot.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            AccumulateReadableMeshBounds(cutterRoot, filters[i].transform, filters[i].sharedMesh, ref bounds, ref hasPoint);
        }

        SkinnedMeshRenderer[] skinnedRenderers = visualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinnedRenderers.Length; i++)
        {
            AccumulateReadableMeshBounds(
                cutterRoot,
                skinnedRenderers[i].transform,
                skinnedRenderers[i].sharedMesh,
                ref bounds,
                ref hasPoint);
        }

        return hasPoint;
    }

    private static void AccumulateReadableMeshBounds(
        Transform cutterRoot,
        Transform meshTransform,
        Mesh mesh,
        ref Bounds bounds,
        ref bool hasPoint)
    {
        if (mesh == null || !mesh.isReadable)
        {
            return;
        }

        Vector3[] meshVertices = mesh.vertices;
        for (int i = 0; i < meshVertices.Length; i++)
        {
            Vector3 point = cutterRoot.InverseTransformPoint(meshTransform.TransformPoint(meshVertices[i]));
            if (!hasPoint)
            {
                bounds = new Bounds(point, Vector3.zero);
                hasPoint = true;
            }
            else
            {
                bounds.Encapsulate(point);
            }
        }
    }

    private static void AccumulateCutterProfileRadii(
        Transform cutterRoot,
        Transform visualRoot,
        float minAxial,
        float axialHeight,
        Vector2 axisCenter,
        float[] rawRadii,
        bool[] hasRadius)
    {
        MeshFilter[] filters = visualRoot.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            AccumulateMeshProfileRadii(
                cutterRoot,
                filters[i].transform,
                filters[i].sharedMesh,
                minAxial,
                axialHeight,
                axisCenter,
                rawRadii,
                hasRadius);
        }

        SkinnedMeshRenderer[] skinnedRenderers = visualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinnedRenderers.Length; i++)
        {
            AccumulateMeshProfileRadii(
                cutterRoot,
                skinnedRenderers[i].transform,
                skinnedRenderers[i].sharedMesh,
                minAxial,
                axialHeight,
                axisCenter,
                rawRadii,
            hasRadius);
        }
    }

    private static void AccumulateCutterAngularProfileRadii(
        Transform cutterRoot,
        Transform visualRoot,
        float minAxial,
        float axialHeight,
        Vector2 axisCenter,
        int axialCount,
        int angleCount,
        float[] minRadii,
        float[] maxRadii,
        bool[] hasRadius)
    {
        MeshFilter[] filters = visualRoot.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < filters.Length; i++)
        {
            AccumulateMeshAngularProfileRadii(
                cutterRoot,
                filters[i].transform,
                filters[i].sharedMesh,
                minAxial,
                axialHeight,
                axisCenter,
                axialCount,
                angleCount,
                minRadii,
                maxRadii,
                hasRadius);
        }

        SkinnedMeshRenderer[] skinnedRenderers = visualRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinnedRenderers.Length; i++)
        {
            AccumulateMeshAngularProfileRadii(
                cutterRoot,
                skinnedRenderers[i].transform,
                skinnedRenderers[i].sharedMesh,
                minAxial,
                axialHeight,
                axisCenter,
                axialCount,
                angleCount,
                minRadii,
                maxRadii,
                hasRadius);
        }
    }

    private static void AccumulateMeshProfileRadii(
        Transform cutterRoot,
        Transform meshTransform,
        Mesh mesh,
        float minAxial,
        float axialHeight,
        Vector2 axisCenter,
        float[] rawRadii,
        bool[] hasRadius)
    {
        if (mesh == null || !mesh.isReadable)
        {
            return;
        }

        Vector3[] meshVertices = mesh.vertices;
        int maxIndex = rawRadii.Length - 1;
        for (int i = 0; i < meshVertices.Length; i++)
        {
            Vector3 point = cutterRoot.InverseTransformPoint(meshTransform.TransformPoint(meshVertices[i]));
            float normalizedAxial = Mathf.Clamp01((point.y - minAxial) / axialHeight);
            int sampleIndex = Mathf.Clamp(Mathf.RoundToInt(normalizedAxial * maxIndex), 0, maxIndex);
            float dx = point.x - axisCenter.x;
            float dz = point.z - axisCenter.y;
            float radius = Mathf.Sqrt(dx * dx + dz * dz);
            rawRadii[sampleIndex] = Mathf.Max(rawRadii[sampleIndex], radius);
            hasRadius[sampleIndex] = true;
        }
    }

    private static void AccumulateMeshAngularProfileRadii(
        Transform cutterRoot,
        Transform meshTransform,
        Mesh mesh,
        float minAxial,
        float axialHeight,
        Vector2 axisCenter,
        int axialCount,
        int angleCount,
        float[] minRadii,
        float[] maxRadii,
        bool[] hasRadius)
    {
        if (mesh == null || !mesh.isReadable)
        {
            return;
        }

        Vector3[] meshVertices = mesh.vertices;
        for (int i = 0; i < meshVertices.Length; i++)
        {
            AccumulateAngularProfilePoint(
                cutterRoot.InverseTransformPoint(meshTransform.TransformPoint(meshVertices[i])),
                minAxial,
                axialHeight,
                axisCenter,
                axialCount,
                angleCount,
                minRadii,
                maxRadii,
                hasRadius);
        }

        int[] triangles = mesh.triangles;
        for (int i = 0; i + 2 < triangles.Length; i += 3)
        {
            Vector3 a = cutterRoot.InverseTransformPoint(meshTransform.TransformPoint(meshVertices[triangles[i]]));
            Vector3 b = cutterRoot.InverseTransformPoint(meshTransform.TransformPoint(meshVertices[triangles[i + 1]]));
            Vector3 c = cutterRoot.InverseTransformPoint(meshTransform.TransformPoint(meshVertices[triangles[i + 2]]));
            AccumulateAngularProfilePoint((a + b + c) / 3f, minAxial, axialHeight, axisCenter, axialCount, angleCount, minRadii, maxRadii, hasRadius);
            AccumulateAngularProfilePoint((a + b) * 0.5f, minAxial, axialHeight, axisCenter, axialCount, angleCount, minRadii, maxRadii, hasRadius);
            AccumulateAngularProfilePoint((b + c) * 0.5f, minAxial, axialHeight, axisCenter, axialCount, angleCount, minRadii, maxRadii, hasRadius);
            AccumulateAngularProfilePoint((c + a) * 0.5f, minAxial, axialHeight, axisCenter, axialCount, angleCount, minRadii, maxRadii, hasRadius);
        }
    }

    private static void AccumulateAngularProfilePoint(
        Vector3 point,
        float minAxial,
        float axialHeight,
        Vector2 axisCenter,
        int axialCount,
        int angleCount,
        float[] minRadii,
        float[] maxRadii,
        bool[] hasRadius)
    {
        float normalizedAxial = Mathf.Clamp01((point.y - minAxial) / axialHeight);
        int axialIndex = Mathf.Clamp(Mathf.RoundToInt(normalizedAxial * (axialCount - 1)), 0, axialCount - 1);
        float dx = point.x - axisCenter.x;
        float dz = point.z - axisCenter.y;
        float radius = Mathf.Sqrt(dx * dx + dz * dz);
        if (radius <= 0.000001f)
        {
            return;
        }

        float angle = Mathf.Atan2(dz, dx);
        float normalizedAngle = Mathf.Repeat(angle / (Mathf.PI * 2f), 1f);
        int angleIndex = Mathf.FloorToInt(normalizedAngle * angleCount) % angleCount;
        int index = axialIndex * angleCount + angleIndex;
        minRadii[index] = Mathf.Min(minRadii[index], radius);
        maxRadii[index] = Mathf.Max(maxRadii[index], radius);
        hasRadius[index] = true;
    }

    private static bool InterpolateMissingProfileRadii(float[] rawRadii, bool[] hasRadius)
    {
        int count = rawRadii.Length;
        int firstKnown = -1;
        for (int i = 0; i < count; i++)
        {
            if (hasRadius[i])
            {
                firstKnown = i;
                break;
            }
        }

        if (firstKnown < 0)
        {
            return false;
        }

        for (int i = 0; i < firstKnown; i++)
        {
            rawRadii[i] = rawRadii[firstKnown];
            hasRadius[i] = true;
        }

        int previousKnown = firstKnown;
        for (int i = firstKnown + 1; i < count; i++)
        {
            if (!hasRadius[i])
            {
                continue;
            }

            int gap = i - previousKnown;
            if (gap > 1)
            {
                float start = rawRadii[previousKnown];
                float end = rawRadii[i];
                for (int fill = previousKnown + 1; fill < i; fill++)
                {
                    float t = (fill - previousKnown) / (float)gap;
                    rawRadii[fill] = Mathf.Lerp(start, end, t);
                    hasRadius[fill] = true;
                }
            }

            previousKnown = i;
        }

        for (int i = previousKnown + 1; i < count; i++)
        {
            rawRadii[i] = rawRadii[previousKnown];
            hasRadius[i] = true;
        }

        return true;
    }

    private static Vector3 SanitizeCutterDirectionEuler(Vector3 euler)
    {
        return new Vector3(
            SanitizeCutterDirectionComponent(euler.x),
            SanitizeCutterDirectionComponent(euler.y),
            SanitizeCutterDirectionComponent(euler.z));
    }

    private static float SanitizeCutterDirectionComponent(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            return 0f;
        }

        return Mathf.Clamp(value, MinCutterDirectionDegrees, MaxCutterDirectionDegrees);
    }

    private void SetupCutterVisual(GameObject cutterObject)
    {
        DisableRootPrimitiveVisual(cutterObject);
        RemoveExistingCutterVisual(cutterObject.transform);

        GameObject anchorObject = new GameObject("Cutter Visual Anchor");
        anchorObject.transform.SetParent(cutterObject.transform, false);
        Transform visualAnchor = anchorObject.transform;

        GameObject modelPrefab = ResolveCutterModelPrefab();

        if (modelPrefab == null)
        {
            CreateFallbackCutterVisual(visualAnchor);
            ConfigureCutterVisibility(cutterObject, visualAnchor);
            return;
        }

        GameObject model = Instantiate(modelPrefab, visualAnchor);
        model.name = "Cutter Model";
        model.transform.localPosition = Vector3.zero;
        model.transform.localRotation = Quaternion.Euler(cutterModelLocalEuler);
        model.transform.localScale = Vector3.one;

        ApplyCutterMaterial(model);

        if (normalizeCutterModelToRadius)
        {
            FitCutterModel(model, visualAnchor);
        }

        ConfigureCutterVisibility(cutterObject, visualAnchor);
    }

    private GameObject ResolveCutterModelPrefab()
    {
        if (cutterModelPrefab != null)
        {
            return cutterModelPrefab;
        }

        if (!string.IsNullOrWhiteSpace(cutterModelResourcePath))
        {
            GameObject resourceModel = Resources.Load<GameObject>(cutterModelResourcePath);
            if (resourceModel != null)
            {
                return resourceModel;
            }
        }

        return Resources.Load<GameObject>(DefaultCutterResourcePath);
    }

    private void DisableRootPrimitiveVisual(GameObject cutterObject)
    {
        MeshRenderer rootRenderer = cutterObject.GetComponent<MeshRenderer>();
        if (rootRenderer != null)
        {
            rootRenderer.enabled = false;
        }

        Collider rootCollider = cutterObject.GetComponent<Collider>();
        if (rootCollider != null)
        {
            rootCollider.enabled = false;
        }
    }

    private static void RemoveExistingCutterVisual(Transform root)
    {
        Transform oldVisual = root.Find("Cutter Visual Anchor");
        if (oldVisual == null)
        {
            oldVisual = root.Find("Cutter Model");
            if (oldVisual == null)
            {
                return;
            }
        }

        if (Application.isPlaying)
        {
            oldVisual.name = "Cutter Visual (Retired)";
            Destroy(oldVisual.gameObject);
        }
        else
        {
            DestroyImmediate(oldVisual.gameObject);
        }
    }

    private void ConfigureCutterVisibility(GameObject cutterObject, Transform model)
    {
        CutterVisualScreenSize visibility = cutterObject.GetComponent<CutterVisualScreenSize>();
        if (!keepCutterVisible || model == null)
        {
            if (visibility != null)
            {
                visibility.enabled = false;
            }
            return;
        }

        if (visibility == null)
        {
            visibility = cutterObject.AddComponent<CutterVisualScreenSize>();
        }

        visibility.Configure(model, cutterRadius * 2f, minimumCutterVisualPixels);
    }

    private void CreateFallbackCutterVisual(Transform visualAnchor)
    {
        GameObject fallback = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        fallback.name = "Cutter Model";
        fallback.transform.SetParent(visualAnchor, false);
        float targetHeight = cutterRadius * cutterHeightScale;
        fallback.transform.localPosition = Vector3.up * targetHeight * 0.5f;
        fallback.transform.localScale = new Vector3(cutterRadius * 2f, targetHeight, cutterRadius * 2f);

        Collider collider = fallback.GetComponent<Collider>();
        if (collider != null)
        {
            collider.enabled = false;
        }

        ApplyCutterMaterial(fallback);
    }

    private void ApplyCutterMaterial(GameObject model)
    {
        Material material = cutterMaterial != null
            ? cutterMaterial
            : CreateCompatibleMaterial("Cutter Material", new Color(1f, 0.18f, 0.06f, 1f));

        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            renderers[i].sharedMaterial = material;
        }
    }

    private void FitCutterModel(GameObject model, Transform visualAnchor)
    {
        if (!TryGetRendererBoundsInSpace(model, visualAnchor, out Bounds bounds))
        {
            return;
        }

        float height = bounds.size.y;
        if (bounds.size.x <= 0.000001f || bounds.size.z <= 0.000001f || height <= 0.000001f)
        {
            return;
        }

        float targetDiameter = Mathf.Max(0.001f, cutterRadius * 2f);
        float targetHeight = Mathf.Max(0.001f, cutterRadius * cutterHeightScale);

        model.transform.localPosition += new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
        float radialScaleX = targetDiameter / bounds.size.x;
        float axialScale = targetHeight / height;
        float radialScaleZ = targetDiameter / bounds.size.z;
        visualAnchor.localScale = new Vector3(radialScaleX, axialScale, radialScaleZ);
    }

    private static bool TryGetRendererBoundsInSpace(GameObject model, Transform space, out Bounds bounds)
    {
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        bounds = default;
        bool hasPoint = false;

        for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            Renderer renderer = renderers[rendererIndex];
            Bounds localBounds = renderer.localBounds;
            Vector3 center = localBounds.center;
            Vector3 extents = localBounds.extents;

            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 localCorner = center + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                Vector3 point = space.InverseTransformPoint(renderer.transform.TransformPoint(localCorner));

                if (!hasPoint)
                {
                    bounds = new Bounds(point, Vector3.zero);
                    hasPoint = true;
                }
                else
                {
                    bounds.Encapsulate(point);
                }
            }
        }

        return hasPoint;
    }

    private void ConfigureCamera(WorkpieceVoxel workpiece)
    {
        Transform target = workpiece.transform;
        Bounds localBounds = workpiece.LocalBounds;
        Vector3 worldCenter = target.TransformPoint(localBounds.center);
        Vector3 scale = target.lossyScale;
        Vector3 worldSize = new Vector3(
            Mathf.Abs(localBounds.size.x * scale.x),
            Mathf.Abs(localBounds.size.y * scale.y),
            Mathf.Abs(localBounds.size.z * scale.z));
        float boundingRadius = Mathf.Max(workpiece.voxelSize, worldSize.magnitude * 0.5f);

        Camera camera = Camera.main;
        if (camera == null)
        {
            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            camera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
        }

        float verticalHalfFov = camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
        float horizontalHalfFov = Mathf.Atan(Mathf.Tan(verticalHalfFov) * Mathf.Max(0.01f, camera.aspect));
        float limitingHalfFov = Mathf.Max(1f * Mathf.Deg2Rad, Mathf.Min(verticalHalfFov, horizontalHalfFov));
        float framingDistance;
        if (camera.orthographic)
        {
            camera.orthographicSize = boundingRadius * 1.1f;
            framingDistance = boundingRadius * 2f;
        }
        else
        {
            framingDistance = boundingRadius / Mathf.Sin(limitingHalfFov) * 1.1f;
        }

        Vector3 viewDirection = new Vector3(0.82f, 0.62f, -1f).normalized;
        camera.transform.position = worldCenter + viewDirection * framingDistance;
        camera.transform.rotation = Quaternion.LookRotation(worldCenter - camera.transform.position, Vector3.up);
        camera.nearClipPlane = Mathf.Max(workpiece.voxelSize, boundingRadius * 0.0001f);
        camera.farClipPlane = framingDistance * 4f + boundingRadius * 2f;

        SimpleCameraController cameraController = camera.GetComponent<SimpleCameraController>();
        if (cameraController == null)
        {
            cameraController = camera.gameObject.AddComponent<SimpleCameraController>();
        }

        cameraController.target = target;
        cameraController.targetLocalOffset = localBounds.center;
        cameraController.distance = Vector3.Distance(camera.transform.position, worldCenter);
        cameraController.minDistance = Mathf.Max(workpiece.voxelSize * 4f, cutterRadius * 2f);
        cameraController.maxDistance = framingDistance * 4f;
        cameraController.zoomStep = framingDistance * 0.05f;
        cameraController.keyboardPanSpeed = boundingRadius * 0.5f;
        CutterController cutter = ResolveActiveCutter();
        float cutterFocusDistance = Mathf.Max(
            cutterRadius * 20f,
            workpiece.voxelSize * 100f);
        cameraController.ConfigureViewTargets(
            target,
            localBounds.center,
            cutter != null ? cutter.transform : null,
            cutterFocusDistance);
    }

    private static void EnsureLight()
    {
        if (Object.FindFirstObjectByType<Light>() != null)
        {
            return;
        }

        GameObject lightObject = new GameObject("Directional Light");
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.3f;
        lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
    }

    private WorkpieceVoxel ResolveActiveWorkpiece()
    {
        if (activeWorkpiece != null)
        {
            return activeWorkpiece;
        }

        activeWorkpiece = Object.FindFirstObjectByType<WorkpieceVoxel>();
        return activeWorkpiece;
    }

    private CutterController ResolveActiveCutter()
    {
        if (activeCutter != null)
        {
            return activeCutter;
        }

        activeCutter = Object.FindFirstObjectByType<CutterController>();
        return activeCutter;
    }

    private static Material CreateCompatibleMaterial(string materialName, Color color)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            shader = Shader.Find("Universal Render Pipeline/Simple Lit");
        }

        if (shader == null)
        {
            shader = Shader.Find("Standard");
        }

        if (shader == null)
        {
            shader = Shader.Find("Diffuse");
        }

        Material material = new Material(shader)
        {
            name = materialName
        };

        SetMaterialColor(material, color);
        return material;
    }

    private static void SetMaterialColor(Material material, Color color)
    {
        if (material == null)
        {
            return;
        }

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Color"))
        {
            material.SetColor("_Color", color);
        }
    }

#if UNITY_EDITOR
    [UnityEditor.MenuItem("Tools/Cutting Simulation/Add Demo Bootstrap")]
    private static void AddDemoBootstrap()
    {
        CuttingDemoBootstrap existing = Object.FindFirstObjectByType<CuttingDemoBootstrap>();
        if (existing != null)
        {
            UnityEditor.Selection.activeGameObject = existing.gameObject;
            return;
        }

        GameObject bootstrapObject = new GameObject("Cutting Demo Bootstrap");
        bootstrapObject.AddComponent<CuttingDemoBootstrap>();
        UnityEditor.Selection.activeGameObject = bootstrapObject;
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }
#endif
}
