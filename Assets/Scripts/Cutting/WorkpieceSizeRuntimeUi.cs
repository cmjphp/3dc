using System.Globalization;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class WorkpieceSizeRuntimeUi : MonoBehaviour
{
    private const int WindowId = 731042;
    private readonly CultureInfo invariantCulture = CultureInfo.InvariantCulture;

    private CuttingDemoBootstrap bootstrap;
    private Rect windowRect = new Rect(16f, 16f, 300f, 680f);
    private string sizeX;
    private string sizeY;
    private string sizeZ;
    private string cutterDiameter;
    private string cutterHeight;
    private string moveSpeed;
    private string verticalSpeed;
    private string cutterDirectionX;
    private string cutterDirectionY;
    private string cutterDirectionZ;
    private string validationMessage;
    private int selectedCutterToolIndex;
    private string[] cutterToolNames = new string[0];
    private bool collapsed;

    public void Configure(CuttingDemoBootstrap owner)
    {
        bootstrap = owner;
        SyncFromBootstrap();
    }

    private void OnEnable()
    {
        if (bootstrap == null)
        {
            bootstrap = GetComponent<CuttingDemoBootstrap>();
        }
        SyncFromBootstrap();
    }

    private void OnGUI()
    {
        if (bootstrap == null)
        {
            return;
        }

        windowRect.height = collapsed ? 54f : 680f;
        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "加工参数");
    }

    private void DrawWindow(int windowId)
    {
        if (GUI.Button(new Rect(windowRect.width - 32f, 4f, 24f, 20f), collapsed ? "+" : "−"))
        {
            collapsed = !collapsed;
        }

        if (!collapsed)
        {
            GUI.Label(new Rect(14f, 30f, 270f, 20f), "工件尺寸 / mm（每轴 1–1000）");
            DrawValueField("X / 宽", ref sizeX, 58f);
            DrawValueField("Y / 高", ref sizeY, 88f);
            DrawValueField("Z / 深", ref sizeZ, 118f);

            if (GUI.Button(new Rect(14f, 151f, 272f, 30f), "应用并重建工件"))
            {
                ApplyRequestedSize();
            }

            GUI.Label(new Rect(14f, 194f, 270f, 20f), "刀具列表");
            DrawCutterToolList(222f);

            GUI.Label(new Rect(14f, 264f, 270f, 20f), "刀具参数 / mm、mm/s");
            DrawValueField("直径", ref cutterDiameter, 292f);
            DrawValueField("高度", ref cutterHeight, 322f);
            DrawValueField("水平速度", ref moveSpeed, 352f);
            DrawValueField("垂直速度", ref verticalSpeed, 382f);

            if (GUI.Button(new Rect(14f, 415f, 272f, 30f), "应用刀具参数"))
            {
                ApplyCutterParameters();
            }

            GUI.Label(new Rect(14f, 458f, 270f, 20f), "刀具方向 / 度（切削轴）");
            DrawValueField("X 俯仰", ref cutterDirectionX, 486f);
            DrawValueField("Y 偏航", ref cutterDirectionY, 516f);
            DrawValueField("Z 滚转", ref cutterDirectionZ, 546f);

            if (GUI.Button(new Rect(14f, 579f, 272f, 30f), "应用刀具方向"))
            {
                ApplyCutterDirection();
            }

            if (!string.IsNullOrEmpty(validationMessage))
            {
                GUI.Label(new Rect(14f, 619f, 272f, 50f), validationMessage);
            }
        }

        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 38f, 26f));
    }

    private void DrawValueField(string label, ref string value, float y)
    {
        GUI.Label(new Rect(14f, y, 86f, 24f), label);
        value = GUI.TextField(new Rect(104f, y, 182f, 24f), value ?? string.Empty);
    }

    private void DrawCutterToolList(float y)
    {
        cutterToolNames = bootstrap.GetCutterToolNames();
        if (cutterToolNames == null || cutterToolNames.Length == 0)
        {
            GUI.Label(new Rect(14f, y, 272f, 24f), "暂无刀具");
            return;
        }

        int safeIndex = Mathf.Clamp(selectedCutterToolIndex, 0, cutterToolNames.Length - 1);
        int nextIndex = GUI.Toolbar(new Rect(14f, y, 272f, 30f), safeIndex, cutterToolNames);
        if (nextIndex == safeIndex)
        {
            return;
        }

        bootstrap.SelectCutterTool(nextIndex);
        SyncFromBootstrap();
        validationMessage = $"已选择：{bootstrap.SelectedCutterToolName}";
    }

    private void ApplyRequestedSize()
    {
        if (!TryParseBounded(
                sizeX,
                CuttingDemoBootstrap.MinWorkpieceSizeMm,
                CuttingDemoBootstrap.MaxWorkpieceSizeMm,
                out float x) ||
            !TryParseBounded(
                sizeY,
                CuttingDemoBootstrap.MinWorkpieceSizeMm,
                CuttingDemoBootstrap.MaxWorkpieceSizeMm,
                out float y) ||
            !TryParseBounded(
                sizeZ,
                CuttingDemoBootstrap.MinWorkpieceSizeMm,
                CuttingDemoBootstrap.MaxWorkpieceSizeMm,
                out float z))
        {
            validationMessage = "请输入有效数字。";
            return;
        }

        Vector3 requested = new Vector3(x, y, z);
        bootstrap.SetWorkpieceSizeMm(requested);
        SyncFromBootstrap();
        validationMessage = $"已应用：{sizeX} × {sizeY} × {sizeZ} mm";
    }

    private void ApplyCutterParameters()
    {
        if (!TryParseBounded(
                cutterDiameter,
                CuttingDemoBootstrap.MinCutterSizeMm,
                CuttingDemoBootstrap.MaxCutterSizeMm,
                out float diameter) ||
            !TryParseBounded(
                cutterHeight,
                CuttingDemoBootstrap.MinCutterSizeMm,
                CuttingDemoBootstrap.MaxCutterSizeMm,
                out float height) ||
            !TryParseBounded(
                moveSpeed,
                CuttingDemoBootstrap.MinCutterSpeedMmPerSecond,
                CuttingDemoBootstrap.MaxCutterSpeedMmPerSecond,
                out float horizontal) ||
            !TryParseBounded(
                verticalSpeed,
                CuttingDemoBootstrap.MinCutterSpeedMmPerSecond,
                CuttingDemoBootstrap.MaxCutterSpeedMmPerSecond,
                out float vertical))
        {
            validationMessage = "请输入有效的刀具参数。";
            return;
        }

        bootstrap.SetCutterParameters(diameter, height, horizontal, vertical);
        SyncFromBootstrap();
        validationMessage =
            $"刀具：直径 {cutterDiameter}，高度 {cutterHeight} mm";
    }

    private void ApplyCutterDirection()
    {
        if (!TryParseBounded(
                cutterDirectionX,
                CuttingDemoBootstrap.MinCutterDirectionDegrees,
                CuttingDemoBootstrap.MaxCutterDirectionDegrees,
                out float x) ||
            !TryParseBounded(
                cutterDirectionY,
                CuttingDemoBootstrap.MinCutterDirectionDegrees,
                CuttingDemoBootstrap.MaxCutterDirectionDegrees,
                out float y) ||
            !TryParseBounded(
                cutterDirectionZ,
                CuttingDemoBootstrap.MinCutterDirectionDegrees,
                CuttingDemoBootstrap.MaxCutterDirectionDegrees,
                out float z))
        {
            validationMessage = "请输入有效的刀具方向。";
            return;
        }

        bootstrap.SetCutterDirectionEuler(new Vector3(x, y, z));
        SyncFromBootstrap();
        validationMessage =
            $"方向：X {cutterDirectionX}°，Y {cutterDirectionY}°，Z {cutterDirectionZ}°";
    }

    private static bool TryParseBounded(string text, float min, float max, out float value)
    {
        bool parsed = float.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
        if (!parsed)
        {
            parsed = float.TryParse(text, out value);
        }

        if (!parsed || float.IsNaN(value) || float.IsInfinity(value))
        {
            return false;
        }

        value = Mathf.Clamp(value, min, max);
        return true;
    }

    private void SyncFromBootstrap()
    {
        if (bootstrap == null)
        {
            return;
        }

        Vector3 size = bootstrap.workpieceSizeMm;
        sizeX = size.x.ToString("0.###", invariantCulture);
        sizeY = size.y.ToString("0.###", invariantCulture);
        sizeZ = size.z.ToString("0.###", invariantCulture);
        cutterDiameter = (bootstrap.cutterRadius * 2f).ToString("0.###", invariantCulture);
        cutterHeight = (bootstrap.cutterRadius * bootstrap.cutterHeightScale).ToString(
            "0.###",
            invariantCulture);
        moveSpeed = bootstrap.cutterMoveSpeedMmPerSecond.ToString("0.###", invariantCulture);
        verticalSpeed = bootstrap.cutterVerticalSpeedMmPerSecond.ToString("0.###", invariantCulture);
        Vector3 direction = bootstrap.cutterDirectionEuler;
        cutterDirectionX = direction.x.ToString("0.###", invariantCulture);
        cutterDirectionY = direction.y.ToString("0.###", invariantCulture);
        cutterDirectionZ = direction.z.ToString("0.###", invariantCulture);
        selectedCutterToolIndex = bootstrap.selectedCutterToolIndex;
        cutterToolNames = bootstrap.GetCutterToolNames();
    }
}
