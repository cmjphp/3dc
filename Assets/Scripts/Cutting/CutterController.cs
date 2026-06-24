using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
public sealed class CutterController : MonoBehaviour
{
    [Header("Cutting")]
    public WorkpieceVoxel workpiece;
    public SparseCutHistory sparseHistory;
    [Min(0.001f)] public float cutterRadius = 3f;
    [Min(0.001f)] public float cutInterval = 0.015f;
    [Min(0.001f)] public float meshRebuildInterval = 0.016f;
    public bool cutContinuously = true;
    public bool sweepBetweenCutPositions = true;
    public bool cutEveryFrame = true;
    public bool requestMeshRebuildAfterEachCut = true;
    public bool useProfileCutter = true;
    [Min(0.001f)] public float cutterHeightScale = 1f;

    [Header("Movement")]
    [Min(0.001f)] public float moveSpeed = 50f;
    [Min(0.001f)] public float verticalSpeed = 20f;
    [Min(1f)] public float shiftMultiplier = 3f;

    [Header("Auto Test Path")]
    public bool autoMove;
    public bool autoMoveStraightLine = true;
    public Vector3 autoMoveExtents = new Vector3(1.2f, 0f, 0f);
    [Min(0.001f)] public float autoMoveSpeed = 1.2f;

    private Vector3 startPosition;
    private Vector3 autoMoveOrigin;
    private float autoMovePhase;
    private float nextCutTime;
    private float nextMeshRebuildTime;
    private Vector3 lastCutPosition;
    private bool hasLastCutPosition;
    private bool hasPrimedCutPosition;
    private bool meshRebuildPending;

    public void PrimeCutSweep(Vector3 worldStart)
    {
        lastCutPosition = worldStart;
        hasLastCutPosition = true;
        hasPrimedCutPosition = true;
    }

    public void PrimeCutSweepFromAutomaticEntry()
    {
        PrimeCutSweep(GetAutomaticEntryPosition());
    }

    public void ResetCutSweep()
    {
        PrimeCutSweep(transform.position);
    }

    private void Start()
    {
        if (workpiece == null)
        {
            workpiece = Object.FindFirstObjectByType<WorkpieceVoxel>();
        }

        if (sparseHistory == null)
        {
            sparseHistory = Object.FindFirstObjectByType<SparseCutHistory>();
        }

        startPosition = transform.position;
        autoMoveOrigin = startPosition;
        autoMovePhase = autoMoveStraightLine ? 1f : 0f;
        if (!hasPrimedCutPosition)
        {
            lastCutPosition = GetAutomaticEntryPosition();
            hasLastCutPosition = true;
            hasPrimedCutPosition = lastCutPosition != transform.position;
        }
        nextCutTime = Time.time;
        nextMeshRebuildTime = Time.time;
    }

    private Vector3 GetAutomaticEntryPosition()
    {
        if (workpiece == null)
        {
            return transform.position;
        }

        Bounds bounds = workpiece.LocalBounds;
        Vector3 localPosition = workpiece.transform.InverseTransformPoint(transform.position);
        if (!bounds.Contains(localPosition))
        {
            return transform.position;
        }

        Vector3 localAxis = workpiece.transform.InverseTransformDirection(transform.up).normalized;
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;
        float exitDistance = float.PositiveInfinity;
        AccumulateExitDistance(localPosition.x, localAxis.x, min.x, max.x, ref exitDistance);
        AccumulateExitDistance(localPosition.y, localAxis.y, min.y, max.y, ref exitDistance);
        AccumulateExitDistance(localPosition.z, localAxis.z, min.z, max.z, ref exitDistance);

        if (float.IsInfinity(exitDistance) || exitDistance < 0f)
        {
            return transform.position;
        }

        Vector3 localEntry = localPosition + localAxis * (exitDistance + workpiece.voxelSize);
        return workpiece.transform.TransformPoint(localEntry);
    }

    private static void AccumulateExitDistance(
        float position,
        float direction,
        float min,
        float max,
        ref float exitDistance)
    {
        const float axisEpsilon = 0.000001f;
        if (Mathf.Abs(direction) <= axisEpsilon)
        {
            return;
        }

        float distance = direction > 0f
            ? (max - position) / direction
            : (min - position) / direction;
        if (distance >= 0f)
        {
            exitDistance = Mathf.Min(exitDistance, distance);
        }
    }

    private void Update()
    {
        HandleModeKeys();

        if (autoMove)
        {
            UpdateAutoMove();
        }
        else
        {
            UpdateManualMove();
        }

        if (CuttingInput.WasKeyPressed(KeyCode.Space))
        {
            TryCutAtCurrentPosition(true);
            nextCutTime = Time.time + cutInterval;
        }

        if (cutContinuously && (cutEveryFrame || Time.time >= nextCutTime))
        {
            TryContinuousCut();
            if (!cutEveryFrame)
            {
                nextCutTime = Time.time + cutInterval;
            }
        }

        FlushMeshRebuildIfDue();
    }

