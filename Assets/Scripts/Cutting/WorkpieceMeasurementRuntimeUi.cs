using System.Globalization;
using UnityEngine;

public enum WorkpieceMeasurementMode
{
    Surface,
    Thickness
}

[DisallowMultipleComponent]
public sealed class WorkpieceMeasurementRuntimeUi : MonoBehaviour
{
    private const int WindowId = 731043;

    public WorkpieceMeasurement measurement;
    public Camera measurementCamera;
    public bool measureOnLeftClick;
    public WorkpieceMeasurementMode mode = WorkpieceMeasurementMode.Thickness;

    private readonly CultureInfo invariantCulture = CultureInfo.InvariantCulture;
    private Rect windowRect = new Rect(328f, 16f, 290f, 250f);
    private bool collapsed;
    private string resultText = "无结果";
    private bool hasSurfaceHit;
    private bool hasThickness;
    private WorkpieceMeasurementHit lastSurfaceHit;
    private WorkpieceThicknessMeasurement lastThickness;

    public void Configure(WorkpieceMeasurement target)
    {
        measurement = target;
        if (measurementCamera == null)
        {
            measurementCamera = Camera.main;
        }
    }

    private void OnEnable()
    {
        ResolveReferences();
    }

    private void Update()
    {
        ResolveReferences();
        if (!measureOnLeftClick || measurement == null || measurementCamera == null)
        {
            DrawMeasurementDebugLines();
            return;
        }

        if (CuttingInput.WasLeftMousePressed() && !MouseInsideWindow())
        {
            MeasureMouseRay();
        }

        DrawMeasurementDebugLines();
    }

    private void OnGUI()
    {
        windowRect.height = collapsed ? 54f : 250f;
        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "测量");
    }

    private void DrawWindow(int windowId)
    {
        if (GUI.Button(new Rect(windowRect.width - 32f, 4f, 24f, 20f), collapsed ? "+" : "-"))
        {
            collapsed = !collapsed;
        }

        if (!collapsed)
        {
            int nextMode = GUI.Toolbar(
                new Rect(14f, 34f, 262f, 28f),
                mode == WorkpieceMeasurementMode.Surface ? 0 : 1,
                new[] { "表面", "厚度" });
            mode = nextMode == 0 ? WorkpieceMeasurementMode.Surface : WorkpieceMeasurementMode.Thickness;

            measureOnLeftClick = GUI.Toggle(
                new Rect(14f, 72f, 262f, 24f),
                measureOnLeftClick,
                "左键测量");

            if (GUI.Button(new Rect(14f, 104f, 126f, 30f), "测量鼠标射线"))
            {
                MeasureMouseRay();
            }

            if (GUI.Button(new Rect(150f, 104f, 126f, 30f), "清除"))
            {
                ClearResult();
            }

            GUI.Label(
                new Rect(14f, 146f, 262f, 88f),
                resultText);
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 38f, 26f));
    }

    private void MeasureMouseRay()
    {
        ResolveReferences();
        if (measurement == null || measurementCamera == null)
        {
            resultText = "测量组件未就绪";
            return;
        }

        Vector2 mouse = CuttingInput.MousePosition();
        Ray ray = measurementCamera.ScreenPointToRay(new Vector3(mouse.x, mouse.y, 0f));
        if (mode == WorkpieceMeasurementMode.Surface)
        {
            MeasureSurface(ray);
        }
        else
        {
            MeasureThickness(ray);
        }
    }

    private void MeasureSurface(Ray ray)
    {
        if (!measurement.TryMeasureSurfaceWorldRay(ray, out WorkpieceMeasurementHit hit))
        {
            resultText = "未命中";
            hasSurfaceHit = false;
            return;
        }

        lastSurfaceHit = hit;
        hasSurfaceHit = true;
        hasThickness = false;
        resultText =
            $"表面距离 {FormatMm(hit.localDistanceMm)} mm\n" +
            $"SDF {FormatMm(hit.signedDistanceMm)} mm\n" +
            $"局部点 {FormatVector(hit.localPoint)}";
    }

    private void MeasureThickness(Ray ray)
    {
        if (!measurement.TryMeasureThicknessWorldRay(ray, out WorkpieceThicknessMeasurement thickness))
        {
            resultText = "未命中";
            hasThickness = false;
            return;
        }

        lastThickness = thickness;
        hasThickness = true;
        hasSurfaceHit = false;
        resultText =
            $"厚度 {FormatMm(thickness.thicknessMm)} mm\n" +
            $"入口 {FormatVector(thickness.entryLocalPoint)}\n" +
            $"出口 {FormatVector(thickness.exitLocalPoint)}";
    }

    private void ClearResult()
    {
        hasSurfaceHit = false;
        hasThickness = false;
        resultText = "无结果";
    }

    private void ResolveReferences()
    {
        if (measurement == null)
        {
            measurement = Object.FindFirstObjectByType<WorkpieceMeasurement>();
        }

        if (measurementCamera == null)
        {
            measurementCamera = Camera.main;
        }
    }

    private bool MouseInsideWindow()
    {
        Vector2 mouse = CuttingInput.MousePosition();
        mouse.y = Screen.height - mouse.y;
        return windowRect.Contains(mouse);
    }

    private void DrawMeasurementDebugLines()
    {
        if (hasSurfaceHit)
        {
            Debug.DrawLine(
                lastSurfaceHit.worldPoint - Vector3.up * 0.4f,
                lastSurfaceHit.worldPoint + Vector3.up * 0.4f,
                Color.yellow);
            Debug.DrawLine(
                lastSurfaceHit.worldPoint - Vector3.right * 0.4f,
                lastSurfaceHit.worldPoint + Vector3.right * 0.4f,
                Color.yellow);
            Debug.DrawLine(
                lastSurfaceHit.worldPoint - Vector3.forward * 0.4f,
                lastSurfaceHit.worldPoint + Vector3.forward * 0.4f,
                Color.yellow);
        }

        if (hasThickness)
        {
            Debug.DrawLine(lastThickness.entryWorldPoint, lastThickness.exitWorldPoint, Color.cyan);
            Debug.DrawLine(
                lastThickness.entryWorldPoint - Vector3.up * 0.3f,
                lastThickness.entryWorldPoint + Vector3.up * 0.3f,
                Color.yellow);
            Debug.DrawLine(
                lastThickness.exitWorldPoint - Vector3.up * 0.3f,
                lastThickness.exitWorldPoint + Vector3.up * 0.3f,
                Color.red);
        }
    }

    private string FormatMm(float value)
    {
        return value.ToString("0.000", invariantCulture);
    }

    private string FormatVector(Vector3 value)
    {
        return
            $"{FormatMm(value.x)}, " +
            $"{FormatMm(value.y)}, " +
            $"{FormatMm(value.z)}";
    }
}
