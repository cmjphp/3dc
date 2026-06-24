using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public sealed class SparseCutHistory : MonoBehaviour
{
    private const int MaxProfileRadiusSamples = WorkpieceVoxel.MaxCutterProfileRadiusSamples;

    [Header("Sparse Brick Space")]
    [Min(0.001f)] public float precisionMm = 0.01f;
    [Min(8)] public int brickResolution = 64;
    public Vector3 travelEnvelopeMm = new Vector3(1000f, 1000f, 1000f);
    public Vector3 travelCenterMm;
    public Vector3 workpieceSizeMm = new Vector3(100f, 100f, 100f);
    public Vector3 workpieceCenterMm;
    [Min(1)] public int maxResidentBricks = 4096;

    [Header("GPU Brick SDF")]
    public bool useGpuBrickSdf = true;
    public bool drawGpuBricks;
    public ComputeShader sdfCutCompute;
    public Material gpuSurfaceMaterial;
    [Min(1)] public int maxResidentGpuBricks = 96;
    [Min(1)] public int maxRenderedGpuBricks = 96;
    [Min(1)] public int maxGpuBrickCutsPerOperation = 24;
    [Min(16)] public int gpuRaymarchMaxSteps = 96;
    [Range(0.25f, 2f)] public float gpuRaymarchStepScale = 0.65f;
    public Color gpuBrickColor = new Color(0.72f, 0.76f, 0.78f, 1f);

    [Header("Measurement")]
    [Min(0.0001f)] public float measurementPrecisionMm = 0.001f;
    [Min(1f)] public float defaultMeasurementMaxDistanceMm = 1500f;
    [Min(0.001f)] public float measurementMaxMarchStepMm = 1f;
    [Min(64)] public int maxMeasurementMarchIterations = 4096;
    [Min(8)] public int measurementBinaryIterations = 24;
    [Min(1024)] public int maxMeasurementOperationHistory = 200000;
    [Min(2)] public int profileSegmentCount = 24;
    public bool useDoublePrecisionMeasurement = true;

    [Header("Debug")]
    public bool drawDebugBricks;
    [Min(1)] public int maxDebugBricks = 256;
    public Color debugBrickColor = new Color(0.1f, 0.8f, 1f, 0.18f);

    [SerializeField] private int residentBrickCount;
    [SerializeField] private int residentGpuBrickCount;
    [SerializeField] private int totalCutOperationCount;
    [SerializeField] private int totalTouchedBrickCount;
    [SerializeField] private int measurementOperationCount;

    private readonly Dictionary<Vector3Int, SparseCutBrick> bricks = new Dictionary<Vector3Int, SparseCutBrick>(1024);
    private readonly Queue<Vector3Int> residencyQueue = new Queue<Vector3Int>(1024);
    private readonly List<ProfileCutOperation> profileCutOperations = new List<ProfileCutOperation>(4096);
    private readonly int[] changedCounterData = new int[1];
    private readonly float[] cutterProfileRadiusSamples = new float[MaxProfileRadiusSamples];
    private int cutterProfileRadiusSampleCount;

    private ComputeBuffer changedCounterBuffer;
    private Material runtimeGpuSurfaceMaterial;
    private MaterialPropertyBlock materialPropertyBlock;
    private Mesh brickProxyMesh;
    private float cachedProxyBrickSize;
    private int sdfProfileCutterKernel = -1;
    private int sdfInitKernel = -1;
    private bool gpuUnavailable;

    private sealed class SparseCutBrick
    {
        public Vector3Int index;
        public Bounds localBounds;
        public int cutCount;
        public int lastTouchedFrame;
        public int lastGpuTouchedFrame;
        public ComputeBuffer sdfBuffer;
        public int sdfSampleCount;
        public List<int> measurementOperationIndices;
    }

    private struct SparseProfileCutCommand
    {
        public bool isValid;
        public Vector3 localStart;
        public Vector3 localEnd;
        public Vector3 localAxis;
        public float localRadius;
        public float localHeight;
        public float updateBand;
    }

    private struct ProfileCutOperation
    {
        public Vector3 localStart;
        public Vector3 localEnd;
        public Vector3 localAxis;
        public float localRadius;
        public float localHeight;
        public Bounds localBounds;
        public int profileRadiusSampleCount;
        public Vector4 profile0;
        public Vector4 profile1;
        public Vector4 profile2;
        public Vector4 profile3;
        public Vector4 profile4;
        public Vector4 profile5;
        public Vector4 profile6;
        public Vector4 profile7;
    }

    public struct MeasurementResult
    {
        public bool hit;
        public Vector3 worldPoint;
        public float distanceMm;
        public float signedDistanceMm;
        public int iterations;
        public float precisionMm;
    }

    public int ResidentBrickCount
    {
        get { return residentBrickCount; }
    }

    public int TotalCutOperationCount
    {
        get { return totalCutOperationCount; }
    }

    public int TotalTouchedBrickCount
    {
        get { return totalTouchedBrickCount; }
    }

    public int ResidentGpuBrickCount
    {
        get { return residentGpuBrickCount; }
    }

    public int MeasurementOperationCount
    {
        get { return measurementOperationCount; }
    }

    public float BrickSizeMm
    {
        get { return Mathf.Max(0.001f, precisionMm) * Mathf.Max(8, brickResolution); }
    }

    private int SampleSizePerAxis
    {
        get { return Mathf.Max(8, brickResolution) + 3; }
    }

    private int SdfSampleCount
    {
        get
        {
            int sampleSize = SampleSizePerAxis;
            return sampleSize * sampleSize * sampleSize;
        }
    }

    private Vector3 TravelMin
    {
        get { return travelCenterMm - SanitizeSize(travelEnvelopeMm) * 0.5f; }
    }

    private Vector3Int MaxBrickIndex
    {
        get
        {
            Vector3 size = SanitizeSize(travelEnvelopeMm);
            float brickSize = BrickSizeMm;
            return new Vector3Int(
                Mathf.Max(0, Mathf.CeilToInt(size.x / brickSize) - 1),
                Mathf.Max(0, Mathf.CeilToInt(size.y / brickSize) - 1),
                Mathf.Max(0, Mathf.CeilToInt(size.z / brickSize) - 1));
        }
    }

    private void OnValidate()
    {
        precisionMm = Mathf.Max(0.001f, precisionMm);
        brickResolution = Mathf.Max(8, brickResolution);
        maxResidentBricks = Mathf.Max(1, maxResidentBricks);
        maxResidentGpuBricks = Mathf.Max(1, maxResidentGpuBricks);
        maxRenderedGpuBricks = Mathf.Max(1, maxRenderedGpuBricks);
        maxGpuBrickCutsPerOperation = Mathf.Max(1, maxGpuBrickCutsPerOperation);
        gpuRaymarchMaxSteps = Mathf.Max(16, gpuRaymarchMaxSteps);
        gpuRaymarchStepScale = Mathf.Clamp(gpuRaymarchStepScale, 0.25f, 2f);
        measurementPrecisionMm = Mathf.Max(0.0001f, measurementPrecisionMm);
        defaultMeasurementMaxDistanceMm = Mathf.Max(1f, defaultMeasurementMaxDistanceMm);
        measurementMaxMarchStepMm = Mathf.Max(0.001f, measurementMaxMarchStepMm);
        maxMeasurementMarchIterations = Mathf.Max(64, maxMeasurementMarchIterations);
        measurementBinaryIterations = Mathf.Max(8, measurementBinaryIterations);
        maxMeasurementOperationHistory = Mathf.Max(1024, maxMeasurementOperationHistory);
        profileSegmentCount = Mathf.Max(2, profileSegmentCount);
        maxDebugBricks = Mathf.Max(1, maxDebugBricks);
        travelEnvelopeMm = SanitizeSize(travelEnvelopeMm);
        workpieceSizeMm = SanitizeSize(workpieceSizeMm);
    }

    private void LateUpdate()
    {
        DrawGpuBricks();
    }

    private void OnDisable()
    {
        ReleaseAllGpuResources();
    }

    private void OnDestroy()
    {
        ReleaseAllGpuResources();
    }

    public void ResetHistory()
    {
        gpuUnavailable = false;
        ReleaseAllBrickGpuResources();
        bricks.Clear();
        residencyQueue.Clear();
        profileCutOperations.Clear();
        residentBrickCount = 0;
        residentGpuBrickCount = 0;
        totalCutOperationCount = 0;
        totalTouchedBrickCount = 0;
        measurementOperationCount = 0;
    }

    public void SetProfileRadiusSamples(float[] normalizedRadiusSamples, int sampleCount)
    {
        cutterProfileRadiusSampleCount = 0;
        if (normalizedRadiusSamples == null || sampleCount < 2)
        {
            return;
        }

        int count = Mathf.Clamp(sampleCount, 2, MaxProfileRadiusSamples);
        for (int i = 0; i < MaxProfileRadiusSamples; i++)
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

        if (maxSample > 0.000001f)
        {
            cutterProfileRadiusSampleCount = count;
        }
    }

    public int RecordProfileCutterSweep(Vector3 worldStart, Vector3 worldEnd, Vector3 worldAxis, float worldRadius, float worldHeight)
    {
        return RecordProfileCutterSweep(worldStart, worldEnd, worldAxis, Vector3.zero, worldRadius, worldHeight);
    }

    public int RecordProfileCutterSweep(
        Vector3 worldStart,
        Vector3 worldEnd,
        Vector3 worldAxis,
        Vector3 worldRight,
        float worldRadius,
        float worldHeight)
    {
        if (worldRadius <= 0f || worldHeight <= 0f || worldAxis.sqrMagnitude < 0.000001f)
        {
            return 0;
        }

        Vector3 localStart = transform.InverseTransformPoint(worldStart);
        Vector3 localEnd = transform.InverseTransformPoint(worldEnd);
        Vector3 localAxis = transform.InverseTransformDirection(worldAxis).normalized;
        float localRadius = WorldDistanceToLocalDistance(worldRadius);
        float localHeight = WorldDistanceToLocalDistance(worldHeight);
        float updateBand = precisionMm * 2f;

        Vector3 startTop = localStart + localAxis * localHeight;
        Vector3 endTop = localEnd + localAxis * localHeight;
        Vector3 localMin = Vector3.Min(Vector3.Min(localStart, localEnd), Vector3.Min(startTop, endTop)) - Vector3.one * (localRadius + updateBand);
        Vector3 localMax = Vector3.Max(Vector3.Max(localStart, localEnd), Vector3.Max(startTop, endTop)) + Vector3.one * (localRadius + updateBand);

        SparseProfileCutCommand cutCommand = new SparseProfileCutCommand
        {
            isValid = true,
            localStart = localStart,
            localEnd = localEnd,
            localAxis = localAxis,
            localRadius = localRadius,
            localHeight = localHeight,
            updateBand = updateBand
        };

        int operationIndex = AddMeasurementOperation(cutCommand, localMin, localMax);
        return RecordLocalAabb(localMin, localMax, cutCommand, operationIndex);
    }

    public bool TrySampleMeasurementSdf(Vector3 worldPoint, out float signedDistanceMm)
    {
        Vector3 localPoint = transform.InverseTransformPoint(worldPoint);
        signedDistanceMm = SampleMeasurementSdfLocal(localPoint);
        return true;
    }

    public bool TryMeasureSurfaceAlongRay(Vector3 worldRayOrigin, Vector3 worldRayDirection, out MeasurementResult result)
    {
        return TryMeasureSurfaceAlongRay(worldRayOrigin, worldRayDirection, defaultMeasurementMaxDistanceMm, out result);
    }

    public bool TryMeasureSurfaceAlongRay(Vector3 worldRayOrigin, Vector3 worldRayDirection, float maxDistanceMm, out MeasurementResult result)
    {
        result = new MeasurementResult
        {
            hit = false,
            worldPoint = worldRayOrigin,
            distanceMm = 0f,
            signedDistanceMm = 0f,
            iterations = 0,
            precisionMm = measurementPrecisionMm
        };

        if (worldRayDirection.sqrMagnitude < 0.000001f || maxDistanceMm <= 0f)
        {
            return false;
        }

        Vector3 localOrigin = transform.InverseTransformPoint(worldRayOrigin);
        Vector3 localDirection = transform.InverseTransformDirection(worldRayDirection).normalized;
        if (localDirection.sqrMagnitude < 0.000001f)
        {
            return false;
        }

        float maxLocalDistance = WorldDistanceToLocalDistance(maxDistanceMm);
        double targetPrecision = System.Math.Max(0.0001, measurementPrecisionMm);
        double maxMarchStep = System.Math.Max(targetPrecision, (double)WorldDistanceToLocalDistance(measurementMaxMarchStepMm));
        int maxIterations = Mathf.Max(64, maxMeasurementMarchIterations);

        if (useDoublePrecisionMeasurement)
        {
            // Double-precision path: use double for t, coordinate accumulation, and SDF evaluation
            double t = 0.0;
            double previousT = 0.0;
            double originX = localOrigin.x, originY = localOrigin.y, originZ = localOrigin.z;
            double dirX = localDirection.x, dirY = localDirection.y, dirZ = localDirection.z;
            float previousSdf = (float)SampleMeasurementSdfLocalDouble(originX, originY, originZ);

            if (System.Math.Abs(previousSdf) <= targetPrecision)
            {
                result.hit = true;
                result.worldPoint = worldRayOrigin;
                result.signedDistanceMm = previousSdf;
                return true;
            }

            for (int i = 0; i < maxIterations && t <= maxLocalDistance; i++)
            {
                double step = System.Math.Min(System.Math.Max(System.Math.Abs(previousSdf) * 0.75, targetPrecision), maxMarchStep);
                previousT = t;
                t += step;

                double px = originX + dirX * t;
                double py = originY + dirY * t;
                double pz = originZ + dirZ * t;
                float sdf = (float)SampleMeasurementSdfLocalDouble(px, py, pz);

                if (HasSurfaceCrossing(previousSdf, sdf))
                {
                    double hitT = RefineMeasurementHitDouble(originX, originY, originZ, dirX, dirY, dirZ, previousT, t, previousSdf, sdf, out float hitSdf, out int refineIterations);
                    Vector3 hitLocal = new Vector3(
                        (float)(originX + dirX * hitT),
                        (float)(originY + dirY * hitT),
                        (float)(originZ + dirZ * hitT));
                    Vector3 hitWorld = transform.TransformPoint(hitLocal);
                    result.hit = true;
                    result.worldPoint = hitWorld;
                    result.distanceMm = Vector3.Distance(worldRayOrigin, hitWorld);
                    result.signedDistanceMm = hitSdf;
                    result.iterations = i + refineIterations + 1;
                    return true;
                }

                previousSdf = sdf;
            }
        }
        else
        {
            // Float-precision path (original)
            float tF = 0f;
            float previousTF = 0f;
            float previousSdf = SampleMeasurementSdfLocal(localOrigin);
            float targetPrecisionF = (float)targetPrecision;
            float maxMarchStepF = (float)maxMarchStep;

            if (Mathf.Abs(previousSdf) <= targetPrecisionF)
            {
                result.hit = true;
                result.worldPoint = worldRayOrigin;
                result.signedDistanceMm = previousSdf;
                return true;
            }

            for (int i = 0; i < maxIterations && tF <= maxLocalDistance; i++)
            {
                float step = Mathf.Clamp(Mathf.Abs(previousSdf) * 0.75f, targetPrecisionF, maxMarchStepF);
                previousTF = tF;
                tF += step;

                Vector3 localPoint = localOrigin + localDirection * tF;
                float sdf = SampleMeasurementSdfLocal(localPoint);

                if (HasSurfaceCrossing(previousSdf, sdf))
                {
                    float hitT = RefineMeasurementHit(localOrigin, localDirection, previousTF, tF, previousSdf, sdf, out float hitSdf, out int refineIterations);
                    Vector3 hitLocal = localOrigin + localDirection * hitT;
                    Vector3 hitWorld = transform.TransformPoint(hitLocal);
                    result.hit = true;
                    result.worldPoint = hitWorld;
                    result.distanceMm = Vector3.Distance(worldRayOrigin, hitWorld);
                    result.signedDistanceMm = hitSdf;
                    result.iterations = i + refineIterations + 1;
                    return true;
                }

                previousSdf = sdf;
            }
        }

        result.iterations = maxIterations;
        return false;
    }

    private int RecordLocalAabb(Vector3 localMin, Vector3 localMax, SparseProfileCutCommand cutCommand, int measurementOperationIndex)
    {
        Vector3Int minBrick = ClampBrickIndex(LocalToBrickIndex(localMin));
        Vector3Int maxBrick = ClampBrickIndex(LocalToBrickIndex(localMax));
        int touchedThisOperation = 0;
        int gpuCutsThisOperation = 0;

        totalCutOperationCount++;

        for (int x = minBrick.x; x <= maxBrick.x; x++)
        {
            for (int y = minBrick.y; y <= maxBrick.y; y++)
            {
                for (int z = minBrick.z; z <= maxBrick.z; z++)
                {
                    SparseCutBrick brick = TouchBrick(new Vector3Int(x, y, z));
                    AddMeasurementOperationToBrick(brick, measurementOperationIndex);
                    if (gpuCutsThisOperation < maxGpuBrickCutsPerOperation && ApplyProfileCutToBrick(brick, cutCommand))
                    {
                        gpuCutsThisOperation++;
                    }

                    touchedThisOperation++;
                }
            }
        }

        totalTouchedBrickCount += touchedThisOperation;
        residentBrickCount = bricks.Count;
        EvictOldestIfNeeded();
        EvictOldestGpuBricksIfNeeded();
        return touchedThisOperation;
    }

    private SparseCutBrick TouchBrick(Vector3Int index)
    {
        if (!bricks.TryGetValue(index, out SparseCutBrick brick))
        {
            brick = new SparseCutBrick
            {
                index = index,
                localBounds = GetBrickBounds(index)
            };

            bricks.Add(index, brick);
            residencyQueue.Enqueue(index);
        }

        brick.cutCount++;
        brick.lastTouchedFrame = Time.frameCount;
        return brick;
    }

    private void EvictOldestIfNeeded()
    {
        while (bricks.Count > maxResidentBricks && residencyQueue.Count > 0)
        {
            Vector3Int candidate = residencyQueue.Dequeue();
            if (bricks.TryGetValue(candidate, out SparseCutBrick brick))
            {
                ReleaseBrickGpuResources(brick);
                bricks.Remove(candidate);
            }
        }

        residentBrickCount = bricks.Count;
        residentGpuBrickCount = CountResidentGpuBricks();
    }

    private int AddMeasurementOperation(SparseProfileCutCommand cutCommand, Vector3 localMin, Vector3 localMax)
    {
        if (profileCutOperations.Count >= maxMeasurementOperationHistory)
        {
            CompactMeasurementHistory();
        }

        ProfileCutOperation operation = new ProfileCutOperation
        {
            localStart = cutCommand.localStart,
            localEnd = cutCommand.localEnd,
            localAxis = cutCommand.localAxis.normalized,
            localRadius = cutCommand.localRadius,
            localHeight = cutCommand.localHeight,
            localBounds = BoundsFromMinMax(localMin, localMax),
            profileRadiusSampleCount = cutterProfileRadiusSampleCount,
            profile0 = PackProfileRadiusSamples(0),
            profile1 = PackProfileRadiusSamples(4),
            profile2 = PackProfileRadiusSamples(8),
            profile3 = PackProfileRadiusSamples(12),
            profile4 = PackProfileRadiusSamples(16),
            profile5 = PackProfileRadiusSamples(20),
            profile6 = PackProfileRadiusSamples(24),
            profile7 = PackProfileRadiusSamples(28)
        };

        int index = profileCutOperations.Count;
        profileCutOperations.Add(operation);
        measurementOperationCount = profileCutOperations.Count;
        return index;
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

    private void AddMeasurementOperationToBrick(SparseCutBrick brick, int operationIndex)
    {
        if (brick == null || operationIndex < 0)
        {
            return;
        }

        if (brick.measurementOperationIndices == null)
        {
            brick.measurementOperationIndices = new List<int>(8);
        }

        brick.measurementOperationIndices.Add(operationIndex);
    }

    private void CompactMeasurementHistory()
    {
        int removeCount = Mathf.Max(1, profileCutOperations.Count / 4);
        profileCutOperations.RemoveRange(0, removeCount);

        foreach (SparseCutBrick brick in bricks.Values)
        {
            if (brick.measurementOperationIndices == null)
            {
                continue;
            }

            for (int i = brick.measurementOperationIndices.Count - 1; i >= 0; i--)
            {
                int nextIndex = brick.measurementOperationIndices[i] - removeCount;
                if (nextIndex < 0)
                {
                    brick.measurementOperationIndices.RemoveAt(i);
                }
                else
                {
                    brick.measurementOperationIndices[i] = nextIndex;
                }
            }
        }

        measurementOperationCount = profileCutOperations.Count;
    }

    private float SampleMeasurementSdfLocal(Vector3 localPoint)
    {
        float sdf = BoxSdf(localPoint - workpieceCenterMm, SanitizeSize(workpieceSizeMm));
        Vector3Int brickIndex = LocalToBrickIndex(localPoint);
        if (!IsBrickIndexInRange(brickIndex) || !bricks.TryGetValue(brickIndex, out SparseCutBrick brick))
        {
            return sdf;
        }

        if (brick.measurementOperationIndices == null)
        {
            return sdf;
        }

        for (int i = 0; i < brick.measurementOperationIndices.Count; i++)
        {
            int operationIndex = brick.measurementOperationIndices[i];
            if (operationIndex < 0 || operationIndex >= profileCutOperations.Count)
            {
                continue;
            }

            ProfileCutOperation operation = profileCutOperations[operationIndex];
            if (!operation.localBounds.Contains(localPoint))
            {
                continue;
            }

            float cutterDifference = SweptProfileCutterDifference(localPoint, operation, profileSegmentCount);
            if (cutterDifference > sdf)
            {
                sdf = cutterDifference;
            }
        }

        return sdf;
    }

    private float RefineMeasurementHit(
        Vector3 localOrigin,
        Vector3 localDirection,
        float lowT,
        float highT,
        float lowSdf,
        float highSdf,
        out float hitSdf,
        out int iterations)
    {
        iterations = 0;
        float targetPrecision = Mathf.Max(0.0001f, measurementPrecisionMm);

        for (int i = 0; i < measurementBinaryIterations && Mathf.Abs(highT - lowT) > targetPrecision; i++)
        {
            float midT = (lowT + highT) * 0.5f;
            float midSdf = SampleMeasurementSdfLocal(localOrigin + localDirection * midT);

            if (HasSurfaceCrossing(lowSdf, midSdf))
            {
                highT = midT;
                highSdf = midSdf;
            }
            else
            {
                lowT = midT;
                lowSdf = midSdf;
            }

            iterations++;
        }

        hitSdf = Mathf.Abs(lowSdf) < Mathf.Abs(highSdf) ? lowSdf : highSdf;
        return Mathf.Abs(lowSdf) < Mathf.Abs(highSdf) ? lowT : highT;
    }

    private double RefineMeasurementHitDouble(
        double originX, double originY, double originZ,
        double dirX, double dirY, double dirZ,
        double lowT,
        double highT,
        float lowSdf,
        float highSdf,
        out float hitSdf,
        out int iterations)
    {
        iterations = 0;
        double targetPrecision = System.Math.Max(0.0000001, (double)measurementPrecisionMm);

        for (int i = 0; i < measurementBinaryIterations && System.Math.Abs(highT - lowT) > targetPrecision; i++)
        {
            double midT = (lowT + highT) * 0.5;
            double px = originX + dirX * midT;
            double py = originY + dirY * midT;
            double pz = originZ + dirZ * midT;
            float midSdf = (float)SampleMeasurementSdfLocalDouble(px, py, pz);

            if (HasSurfaceCrossing(lowSdf, midSdf))
            {
                highT = midT;
                highSdf = midSdf;
            }
            else
            {
                lowT = midT;
                lowSdf = midSdf;
            }

            iterations++;
        }

        hitSdf = System.Math.Abs(lowSdf) < System.Math.Abs(highSdf) ? lowSdf : highSdf;
        return System.Math.Abs(lowSdf) < System.Math.Abs(highSdf) ? lowT : highT;
    }

    private static bool HasSurfaceCrossing(float a, float b)
    {
        if (Mathf.Approximately(a, 0f) || Mathf.Approximately(b, 0f))
        {
            return true;
        }

        return (a > 0f && b < 0f) || (a < 0f && b > 0f);
    }

    private static float BoxSdf(Vector3 localPoint, Vector3 size)
    {
        Vector3 halfSize = size * 0.5f;
        Vector3 q = Abs(localPoint) - halfSize;
        Vector3 outside = Max(q, Vector3.zero);
        float inside = Mathf.Min(Mathf.Max(q.x, Mathf.Max(q.y, q.z)), 0f);
        return outside.magnitude + inside;
    }

    private static float SweptProfileCutterDifference(Vector3 samplePoint, ProfileCutOperation operation, int segmentCount)
    {
        Vector3 axis = operation.localAxis.sqrMagnitude < 0.000001f ? Vector3.up : operation.localAxis.normalized;
        Vector3 motion = operation.localEnd - operation.localStart;
        Vector3 radialMotion = motion - axis * Vector3.Dot(motion, axis);
        Vector3 toSampleFromStart = samplePoint - operation.localStart;
        Vector3 radialFromStart = toSampleFromStart - axis * Vector3.Dot(toSampleFromStart, axis);
        float motionEpsilon = Mathf.Max(operation.localRadius, operation.localHeight) * 0.000001f;
        float motionEpsilonSqr = motionEpsilon * motionEpsilon;

        // Primary t: radial projection (best for lateral sweeps)
        float tRadial = 1f;
        float radialMotionLengthSqr = radialMotion.sqrMagnitude;
        if (radialMotionLengthSqr > motionEpsilonSqr)
        {
            tRadial = Mathf.Clamp01(Vector3.Dot(radialFromStart, radialMotion) / radialMotionLengthSqr);
        }

        // Secondary t: full 3D projection (best for axial sweeps)
        float tFull = 1f;
        float motionLengthSqr = motion.sqrMagnitude;
        if (motionLengthSqr > motionEpsilonSqr)
        {
            tFull = Mathf.Clamp01(Vector3.Dot(samplePoint - operation.localStart, motion) / motionLengthSqr);
        }

        // Evaluate at both candidate t values and a few intermediate samples
        // to better approximate the true swept volume envelope
        float bestDifference = float.MinValue;
        int sampleCount = motionLengthSqr > motionEpsilonSqr ? 8 : 1;

        for (int i = 0; i < sampleCount; i++)
        {
            float t;
            if (sampleCount == 1)
            {
                t = tRadial;
            }
            else if (i == 0)
            {
                t = tRadial;
            }
            else if (i == 1)
            {
                t = tFull;
            }
            else
            {
                t = (float)(i - 1) / (sampleCount - 2);
            }

            Vector3 cutterRoot = Vector3.Lerp(operation.localStart, operation.localEnd, t);
            Vector3 toSample = samplePoint - cutterRoot;
            float axial = Vector3.Dot(toSample, axis);
            Vector3 radial = toSample - axis * axial;
            float profileRadius = CutterProfileRadius(axial, operation, segmentCount);
            float radialDifference = profileRadius - radial.magnitude;
            float axialDifference = Mathf.Min(axial, operation.localHeight - axial);
            float difference = Mathf.Min(radialDifference, axialDifference);

            if (difference > bestDifference)
            {
                bestDifference = difference;
            }
        }

        return bestDifference;
    }

    private static float CutterProfileRadius(float axial, ProfileCutOperation operation, int segmentCount)
    {
        float radius = operation.localRadius;
        float height = operation.localHeight;
        if (operation.profileRadiusSampleCount >= 2)
        {
            float sampledNormalizedHeight = Mathf.Clamp01(axial / Mathf.Max(height, 0.000001f));
            float samplePosition = sampledNormalizedHeight * (operation.profileRadiusSampleCount - 1);
            int sampleIndex = Mathf.Min(Mathf.FloorToInt(samplePosition), operation.profileRadiusSampleCount - 2);
            float sampleT = Mathf.Clamp01(samplePosition - sampleIndex);
            float sampleRadiusA = GetOperationProfileRadiusSample(operation, sampleIndex);
            float sampleRadiusB = GetOperationProfileRadiusSample(operation, sampleIndex + 1);
            return radius * Mathf.Lerp(sampleRadiusA, sampleRadiusB, Mathf.SmoothStep(0f, 1f, sampleT));
        }

        float normalizedHeight = Mathf.Clamp01(axial / Mathf.Max(height, 0.000001f));
        int segs = Mathf.Max(2, segmentCount);
        float profilePosition = normalizedHeight * segs;
        int segment = Mathf.Min(Mathf.FloorToInt(profilePosition), segs - 1);
        float t = Mathf.Clamp01(profilePosition - segment);
        float innerRadius = radius * 0.6535898f;
        bool segmentEven = (segment & 1) == 0;
        bool nextSegmentEven = ((segment + 1) & 1) == 0;
        float radiusA = segmentEven ? radius : innerRadius;
        float radiusB = nextSegmentEven ? radius : innerRadius;
        return Mathf.Lerp(radiusA, radiusB, Mathf.SmoothStep(0f, 1f, t));
    }

    private static float GetOperationProfileRadiusSample(ProfileCutOperation operation, int index)
    {
        if (index < 0 || index >= MaxProfileRadiusSamples)
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

    // ========================================================================
    // Double-precision measurement helpers
    // ========================================================================

    private double SampleMeasurementSdfLocalDouble(double px, double py, double pz)
    {
        Vector3 size = SanitizeSize(workpieceSizeMm);
        double boxSdf = BoxSdfDouble(
            px - workpieceCenterMm.x,
            py - workpieceCenterMm.y,
            pz - workpieceCenterMm.z,
            size);

        // Brick lookup uses float (brick grid is coarse enough)
        Vector3 localPoint = new Vector3((float)px, (float)py, (float)pz);
        Vector3Int brickIndex = LocalToBrickIndex(localPoint);
        if (!IsBrickIndexInRange(brickIndex) || !bricks.TryGetValue(brickIndex, out SparseCutBrick brick))
        {
            return boxSdf;
        }

        if (brick.measurementOperationIndices == null)
        {
            return boxSdf;
        }

        double sdf = boxSdf;
        for (int i = 0; i < brick.measurementOperationIndices.Count; i++)
        {
            int operationIndex = brick.measurementOperationIndices[i];
            if (operationIndex < 0 || operationIndex >= profileCutOperations.Count)
            {
                continue;
            }

            ProfileCutOperation operation = profileCutOperations[operationIndex];
            if (!operation.localBounds.Contains(localPoint))
            {
                continue;
            }

            double cutterDifference = SweptProfileCutterDifferenceDouble(px, py, pz, operation, profileSegmentCount);
            if (cutterDifference > sdf)
            {
                sdf = cutterDifference;
            }
        }

        return sdf;
    }

    private static double SweptProfileCutterDifferenceDouble(double px, double py, double pz, ProfileCutOperation operation, int segmentCount)
    {
        Vector3 axisRaw = operation.localAxis;
        double axLen = System.Math.Sqrt(axisRaw.x * (double)axisRaw.x + axisRaw.y * (double)axisRaw.y + axisRaw.z * (double)axisRaw.z);
        double axX = axLen > 0.000001 ? axisRaw.x / axLen : 0.0;
        double axY = axLen > 0.000001 ? axisRaw.y / axLen : 1.0;
        double axZ = axLen > 0.000001 ? axisRaw.z / axLen : 0.0;

        double mx = (double)operation.localEnd.x - operation.localStart.x;
        double my = (double)operation.localEnd.y - operation.localStart.y;
        double mz = (double)operation.localEnd.z - operation.localStart.z;

        double axDot = mx * axX + my * axY + mz * axZ;
        double rmx = mx - axX * axDot;
        double rmy = my - axY * axDot;
        double rmz = mz - axZ * axDot;

        double tsx = px - operation.localStart.x;
        double tsy = py - operation.localStart.y;
        double tsz = pz - operation.localStart.z;
        double rsx = tsx - axX * (tsx * axX + tsy * axY + tsz * axZ);
        double rsy = tsy - axY * (tsx * axX + tsy * axY + tsz * axZ);
        double rsz = tsz - axZ * (tsx * axX + tsy * axY + tsz * axZ);
        double motionEpsilon = System.Math.Max(operation.localRadius, operation.localHeight) * 0.000001;
        double motionEpsilonSqr = motionEpsilon * motionEpsilon;

        double tRadial = 1.0;
        double rmLen2 = rmx * rmx + rmy * rmy + rmz * rmz;
        if (rmLen2 > motionEpsilonSqr)
        {
            double dot = rsx * rmx + rsy * rmy + rsz * rmz;
            tRadial = Clamp01D(dot / rmLen2);
        }

        double tFull = 1.0;
        double mLen2 = mx * mx + my * my + mz * mz;
        if (mLen2 > motionEpsilonSqr)
        {
            double dot = tsx * mx + tsy * my + tsz * mz;
            tFull = Clamp01D(dot / mLen2);
        }

        int sampleCount = mLen2 > motionEpsilonSqr ? 8 : 1;
        double bestDiff = -1e30;

        for (int i = 0; i < sampleCount; i++)
        {
            double t;
            if (sampleCount == 1) t = tRadial;
            else if (i == 0) t = tRadial;
            else if (i == 1) t = tFull;
            else t = (double)(i - 1) / (sampleCount - 2);

            double crx = operation.localStart.x + mx * t;
            double cry = operation.localStart.y + my * t;
            double crz = operation.localStart.z + mz * t;

            double tosx = px - crx;
            double tosy = py - cry;
            double tosz = pz - crz;

            double axial = tosx * axX + tosy * axY + tosz * axZ;
            double rdx = tosx - axX * axial;
            double rdy = tosy - axY * axial;
            double rdz = tosz - axZ * axial;
            double radialDist = System.Math.Sqrt(rdx * rdx + rdy * rdy + rdz * rdz);

            double profileRadius = CutterProfileRadiusDouble(axial, operation, segmentCount);
            double radialDiff = profileRadius - radialDist;
            double axialDiff = System.Math.Min(axial, operation.localHeight - axial);
            double diff = System.Math.Min(radialDiff, axialDiff);

            if (diff > bestDiff) bestDiff = diff;
        }

        return bestDiff;
    }

    private static double CutterProfileRadiusDouble(double axial, ProfileCutOperation operation, int segmentCount)
    {
        float radius = operation.localRadius;
        float height = operation.localHeight;
        if (operation.profileRadiusSampleCount >= 2)
        {
            double sampledHeight = System.Math.Max(height, 0.000001);
            double sampledNorm = Clamp01D(axial / sampledHeight);
            double samplePosition = sampledNorm * (operation.profileRadiusSampleCount - 1);
            int sampleIndex = System.Math.Min((int)System.Math.Floor(samplePosition), operation.profileRadiusSampleCount - 2);
            double sampleT = Clamp01D(samplePosition - sampleIndex);
            double radiusA = GetOperationProfileRadiusSample(operation, sampleIndex);
            double radiusB = GetOperationProfileRadiusSample(operation, sampleIndex + 1);
            double sampleSmoothT = sampleT * sampleT * (3.0 - 2.0 * sampleT);
            return radius * (radiusA + (radiusB - radiusA) * sampleSmoothT);
        }

        double h = System.Math.Max(height, 0.000001);
        double norm = Clamp01D(axial / h);
        int segs = System.Math.Max(2, segmentCount);
        double profilePosition = norm * segs;
        int segment = System.Math.Min((int)System.Math.Floor(profilePosition), segs - 1);
        double t = Clamp01D(profilePosition - segment);
        double innerRadius = radius * 0.6535898;
        bool even = (segment & 1) == 0;
        bool nextEven = ((segment + 1) & 1) == 0;
        double rA = even ? radius : innerRadius;
        double rB = nextEven ? radius : innerRadius;
        double smoothT = t * t * (3.0 - 2.0 * t);
        return rA + (rB - rA) * smoothT;
    }

    private static double BoxSdfDouble(double px, double py, double pz, Vector3 size)
    {
        double hx = System.Math.Max(size.x * 0.5, 0.001);
        double hy = System.Math.Max(size.y * 0.5, 0.001);
        double hz = System.Math.Max(size.z * 0.5, 0.001);
        double qx = System.Math.Abs(px) - hx;
        double qy = System.Math.Abs(py) - hy;
        double qz = System.Math.Abs(pz) - hz;
        double ox = System.Math.Max(qx, 0.0);
        double oy = System.Math.Max(qy, 0.0);
        double oz = System.Math.Max(qz, 0.0);
        double outside = System.Math.Sqrt(ox * ox + oy * oy + oz * oz);
        double inside = System.Math.Min(System.Math.Max(qx, System.Math.Max(qy, qz)), 0.0);
        return outside + inside;
    }

    private static double Clamp01D(double v)
    {
        if (v < 0.0) return 0.0;
        if (v > 1.0) return 1.0;
        return v;
    }

    private bool IsBrickIndexInRange(Vector3Int index)
    {
        Vector3Int max = MaxBrickIndex;
        return index.x >= 0 && index.y >= 0 && index.z >= 0
            && index.x <= max.x && index.y <= max.y && index.z <= max.z;
    }

    private static Bounds BoundsFromMinMax(Vector3 min, Vector3 max)
    {
        Bounds bounds = new Bounds((min + max) * 0.5f, Vector3.zero);
        bounds.SetMinMax(min, max);
        return bounds;
    }

    private void EvictOldestGpuBricksIfNeeded()
    {
        while (residentGpuBrickCount > maxResidentGpuBricks)
        {
            SparseCutBrick oldest = null;
            foreach (SparseCutBrick brick in bricks.Values)
            {
                if (brick.sdfBuffer == null)
                {
                    continue;
                }

                if (oldest == null || brick.lastGpuTouchedFrame < oldest.lastGpuTouchedFrame)
                {
                    oldest = brick;
                }
            }

            if (oldest == null)
            {
                break;
            }

            ReleaseBrickGpuResources(oldest);
            residentGpuBrickCount = CountResidentGpuBricks();
        }
    }

    private int CountResidentGpuBricks()
    {
        int count = 0;
        foreach (SparseCutBrick brick in bricks.Values)
        {
            if (brick.sdfBuffer != null)
            {
                count++;
            }
        }

        return count;
    }

    private Vector3Int LocalToBrickIndex(Vector3 localPoint)
    {
        Vector3 offset = localPoint - TravelMin;
        float brickSize = BrickSizeMm;
        return new Vector3Int(
            Mathf.FloorToInt(offset.x / brickSize),
            Mathf.FloorToInt(offset.y / brickSize),
            Mathf.FloorToInt(offset.z / brickSize));
    }

    private Vector3Int ClampBrickIndex(Vector3Int index)
    {
        Vector3Int max = MaxBrickIndex;
        return new Vector3Int(
            Mathf.Clamp(index.x, 0, max.x),
            Mathf.Clamp(index.y, 0, max.y),
            Mathf.Clamp(index.z, 0, max.z));
    }

    private Bounds GetBrickBounds(Vector3Int index)
    {
        float brickSize = BrickSizeMm;
        Vector3 min = TravelMin + new Vector3(index.x, index.y, index.z) * brickSize;
        return new Bounds(min + Vector3.one * brickSize * 0.5f, Vector3.one * brickSize);
    }

    private bool ApplyProfileCutToBrick(SparseCutBrick brick, SparseProfileCutCommand cutCommand)
    {
        if (!cutCommand.isValid || !useGpuBrickSdf || brick == null || !EnsureBrickGpuSdf(brick))
        {
            return false;
        }

        Vector3 brickCenter = brick.localBounds.center;
        Vector3 brickStart = cutCommand.localStart - brickCenter;
        Vector3 brickEnd = cutCommand.localEnd - brickCenter;
        Vector3 brickAxis = cutCommand.localAxis.normalized;
        Vector3 startTop = brickStart + brickAxis * cutCommand.localHeight;
        Vector3 endTop = brickEnd + brickAxis * cutCommand.localHeight;
        float affectedRadius = cutCommand.localRadius + cutCommand.updateBand;
        Vector3 localMin = Vector3.Min(Vector3.Min(brickStart, brickEnd), Vector3.Min(startTop, endTop)) - Vector3.one * affectedRadius;
        Vector3 localMax = Vector3.Max(Vector3.Max(brickStart, brickEnd), Vector3.Max(startTop, endTop)) + Vector3.one * affectedRadius;

        float brickSize = BrickSizeMm;
        Vector3 gridMin = Vector3.one * (-brickSize * 0.5f);
        int sampleSize = SampleSizePerAxis;
        int minX = ClampSampleIndex(Mathf.FloorToInt((localMin.x - gridMin.x) / precisionMm) + 1, sampleSize);
        int maxX = ClampSampleIndex(Mathf.CeilToInt((localMax.x - gridMin.x) / precisionMm) + 1, sampleSize);
        int minY = ClampSampleIndex(Mathf.FloorToInt((localMin.y - gridMin.y) / precisionMm) + 1, sampleSize);
        int maxY = ClampSampleIndex(Mathf.CeilToInt((localMax.y - gridMin.y) / precisionMm) + 1, sampleSize);
        int minZ = ClampSampleIndex(Mathf.FloorToInt((localMin.z - gridMin.z) / precisionMm) + 1, sampleSize);
        int maxZ = ClampSampleIndex(Mathf.CeilToInt((localMax.z - gridMin.z) / precisionMm) + 1, sampleSize);

        if (maxX < minX || maxY < minY || maxZ < minZ)
        {
            return false;
        }

        try
        {
            changedCounterData[0] = 0;
            changedCounterBuffer.SetData(changedCounterData);

            sdfCutCompute.SetBuffer(sdfProfileCutterKernel, "_SdfSamples", brick.sdfBuffer);
            sdfCutCompute.SetBuffer(sdfProfileCutterKernel, "_ChangedCounter", changedCounterBuffer);
            sdfCutCompute.SetInts("_MinIndex", minX, minY, minZ);
            sdfCutCompute.SetInts("_MaxIndexExclusive", maxX + 1, maxY + 1, maxZ + 1);
            sdfCutCompute.SetInts("_SampleSize", sampleSize, sampleSize, sampleSize);
            sdfCutCompute.SetVector("_GridMin", gridMin);
            sdfCutCompute.SetVector("_LocalStart", brickStart);
            sdfCutCompute.SetVector("_LocalEnd", brickEnd);
            sdfCutCompute.SetVector("_CutterAxis", brickAxis);
            sdfCutCompute.SetFloat("_VoxelSize", precisionMm);
            sdfCutCompute.SetFloat("_LocalRadius", cutCommand.localRadius);
            sdfCutCompute.SetFloat("_CutterHeight", cutCommand.localHeight);
            sdfCutCompute.SetFloat("_CutBand", cutCommand.updateBand);
            sdfCutCompute.SetFloat("_AffectedRadiusSqr", affectedRadius * affectedRadius);
            sdfCutCompute.SetFloat("_ChangeEpsilon", precisionMm * 0.0001f);
            sdfCutCompute.SetInt("_ProfileSegmentCount", Mathf.Max(2, profileSegmentCount));
            sdfCutCompute.SetInt("_ProfileRadiusSampleCount", cutterProfileRadiusSampleCount);
            sdfCutCompute.SetFloats("_ProfileRadiusSamples", cutterProfileRadiusSamples);

            int threadGroupsX = Mathf.Max(1, Mathf.CeilToInt((maxX - minX + 1) / 8f));
            int threadGroupsY = Mathf.Max(1, Mathf.CeilToInt((maxY - minY + 1) / 8f));
            int threadGroupsZ = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ + 1) / 4f));
            sdfCutCompute.Dispatch(sdfProfileCutterKernel, threadGroupsX, threadGroupsY, threadGroupsZ);
            brick.lastGpuTouchedFrame = Time.frameCount;
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"Sparse brick GPU SDF cut failed: {exception.Message}", this);
            gpuUnavailable = true;
            ReleaseBrickGpuResources(brick);
            residentGpuBrickCount = CountResidentGpuBricks();
            return false;
        }
    }

    private bool EnsureBrickGpuSdf(SparseCutBrick brick)
    {
        if (brick == null || !EnsureGpuProgram())
        {
            return false;
        }

        int sampleCount = SdfSampleCount;
        if (brick.sdfBuffer != null && brick.sdfSampleCount == sampleCount)
        {
            return true;
        }

        ReleaseBrickGpuResources(brick);

        try
        {
            brick.sdfBuffer = new ComputeBuffer(sampleCount, sizeof(float));
            brick.sdfSampleCount = sampleCount;
            DispatchBrickInitialization(brick);
            brick.lastGpuTouchedFrame = Time.frameCount;
            residentGpuBrickCount = CountResidentGpuBricks();
            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"Sparse brick GPU SDF allocation failed: {exception.Message}", this);
            gpuUnavailable = true;
            ReleaseBrickGpuResources(brick);
            residentGpuBrickCount = CountResidentGpuBricks();
            return false;
        }
    }

    private bool EnsureGpuProgram()
    {
        if (gpuUnavailable || !useGpuBrickSdf || !SystemInfo.supportsComputeShaders)
        {
            return false;
        }

        if (sdfCutCompute == null)
        {
            sdfCutCompute = Resources.Load<ComputeShader>("Cutting/WorkpieceSdfCut");
        }

        if (sdfCutCompute == null)
        {
            gpuUnavailable = true;
            return false;
        }

        try
        {
            if (sdfProfileCutterKernel < 0)
            {
                sdfProfileCutterKernel = sdfCutCompute.FindKernel("CutProfileCutterSdf");
            }

            if (sdfInitKernel < 0)
            {
                sdfInitKernel = sdfCutCompute.FindKernel("InitializeBoxSdf");
            }

            if (changedCounterBuffer == null)
            {
                changedCounterBuffer = new ComputeBuffer(1, sizeof(int));
            }

            return true;
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"Sparse brick GPU SDF setup failed: {exception.Message}", this);
            gpuUnavailable = true;
            return false;
        }
    }

    private void DispatchBrickInitialization(SparseCutBrick brick)
    {
        float brickSize = BrickSizeMm;
        int sampleSize = SampleSizePerAxis;
        Vector3 gridMin = Vector3.one * (-brickSize * 0.5f);

        sdfCutCompute.SetBuffer(sdfInitKernel, "_SdfSamples", brick.sdfBuffer);
        sdfCutCompute.SetInts("_SampleSize", sampleSize, sampleSize, sampleSize);
        sdfCutCompute.SetVector("_GridMin", gridMin);
        sdfCutCompute.SetVector("_LocalSize", Vector3.one * brickSize);
        sdfCutCompute.SetFloat("_VoxelSize", precisionMm);

        int threadGroups = Mathf.CeilToInt(sampleSize / 8f);
        int threadGroupsZ = Mathf.CeilToInt(sampleSize / 4f);
        sdfCutCompute.Dispatch(sdfInitKernel, threadGroups, threadGroups, threadGroupsZ);
    }

    private void DrawGpuBricks()
    {
        if (!drawGpuBricks || !useGpuBrickSdf || bricks.Count == 0 || !EnsureGpuRenderResources())
        {
            return;
        }

        int sampleSize = SampleSizePerAxis;
        float brickSize = BrickSizeMm;
        Vector4 sampleSizeVector = new Vector4(sampleSize, sampleSize, sampleSize, 0f);
        Vector4 gridMin = new Vector4(-brickSize * 0.5f, -brickSize * 0.5f, -brickSize * 0.5f, 0f);
        Vector4 localSize = new Vector4(brickSize, brickSize, brickSize, 0f);
        int drawn = 0;

        foreach (SparseCutBrick brick in bricks.Values)
        {
            if (brick.sdfBuffer == null)
            {
                continue;
            }

            Matrix4x4 localToWorld = transform.localToWorldMatrix * Matrix4x4.Translate(brick.localBounds.center);
            Matrix4x4 worldToLocal = localToWorld.inverse;

            materialPropertyBlock.Clear();
            materialPropertyBlock.SetBuffer("_SdfSamples", brick.sdfBuffer);
            materialPropertyBlock.SetVector("_SampleSize", sampleSizeVector);
            materialPropertyBlock.SetVector("_GridMin", gridMin);
            materialPropertyBlock.SetVector("_LocalSize", localSize);
            materialPropertyBlock.SetFloat("_VoxelSize", precisionMm);
            materialPropertyBlock.SetFloat("_IsoLevel", 0f);
            materialPropertyBlock.SetFloat("_MaxSteps", Mathf.Max(16, gpuRaymarchMaxSteps));
            materialPropertyBlock.SetFloat("_StepScale", Mathf.Clamp(gpuRaymarchStepScale, 0.25f, 2f));
            materialPropertyBlock.SetMatrix("_LocalToWorld", localToWorld);
            materialPropertyBlock.SetMatrix("_WorldToLocal", worldToLocal);
            materialPropertyBlock.SetColor("_BaseColor", gpuBrickColor);
            materialPropertyBlock.SetVector("_LightDirection", new Vector4(0.35f, 0.8f, -0.45f, 0f));

            Graphics.DrawMesh(
                brickProxyMesh,
                localToWorld,
                runtimeGpuSurfaceMaterial,
                gameObject.layer,
                null,
                0,
                materialPropertyBlock,
                ShadowCastingMode.On,
                true);

            drawn++;
            if (drawn >= maxRenderedGpuBricks)
            {
                break;
            }
        }
    }

    private bool EnsureGpuRenderResources()
    {
        EnsureGpuMaterial();
        EnsureGpuBrickProxyMesh();

        if (materialPropertyBlock == null)
        {
            materialPropertyBlock = new MaterialPropertyBlock();
        }

        return runtimeGpuSurfaceMaterial != null && brickProxyMesh != null;
    }

    private void EnsureGpuMaterial()
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
                name = "Runtime Sparse Brick SDF Material"
            };
        }
    }

    private void EnsureGpuBrickProxyMesh()
    {
        float brickSize = BrickSizeMm;
        if (brickProxyMesh != null && Mathf.Approximately(cachedProxyBrickSize, brickSize))
        {
            return;
        }

        if (brickProxyMesh != null)
        {
            DestroyRuntimeObject(brickProxyMesh);
        }

        brickProxyMesh = BuildCubeProxyMesh(brickSize);
        cachedProxyBrickSize = brickSize;
    }

    private static Mesh BuildCubeProxyMesh(float size)
    {
        Vector3 half = Vector3.one * (size * 0.5f);
        Vector3[] meshVertices =
        {
            new Vector3(-half.x, -half.y, -half.z),
            new Vector3(half.x, -half.y, -half.z),
            new Vector3(half.x, half.y, -half.z),
            new Vector3(-half.x, half.y, -half.z),
            new Vector3(-half.x, -half.y, half.z),
            new Vector3(half.x, -half.y, half.z),
            new Vector3(half.x, half.y, half.z),
            new Vector3(-half.x, half.y, half.z)
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
            name = "Runtime Sparse Brick SDF Proxy"
        };
        mesh.SetVertices(meshVertices);
        mesh.SetTriangles(meshTriangles, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    private void ReleaseAllGpuResources()
    {
        ReleaseAllBrickGpuResources();

        if (changedCounterBuffer != null)
        {
            changedCounterBuffer.Release();
            changedCounterBuffer = null;
        }

        if (runtimeGpuSurfaceMaterial != null && runtimeGpuSurfaceMaterial != gpuSurfaceMaterial)
        {
            DestroyRuntimeObject(runtimeGpuSurfaceMaterial);
        }

        if (brickProxyMesh != null)
        {
            DestroyRuntimeObject(brickProxyMesh);
        }

        runtimeGpuSurfaceMaterial = null;
        materialPropertyBlock = null;
        brickProxyMesh = null;
        cachedProxyBrickSize = 0f;
        sdfProfileCutterKernel = -1;
        sdfInitKernel = -1;
    }

    private void ReleaseAllBrickGpuResources()
    {
        foreach (SparseCutBrick brick in bricks.Values)
        {
            ReleaseBrickGpuResources(brick);
        }

        residentGpuBrickCount = 0;
    }

    private static void ReleaseBrickGpuResources(SparseCutBrick brick)
    {
        if (brick == null || brick.sdfBuffer == null)
        {
            return;
        }

        brick.sdfBuffer.Release();
        brick.sdfBuffer = null;
        brick.sdfSampleCount = 0;
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

    private float WorldDistanceToLocalDistance(float worldDistance)
    {
        Vector3 scale = transform.lossyScale;
        float minScale = Mathf.Max(0.0001f, Mathf.Min(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
        return worldDistance / minScale;
    }

    private static Vector3 SanitizeSize(Vector3 size)
    {
        return new Vector3(
            Mathf.Max(0.001f, size.x),
            Mathf.Max(0.001f, size.y),
            Mathf.Max(0.001f, size.z));
    }

    private static Vector3 Abs(Vector3 value)
    {
        return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
    }

    private static Vector3 Max(Vector3 a, Vector3 b)
    {
        return new Vector3(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y), Mathf.Max(a.z, b.z));
    }

    private static int ClampSampleIndex(int index, int sampleSize)
    {
        return Mathf.Clamp(index, 0, Mathf.Max(0, sampleSize - 1));
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawDebugBricks || bricks.Count == 0)
        {
            return;
        }

        Gizmos.color = debugBrickColor;
        int drawn = 0;
        foreach (SparseCutBrick brick in bricks.Values)
        {
            Gizmos.DrawWireCube(transform.TransformPoint(brick.localBounds.center), Vector3.Scale(brick.localBounds.size, transform.lossyScale));
            drawn++;
            if (drawn >= maxDebugBricks)
            {
                break;
            }
        }
    }
}