    private void HandleModeKeys()
    {
        if (CuttingInput.WasKeyPressed(KeyCode.R))
        {
            transform.position = startPosition;
            autoMoveOrigin = startPosition;
            autoMovePhase = autoMoveStraightLine ? 1f : 0f;
            lastCutPosition = transform.position;
            hasLastCutPosition = true;
        }

        if (CuttingInput.WasKeyPressed(KeyCode.T))
        {
            autoMove = !autoMove;
            autoMoveOrigin = transform.position;
            autoMovePhase = autoMoveStraightLine ? 1f : 0f;
            lastCutPosition = transform.position;
            hasLastCutPosition = true;
        }
    }

    private void UpdateManualMove()
    {
        float x = 0f;
        float y = 0f;
        float z = 0f;

        if (CuttingInput.IsKeyHeld(KeyCode.A))
        {
            x -= 1f;
        }

        if (CuttingInput.IsKeyHeld(KeyCode.D))
        {
            x += 1f;
        }

        if (CuttingInput.IsKeyHeld(KeyCode.S))
        {
            z -= 1f;
        }

        if (CuttingInput.IsKeyHeld(KeyCode.W))
        {
            z += 1f;
        }

        if (CuttingInput.IsKeyHeld(KeyCode.Q))
        {
            y -= 1f;
        }

        if (CuttingInput.IsKeyHeld(KeyCode.E))
        {
            y += 1f;
        }

        Vector3 horizontal = new Vector3(x, 0f, z);
        if (horizontal.sqrMagnitude > 1f)
        {
            horizontal.Normalize();
        }

        Vector3 vertical = Vector3.up * y;
        float speedMultiplier = CuttingInput.IsKeyHeld(KeyCode.LeftShift) || CuttingInput.IsKeyHeld(KeyCode.RightShift)
            ? shiftMultiplier
            : 1f;

        Vector3 delta = horizontal * moveSpeed + vertical * verticalSpeed;
        transform.position += delta * speedMultiplier * Time.deltaTime;
    }

    private void UpdateAutoMove()
    {
        autoMovePhase += Time.deltaTime * autoMoveSpeed;

        Vector3 offset;
        if (autoMoveStraightLine)
        {
            float cycle = Mathf.PingPong(autoMovePhase, 4f);
            float signedDistance = (cycle < 2f ? cycle : 4f - cycle) - 1f;
            offset = autoMoveExtents * signedDistance;
        }
        else
        {
            offset = new Vector3(
                Mathf.Sin(autoMovePhase) * autoMoveExtents.x,
                Mathf.Sin(autoMovePhase * 0.37f) * autoMoveExtents.y,
                Mathf.Sin(autoMovePhase * 0.71f) * autoMoveExtents.z);
        }

        transform.position = autoMoveOrigin + offset;
    }

    private void TryCutAtCurrentPosition(bool requestMeshImmediately)
    {
        if (workpiece == null)
        {
            return;
        }

        bool changed = CutWorkpiece(transform.position, transform.position);
        lastCutPosition = transform.position;
        hasLastCutPosition = true;

        if (changed)
        {
            meshRebuildPending = true;
            if (requestMeshImmediately)
            {
                workpiece.RequestRebuildMesh();
                meshRebuildPending = false;
                nextMeshRebuildTime = Time.time + meshRebuildInterval;
            }
        }
    }

    private void TryContinuousCut()
    {
        if (workpiece == null)
        {
            return;
        }

        Vector3 currentPosition = transform.position;
        if (!hasLastCutPosition)
        {
            lastCutPosition = GetAutomaticEntryPosition();
            hasLastCutPosition = true;
        }

        float movementEpsilon = Mathf.Max(
            workpiece.voxelSize * 0.1f,
            cutterRadius * 0.000001f);
        if ((currentPosition - lastCutPosition).sqrMagnitude <= movementEpsilon * movementEpsilon)
        {
            return;
        }

        bool changed = sweepBetweenCutPositions
            ? CutWorkpiece(lastCutPosition, currentPosition)
            : CutWorkpiece(currentPosition, currentPosition);

        lastCutPosition = currentPosition;

        if (changed)
        {
            if (requestMeshRebuildAfterEachCut)
            {
                workpiece.RequestRebuildMesh();
                meshRebuildPending = false;
                nextMeshRebuildTime = Time.time + meshRebuildInterval;
            }
            else
            {
                meshRebuildPending = true;
            }
        }
    }

