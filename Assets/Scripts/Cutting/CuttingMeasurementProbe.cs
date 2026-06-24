using UnityEngine;

[DisallowMultipleComponent]
public sealed class CuttingMeasurementProbe : MonoBehaviour
{
    [Header("Measurement")]
    public SparseCutHistory history;
    public Vector3 localDirection = Vector3.down;
    [Min(0.001f)] public float maxDistanceMm = 200f;
    public bool measureEveryFrame = true;

    [Header("Result")]
    [SerializeField] private bool lastHit;
    [SerializeField] private float lastDistanceMm;
    [SerializeField] private Vector3 lastWorldPoint;
    [SerializeField] private float lastSignedDistanceMm;
    [SerializeField] private int lastIterations;

    [Header("Debug")]
    public bool drawGizmos = true;
    public Color rayColor = new Color(0.1f, 0.65f, 1f, 0.8f);
    public Color hitColor = new Color(1f, 0.9f, 0.1f, 1f);
    [Min(0.001f)] public float hitGizmoRadius = 0.015f;

    public bool LastHit
    {
        get { return lastHit; }
    }

    public float LastDistanceMm
    {
        get { return lastDistanceMm; }
    }

    public Vector3 LastWorldPoint
    {
        get { return lastWorldPoint; }
    }

    public float LastSignedDistanceMm
    {
        get { return lastSignedDistanceMm; }
    }

    private void Start()
    {
        if (history == null)
        {
            history = Object.FindFirstObjectByType<SparseCutHistory>();
        }
    }

    private void Update()
    {
        if (measureEveryFrame)
        {
            MeasureNow();
        }
    }

    [ContextMenu("Measure Now")]
    public void MeasureNow()
    {
        if (history == null)
        {
            history = Object.FindFirstObjectByType<SparseCutHistory>();
        }

        if (history == null)
        {
            ClearResult();
            return;
        }

        Vector3 direction = transform.TransformDirection(localDirection);
        if (history.TryMeasureSurfaceAlongRay(transform.position, direction, maxDistanceMm, out SparseCutHistory.MeasurementResult result))
        {
            lastHit = result.hit;
            lastDistanceMm = result.distanceMm;
            lastWorldPoint = result.worldPoint;
            lastSignedDistanceMm = result.signedDistanceMm;
            lastIterations = result.iterations;
        }
        else
        {
            ClearResult();
        }
    }

    private void ClearResult()
    {
        lastHit = false;
        lastDistanceMm = 0f;
        lastWorldPoint = Vector3.zero;
        lastSignedDistanceMm = 0f;
        lastIterations = 0;
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos)
        {
            return;
        }

        Vector3 direction = transform.TransformDirection(localDirection);
        if (direction.sqrMagnitude < 0.000001f)
        {
            return;
        }

        direction.Normalize();
        Gizmos.color = rayColor;
        Gizmos.DrawLine(transform.position, transform.position + direction * maxDistanceMm);

        if (!lastHit)
        {
            return;
        }

        Gizmos.color = hitColor;
        Gizmos.DrawSphere(lastWorldPoint, hitGizmoRadius);
    }
}
