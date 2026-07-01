using UnityEngine;

public struct WorkpieceMeasurementHit
{
    public float localDistanceMm;
    public float worldDistance;
    public Vector3 localPoint;
    public Vector3 worldPoint;
    public float signedDistanceMm;
}

public struct WorkpieceThicknessMeasurement
{
    public float entryDistanceMm;
    public float exitDistanceMm;
    public float thicknessMm;
    public float entryWorldDistance;
    public float exitWorldDistance;
    public Vector3 entryLocalPoint;
    public Vector3 exitLocalPoint;
    public Vector3 entryWorldPoint;
    public Vector3 exitWorldPoint;
}

[DisallowMultipleComponent]
public sealed class WorkpieceMeasurement : MonoBehaviour
{
    public WorkpieceVoxel workpiece;
    [Min(0.000001f)] public float measurementPrecisionMm = 0.001f;
    [Min(0.001f)] public float defaultMaxDistanceMm = 1500f;
    [Min(0.001f)] public float maxMarchStepMm = 1f;
    [Min(16)] public int maxMarchIterations = 4096;

    public bool TryMeasureSurfaceWorldRay(Ray worldRay, out WorkpieceMeasurementHit hit)
    {
        return TryMeasureSurfaceWorldRay(worldRay.origin, worldRay.direction, defaultMaxDistanceMm, out hit);
    }

    public bool TryMeasureSurfaceWorldRay(
        Vector3 worldOrigin,
        Vector3 worldDirection,
        float maxDistanceMm,
        out WorkpieceMeasurementHit hit)
    {
        hit = default;
        if (!EnsureWorkpiece() || worldDirection.sqrMagnitude <= 0.000000000001f)
        {
            return false;
        }

        Vector3 localOrigin = workpiece.transform.InverseTransformPoint(worldOrigin);
        Vector3 localDirection = workpiece.transform.InverseTransformDirection(worldDirection);
        if (localDirection.sqrMagnitude <= 0.000000000001f)
        {
            return false;
        }

        localDirection.Normalize();
        float safeMaxDistance = Mathf.Max(0.001f, maxDistanceMm);
        float safePrecision = MeasurementPrecisionForNative();
        float safeMaxStep = Mathf.Max(safePrecision, maxMarchStepMm);
        int safeIterations = Mathf.Max(16, maxMarchIterations);

        try
        {
            int result = SdfNativePlugin.sdf_measure_surface_ray(
                localOrigin.x,
                localOrigin.y,
                localOrigin.z,
                localDirection.x,
                localDirection.y,
                localDirection.z,
                safeMaxDistance,
                safePrecision,
                safeMaxStep,
                safeIterations,
                out float localDistance,
                out float hitX,
                out float hitY,
                out float hitZ,
                out float signedDistance);

            if (result == 0)
            {
                return false;
            }

            Vector3 localPoint = new Vector3(hitX, hitY, hitZ);
            Vector3 worldPoint = workpiece.transform.TransformPoint(localPoint);
            hit = new WorkpieceMeasurementHit
            {
                localDistanceMm = localDistance,
                worldDistance = Vector3.Distance(worldOrigin, worldPoint),
                localPoint = localPoint,
                worldPoint = worldPoint,
                signedDistanceMm = signedDistance
            };
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native surface measurement failed: {ex.Message}", this);
            return false;
        }
    }

    public bool TryMeasureThicknessWorldRay(Ray worldRay, out WorkpieceThicknessMeasurement measurement)
    {
        return TryMeasureThicknessWorldRay(
            worldRay.origin,
            worldRay.direction,
            defaultMaxDistanceMm,
            out measurement);
    }

    public bool TryMeasureThicknessWorldRay(
        Vector3 worldOrigin,
        Vector3 worldDirection,
        float maxDistanceMm,
        out WorkpieceThicknessMeasurement measurement)
    {
        measurement = default;
        if (!EnsureWorkpiece() || worldDirection.sqrMagnitude <= 0.000000000001f)
        {
            return false;
        }

        Vector3 localOrigin = workpiece.transform.InverseTransformPoint(worldOrigin);
        Vector3 localDirection = workpiece.transform.InverseTransformDirection(worldDirection);
        if (localDirection.sqrMagnitude <= 0.000000000001f)
        {
            return false;
        }

        localDirection.Normalize();
        float safeMaxDistance = Mathf.Max(0.001f, maxDistanceMm);
        float safePrecision = MeasurementPrecisionForNative();
        float safeMaxStep = Mathf.Max(safePrecision, maxMarchStepMm);
        int safeIterations = Mathf.Max(32, maxMarchIterations);

        try
        {
            int result = SdfNativePlugin.sdf_measure_thickness_ray(
                localOrigin.x,
                localOrigin.y,
                localOrigin.z,
                localDirection.x,
                localDirection.y,
                localDirection.z,
                safeMaxDistance,
                safePrecision,
                safeMaxStep,
                safeIterations,
                out float entryDistance,
                out float exitDistance,
                out float thickness,
                out float entryX,
                out float entryY,
                out float entryZ,
                out float exitX,
                out float exitY,
                out float exitZ);

            if (result == 0)
            {
                return false;
            }

            Vector3 entryLocal = new Vector3(entryX, entryY, entryZ);
            Vector3 exitLocal = new Vector3(exitX, exitY, exitZ);
            Vector3 entryWorld = workpiece.transform.TransformPoint(entryLocal);
            Vector3 exitWorld = workpiece.transform.TransformPoint(exitLocal);
            measurement = new WorkpieceThicknessMeasurement
            {
                entryDistanceMm = entryDistance,
                exitDistanceMm = exitDistance,
                thicknessMm = thickness,
                entryWorldDistance = Vector3.Distance(worldOrigin, entryWorld),
                exitWorldDistance = Vector3.Distance(worldOrigin, exitWorld),
                entryLocalPoint = entryLocal,
                exitLocalPoint = exitLocal,
                entryWorldPoint = entryWorld,
                exitWorldPoint = exitWorld
            };
            return true;
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"Native thickness measurement failed: {ex.Message}", this);
            return false;
        }
    }

    private bool EnsureWorkpiece()
    {
        if (workpiece == null)
        {
            workpiece = GetComponent<WorkpieceVoxel>();
        }

        if (workpiece == null)
        {
            workpiece = Object.FindFirstObjectByType<WorkpieceVoxel>();
        }

        return workpiece != null;
    }

    private float MeasurementPrecisionForNative()
    {
        return Mathf.Clamp(measurementPrecisionMm, 0.000001f, 0.001f);
    }
}