    private void FlushMeshRebuildIfDue()
    {
        if (!meshRebuildPending || workpiece == null || Time.time < nextMeshRebuildTime)
        {
            return;
        }

        workpiece.RequestRebuildMesh();
        meshRebuildPending = false;
        nextMeshRebuildTime = Time.time + meshRebuildInterval;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.2f, 0.05f, 0.35f);
        if (useProfileCutter)
        {
            float height = cutterRadius * cutterHeightScale;
            Gizmos.DrawWireSphere(transform.position, cutterRadius * 0.6535898f);
            Gizmos.DrawLine(transform.position, transform.position + transform.up * height);
            Gizmos.DrawWireSphere(transform.position + transform.up * height, cutterRadius * 0.6535898f);
        }
        else
        {
            Gizmos.DrawWireSphere(transform.position, cutterRadius);
        }
    }

    private bool CutWorkpiece(Vector3 worldStart, Vector3 worldEnd)
    {
        if (useProfileCutter)
        {
            float cutterHeight = cutterRadius * cutterHeightScale;
            if (sparseHistory != null && sparseHistory.enabled)
            {
                sparseHistory.RecordProfileCutterSweep(
                    worldStart,
                    worldEnd,
                    transform.up,
                    transform.right,
                    cutterRadius,
                    cutterHeight);
            }

            return workpiece.CutSweptProfileCutter(
                worldStart,
                worldEnd,
                transform.up,
                transform.right,
                cutterRadius,
                cutterHeight,
                false);
        }

        return worldStart == worldEnd
            ? workpiece.CutSphere(worldEnd, cutterRadius, false)
            : workpiece.CutSweptSphere(worldStart, worldEnd, cutterRadius, false);
    }
}

internal static class CuttingInput
{
    public static bool IsKeyHeld(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        if (IsNewInputKeyHeld(key))
        {
            return true;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKey(key);
#else
        return false;
#endif
    }

    public static bool WasKeyPressed(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        if (WasNewInputKeyPressed(key))
        {
            return true;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(key);
#else
        return false;
#endif
    }

    public static bool IsRightMouseHeld()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null && Mouse.current.rightButton.isPressed)
        {
            return true;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetMouseButton(1);
#else
        return false;
#endif
    }

    public static bool IsMiddleMouseHeld()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null && Mouse.current.middleButton.isPressed)
        {
            return true;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetMouseButton(2);
#else
        return false;
#endif
    }

    public static Vector2 MouseDelta()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            return Mouse.current.delta.ReadValue();
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#else
        return Vector2.zero;
#endif
    }

    public static float MouseScrollY()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            return NormalizeScroll(Mouse.current.scroll.ReadValue().y);
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.mouseScrollDelta.y;
#else
        return 0f;
#endif
    }

    private static float NormalizeScroll(float value)
    {
        return Mathf.Abs(value) > 10f ? value / 120f : value;
    }

#if ENABLE_INPUT_SYSTEM
    private static bool IsNewInputKeyHeld(KeyCode key)
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return false;
        }

        switch (key)
        {
            case KeyCode.W:
                return keyboard.wKey.isPressed;
            case KeyCode.A:
                return keyboard.aKey.isPressed;
            case KeyCode.S:
                return keyboard.sKey.isPressed;
            case KeyCode.D:
                return keyboard.dKey.isPressed;
            case KeyCode.Q:
                return keyboard.qKey.isPressed;
            case KeyCode.E:
                return keyboard.eKey.isPressed;
            case KeyCode.F:
                return keyboard.fKey.isPressed;
            case KeyCode.G:
                return keyboard.gKey.isPressed;
            case KeyCode.R:
                return keyboard.rKey.isPressed;
            case KeyCode.T:
                return keyboard.tKey.isPressed;
            case KeyCode.Space:
                return keyboard.spaceKey.isPressed;
            case KeyCode.LeftShift:
                return keyboard.leftShiftKey.isPressed;
            case KeyCode.RightShift:
                return keyboard.rightShiftKey.isPressed;
            default:
                return false;
        }
    }

    private static bool WasNewInputKeyPressed(KeyCode key)
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null)
        {
            return false;
        }

        switch (key)
        {
            case KeyCode.W:
                return keyboard.wKey.wasPressedThisFrame;
            case KeyCode.A:
                return keyboard.aKey.wasPressedThisFrame;
            case KeyCode.S:
                return keyboard.sKey.wasPressedThisFrame;
            case KeyCode.D:
                return keyboard.dKey.wasPressedThisFrame;
            case KeyCode.Q:
                return keyboard.qKey.wasPressedThisFrame;
            case KeyCode.E:
                return keyboard.eKey.wasPressedThisFrame;
            case KeyCode.F:
                return keyboard.fKey.wasPressedThisFrame;
            case KeyCode.G:
                return keyboard.gKey.wasPressedThisFrame;
            case KeyCode.R:
                return keyboard.rKey.wasPressedThisFrame;
            case KeyCode.T:
                return keyboard.tKey.wasPressedThisFrame;
            case KeyCode.Space:
                return keyboard.spaceKey.wasPressedThisFrame;
            case KeyCode.LeftShift:
                return keyboard.leftShiftKey.wasPressedThisFrame;
            case KeyCode.RightShift:
                return keyboard.rightShiftKey.wasPressedThisFrame;
            default:
                return false;
        }
    }
#endif
}
