using UnityEngine;

[DisallowMultipleComponent]
public sealed class SimpleCameraController : MonoBehaviour
{
    [Header("Target")]
    public Transform target;
    public Vector3 targetLocalOffset;
    public Vector3 pivotPoint = Vector3.zero;

    [Header("Orbit")]
    [Min(0.01f)] public float distance = 5f;
    [Min(0.1f)] public float minDistance = 0.5f;
    [Min(0.1f)] public float maxDistance = 20f;
    [Min(0.01f)] public float rotationSensitivity = 0.18f;
    public float minPitch = -85f;
    public float maxPitch = 85f;

    [Header("Pan")]
    [Min(0.001f)] public float mousePanSensitivity = 0.0025f;
    [Min(0.001f)] public float keyboardPanSpeed = 2.5f;
    public bool keyboardPanRequiresRightMouse = true;

    [Header("Zoom")]
    [Min(0.001f)] public float zoomStep = 0.35f;

    private Transform overviewTarget;
    private Vector3 overviewTargetLocalOffset;
    private Transform machiningTarget;
    private float overviewDistance;
    private float overviewMinDistance;
    private float overviewMaxDistance;
    private float overviewZoomStep;
    private float overviewKeyboardPanSpeed;
    private float overviewYaw;
    private float overviewPitch;
    private float machiningFocusDistance;
    private bool viewTargetsConfigured;
    private Vector3 pivotOffset;
    private float yaw;
    private float pitch;

    private void Start()
    {
        InitializeFromCurrentTransform();
    }

    private void LateUpdate()
    {
        UpdateViewShortcut();
        UpdateRotation();
        UpdateZoom();
        UpdatePan();
        ApplyCameraTransform();
    }

    public void FocusOn(Transform newTarget)
    {
        target = newTarget;
        pivotOffset = Vector3.zero;
        InitializeFromCurrentTransform();
    }

    public void ConfigureViewTargets(
        Transform fullWorkpieceTarget,
        Vector3 fullWorkpieceLocalOffset,
        Transform cutterTarget,
        float cutterFocusDistance)
    {
        target = fullWorkpieceTarget;
        targetLocalOffset = fullWorkpieceLocalOffset;
        pivotOffset = Vector3.zero;
        InitializeFromCurrentTransform();

        overviewTarget = fullWorkpieceTarget;
        overviewTargetLocalOffset = fullWorkpieceLocalOffset;
        machiningTarget = cutterTarget;
        machiningFocusDistance = Mathf.Max(minDistance, cutterFocusDistance);
        overviewDistance = distance;
        overviewMinDistance = minDistance;
        overviewMaxDistance = maxDistance;
        overviewZoomStep = zoomStep;
        overviewKeyboardPanSpeed = keyboardPanSpeed;
        overviewYaw = yaw;
        overviewPitch = pitch;
        viewTargetsConfigured = machiningTarget != null;
    }

    private void UpdateViewShortcut()
    {
        if (!viewTargetsConfigured)
        {
            return;
        }

        if (CuttingInput.WasKeyPressed(KeyCode.F))
        {
            target = machiningTarget;
            targetLocalOffset = Vector3.zero;
            pivotOffset = Vector3.zero;
            minDistance = Mathf.Max(0.001f, machiningFocusDistance * 0.1f);
            maxDistance = Mathf.Max(overviewMaxDistance, machiningFocusDistance * 20f);
            zoomStep = machiningFocusDistance * 0.25f;
            keyboardPanSpeed = machiningFocusDistance * 0.5f;
            distance = machiningFocusDistance;
            pitch = 55f;
        }
        else if (CuttingInput.WasKeyPressed(KeyCode.G))
        {
            target = overviewTarget;
            targetLocalOffset = overviewTargetLocalOffset;
            pivotOffset = Vector3.zero;
            minDistance = overviewMinDistance;
            maxDistance = overviewMaxDistance;
            zoomStep = overviewZoomStep;
            keyboardPanSpeed = overviewKeyboardPanSpeed;
            distance = overviewDistance;
            yaw = overviewYaw;
            pitch = overviewPitch;
        }
    }

    private void InitializeFromCurrentTransform()
    {
        Vector3 pivot = GetBasePivot();
        Vector3 toCamera = transform.position - pivot;

        if (toCamera.sqrMagnitude < 0.0001f)
        {
            toCamera = new Vector3(0f, 1.5f, -distance);
        }

        distance = Mathf.Clamp(toCamera.magnitude, minDistance, maxDistance);

        Quaternion lookRotation = Quaternion.LookRotation(-toCamera.normalized, Vector3.up);
        Vector3 euler = lookRotation.eulerAngles;
        yaw = euler.y;
        pitch = NormalizeAngle(euler.x);
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    }

    private void UpdateRotation()
    {
        if (!CuttingInput.IsRightMouseHeld())
        {
            return;
        }

        Vector2 mouseDelta = CuttingInput.MouseDelta();
        yaw += mouseDelta.x * rotationSensitivity;
        pitch -= mouseDelta.y * rotationSensitivity;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    }

    private void UpdateZoom()
    {
        float scroll = CuttingInput.MouseScrollY();
        if (Mathf.Abs(scroll) < 0.0001f)
        {
            return;
        }

        float adaptiveZoomStep = Mathf.Min(
            zoomStep,
            Mathf.Max(minDistance * 0.25f, distance * 0.12f));
        distance = Mathf.Clamp(distance - scroll * adaptiveZoomStep, minDistance, maxDistance);
    }

    private void UpdatePan()
    {
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 right = rotation * Vector3.right;
        Vector3 up = rotation * Vector3.up;
        Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up).normalized;

        if (CuttingInput.IsMiddleMouseHeld())
        {
            Vector2 mouseDelta = CuttingInput.MouseDelta();
            float scaledPan = Mathf.Max(0.1f, distance) * mousePanSensitivity;
            pivotOffset += (-right * mouseDelta.x - up * mouseDelta.y) * scaledPan;
        }

        bool keyboardPanAllowed = !keyboardPanRequiresRightMouse || CuttingInput.IsRightMouseHeld();
        if (!keyboardPanAllowed)
        {
            return;
        }

        Vector3 keyboardPan = Vector3.zero;

        if (CuttingInput.IsKeyHeld(KeyCode.A))
        {
            keyboardPan -= right;
        }

        if (CuttingInput.IsKeyHeld(KeyCode.D))
        {
            keyboardPan += right;
        }

        if (CuttingInput.IsKeyHeld(KeyCode.S))
        {
            keyboardPan -= forward;
        }

        if (CuttingInput.IsKeyHeld(KeyCode.W))
        {
            keyboardPan += forward;
        }

        if (keyboardPan.sqrMagnitude > 1f)
        {
            keyboardPan.Normalize();
        }

        pivotOffset += keyboardPan * keyboardPanSpeed * Time.deltaTime;
    }

    private void ApplyCameraTransform()
    {
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 pivot = GetBasePivot() + pivotOffset;

        transform.position = pivot - rotation * Vector3.forward * distance;
        transform.rotation = rotation;
    }

    private Vector3 GetBasePivot()
    {
        return target != null ? target.TransformPoint(targetLocalOffset) : pivotPoint;
    }

    private static float NormalizeAngle(float angle)
    {
        return angle > 180f ? angle - 360f : angle;
    }
}
