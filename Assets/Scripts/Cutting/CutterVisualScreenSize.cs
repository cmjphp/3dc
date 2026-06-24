using UnityEngine;

[DisallowMultipleComponent]
public sealed class CutterVisualScreenSize : MonoBehaviour
{
    [Min(0.001f)] public float physicalDiameter = 0.7f;
    [Min(1f)] public float minimumPixelDiameter = 14f;
    public Transform visualRoot;

    private Vector3 baseLocalScale = Vector3.one;

    public void Configure(Transform model, float diameter, float minimumPixels)
    {
        visualRoot = model;
        physicalDiameter = Mathf.Max(0.001f, diameter);
        minimumPixelDiameter = Mathf.Max(1f, minimumPixels);
        baseLocalScale = visualRoot != null ? visualRoot.localScale : Vector3.one;
        enabled = visualRoot != null;
        ApplyScale();
    }

    private void LateUpdate()
    {
        ApplyScale();
    }

    private void OnDisable()
    {
        if (visualRoot != null)
        {
            visualRoot.localScale = baseLocalScale;
        }
    }

    private void ApplyScale()
    {
        Camera camera = Camera.main;
        if (visualRoot == null || camera == null || camera.pixelHeight <= 0)
        {
            return;
        }

        float worldUnitsPerPixel;
        if (camera.orthographic)
        {
            worldUnitsPerPixel = camera.orthographicSize * 2f / camera.pixelHeight;
        }
        else
        {
            float depth = Vector3.Dot(visualRoot.position - camera.transform.position, camera.transform.forward);
            if (depth <= camera.nearClipPlane)
            {
                visualRoot.localScale = baseLocalScale;
                return;
            }

            float verticalSpan = 2f * depth * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            worldUnitsPerPixel = verticalSpan / camera.pixelHeight;
        }

        float desiredDiameter = Mathf.Max(physicalDiameter, worldUnitsPerPixel * minimumPixelDiameter);
        float visualMultiplier = desiredDiameter / physicalDiameter;
        visualRoot.localScale = baseLocalScale * visualMultiplier;
    }
}
